import { create } from 'zustand';
import type { RejectionReason, UploadPhase } from './uploadStates.js';

/**
 * Upload session store (Task 023, R3).
 *
 * - One persisted session per project, keyed by project id. The persisted
 *   shape is metadata + progress numbers only
 *   (`projectId, uploadId, name, size, contentType, fingerprint, language,
 *   partSize, partCount, completedParts, bytesUploaded, phase, ...`); it is
 *   JSON-serializable by construction.
 * - `File`/`Blob` references live in the module-scoped `fileRegistry` (memory
 *   only) and are NEVER written to zustand state or `localStorage` — the
 *   no-bytes test walks both and fails on any `Blob` instance, `data:` URI,
 *   or `base64` marker. After a refresh the bytes are gone by design and the
 *   UI prompts to re-attach the file while completed parts stay intact (R1).
 * - Stored payloads are sanitized field-by-field on load; unknown schemas
 *   fall back to "no session" rather than crashing the uploader.
 */

export const UPLOADS_STORAGE_KEY = 'dubbing.uploads.v1';

export interface PersistedUpload {
  readonly projectId: string;
  readonly uploadId: string;
  readonly name: string;
  readonly size: number;
  readonly contentType: string;
  readonly fingerprint: string;
  readonly language: string;
  readonly partSize: number;
  readonly partCount: number;
  readonly completedParts: readonly number[];
  readonly bytesUploaded: number;
  readonly phase: UploadPhase;
  /** True when the pause came from a network drop (resume prompt differs). */
  readonly autoPaused: boolean;
  readonly rejectionReason?: RejectionReason;
  readonly errorCode?: string;
  readonly errorMessage?: string;
  readonly correlationId?: string;
  /** Opaque idempotency key for initiate/complete/abort (uuid, not a secret). */
  readonly idempotencyKey: string;
  readonly updatedAt: string;
}

interface UploadStoreState {
  readonly sessions: Record<string, PersistedUpload>;
  readonly upsertSession: (session: PersistedUpload) => void;
  readonly updateSession: (projectId: string, patch: Partial<PersistedUpload>) => void;
  readonly removeSession: (projectId: string) => void;
  /** Test-only reset (memory + storage + file registry). */
  readonly resetUploadsForTests: () => void;
}

/** In-memory file handles by project id. Never persisted, never serialized. */
const fileRegistry = new Map<string, File>();

/** Registers the in-memory file for a project (null clears it). */
export function setSessionFile(projectId: string, file: File | null): void {
  if (file === null) {
    fileRegistry.delete(projectId);
    return;
  }
  fileRegistry.set(projectId, file);
}

/** Returns the in-memory file, or null after a refresh until re-attached. */
export function getSessionFile(projectId: string): File | null {
  return fileRegistry.get(projectId) ?? null;
}

function clearFileRegistry(): void {
  fileRegistry.clear();
}

function isUploadPhase(value: unknown): value is UploadPhase {
  return (
    value === 'uploading' ||
    value === 'paused' ||
    value === 'uploaded' ||
    value === 'validating' ||
    value === 'analyzing' ||
    value === 'ready' ||
    value === 'rejected' ||
    value === 'aborted' ||
    value === 'error'
  );
}

function isRejectionReason(value: unknown): value is RejectionReason {
  return value === 'duplicate' || value === 'unsupported' || value === 'corrupt' || value === 'expired' || value === 'unknown';
}

function asString(value: unknown, max: number): string {
  return typeof value === 'string' ? value.slice(0, max) : '';
}

function asFiniteNumber(value: unknown, fallback: number): number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : fallback;
}

function asPartList(value: unknown): readonly number[] {
  if (!Array.isArray(value)) {
    return [];
  }
  const parts = value.filter((entry): entry is number => typeof entry === 'number' && Number.isInteger(entry) && entry >= 1);
  return [...new Set(parts)].sort((a, b) => a - b);
}

/** Field-by-field sanitizer; returns null when the payload is not a session. */
export function sanitizeSession(raw: unknown): PersistedUpload | null {
  if (typeof raw !== 'object' || raw === null) {
    return null;
  }
  const record = raw as Record<string, unknown>;
  const projectId = asString(record['projectId'], 128);
  const uploadId = asString(record['uploadId'], 128);
  const phase = record['phase'];
  if (projectId === '' || uploadId === '' || !isUploadPhase(phase)) {
    return null;
  }
  const partCount = Math.max(1, Math.floor(asFiniteNumber(record['partCount'], 1)));
  const completedParts = asPartList(record['completedParts']).filter((part) => part <= partCount);
  const rejectionRaw = record['rejectionReason'];
  return {
    projectId,
    uploadId,
    name: asString(record['name'], 256),
    size: asFiniteNumber(record['size'], 0),
    contentType: asString(record['contentType'], 256),
    fingerprint: asString(record['fingerprint'], 256),
    language: asString(record['language'], 8),
    partSize: Math.max(1, Math.floor(asFiniteNumber(record['partSize'], 1))),
    partCount,
    completedParts,
    bytesUploaded: asFiniteNumber(record['bytesUploaded'], 0),
    phase,
    autoPaused: record['autoPaused'] === true,
    ...(isRejectionReason(rejectionRaw) ? { rejectionReason: rejectionRaw } : {}),
    ...(typeof record['errorCode'] === 'string' && record['errorCode'] !== '' ? { errorCode: record['errorCode'].slice(0, 64) } : {}),
    ...(typeof record['errorMessage'] === 'string' && record['errorMessage'] !== '' ? { errorMessage: record['errorMessage'].slice(0, 500) } : {}),
    ...(typeof record['correlationId'] === 'string' && record['correlationId'] !== '' ? { correlationId: record['correlationId'].slice(0, 64) } : {}),
    idempotencyKey: asString(record['idempotencyKey'], 64),
    updatedAt: asString(record['updatedAt'], 64),
  };
}

function loadSessions(): Record<string, PersistedUpload> {
  try {
    const raw = window.localStorage.getItem(UPLOADS_STORAGE_KEY);
    if (raw === null || raw === '') {
      return {};
    }
    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
      return {};
    }
    const sessions: Record<string, PersistedUpload> = {};
    for (const [key, value] of Object.entries(parsed as Record<string, unknown>)) {
      const session = sanitizeSession(value);
      if (session !== null) {
        sessions[key] = session;
      }
    }
    return sessions;
  } catch {
    return {};
  }
}

function persist(sessions: Record<string, PersistedUpload>): void {
  try {
    window.localStorage.setItem(UPLOADS_STORAGE_KEY, JSON.stringify(sessions));
  } catch {
    // Storage full or unavailable: sessions stay in memory only.
  }
}

function unpersist(): void {
  try {
    window.localStorage.removeItem(UPLOADS_STORAGE_KEY);
  } catch {
    // Already gone or unavailable; memory state is still reset by callers.
  }
}

export const useUploadStore = create<UploadStoreState>()((set) => ({
  sessions: loadSessions(),
  upsertSession: (session) => {
    set((state) => {
      const next = { ...state.sessions, [session.projectId]: session };
      persist(next);
      return { sessions: next };
    });
  },
  updateSession: (projectId, patch) => {
    set((state) => {
      const current = state.sessions[projectId];
      if (current === undefined) {
        return state;
      }
      const next = { ...state.sessions, [projectId]: { ...current, ...patch } };
      persist(next);
      return { sessions: next };
    });
  },
  removeSession: (projectId) => {
    set((state) => {
      if (state.sessions[projectId] === undefined) {
        return state;
      }
      const next = { ...state.sessions };
      delete next[projectId];
      persist(next);
      return { sessions: next };
    });
    fileRegistry.delete(projectId);
  },
  resetUploadsForTests: () => {
    unpersist();
    set({ sessions: {} });
    clearFileRegistry();
  },
}));

/** Re-reads storage (simulates a page refresh for recovery tests). */
export function loadPersistedSessions(): Record<string, PersistedUpload> {
  return loadSessions();
}
