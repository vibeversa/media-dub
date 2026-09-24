import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import {
  UPLOADS_STORAGE_KEY,
  getSessionFile,
  loadPersistedSessions,
  sanitizeSession,
  setSessionFile,
  useUploadStore,
} from '../uploadStore.js';
import type { PersistedUpload } from '../uploadStore.js';

function session(overrides: Partial<PersistedUpload> = {}): PersistedUpload {
  return {
    projectId: 'prj_1',
    uploadId: 'upl_1',
    name: 'clip.mp4',
    size: 25,
    contentType: 'video/mp4',
    fingerprint: 'v1:25:video/mp4:aa:bb',
    language: 'es',
    partSize: 4,
    partCount: 7,
    completedParts: [1, 2],
    bytesUploaded: 8,
    phase: 'uploading',
    autoPaused: false,
    idempotencyKey: 'key-1',
    updatedAt: '2024-01-15T12:00:00Z',
    ...overrides,
  };
}

function storedRaw(): string {
  return window.localStorage.getItem(UPLOADS_STORAGE_KEY) ?? '';
}

beforeEach(() => {
  window.localStorage.clear();
  useUploadStore.getState().resetUploadsForTests();
});

afterEach(() => {
  window.localStorage.clear();
  useUploadStore.getState().resetUploadsForTests();
});

describe('metadata persistence round-trip', () => {
  it('persists and reloads the session (simulated refresh)', () => {
    useUploadStore.getState().upsertSession(session());
    const reloaded = loadPersistedSessions();
    expect(reloaded['prj_1']?.uploadId).toBe('upl_1');
    expect(reloaded['prj_1']?.completedParts).toEqual([1, 2]);
    expect(reloaded['prj_1']?.phase).toBe('uploading');
  });

  it('updates and removes sessions', () => {
    useUploadStore.getState().upsertSession(session());
    useUploadStore.getState().updateSession('prj_1', { phase: 'paused', autoPaused: true });
    expect(useUploadStore.getState().sessions['prj_1']?.phase).toBe('paused');
    useUploadStore.getState().removeSession('prj_1');
    expect(useUploadStore.getState().sessions['prj_1']).toBeUndefined();
    expect(storedRaw()).not.toContain('upl_1');
  });
});

describe('no bytes in state (R3)', () => {
  it('never persists Blob, base64, or data URIs', () => {
    const file = new File([new Uint8Array([1, 2, 3, 4])], 'clip.mp4', { type: 'video/mp4' });
    useUploadStore.getState().upsertSession(session());
    setSessionFile('prj_1', file);
    expect(getSessionFile('prj_1')).toBe(file);

    const raw = storedRaw();
    expect(raw).not.toContain('blob:');
    expect(raw).not.toContain('data:');
    expect(raw).not.toContain('base64');
    const parsed = JSON.parse(raw) as Record<string, Record<string, unknown>>;
    const keys = Object.keys(parsed['prj_1'] ?? {}).sort();
    expect(keys).toEqual(
      [
        'autoPaused',
        'bytesUploaded',
        'completedParts',
        'contentType',
        'fingerprint',
        'idempotencyKey',
        'language',
        'name',
        'partCount',
        'partSize',
        'phase',
        'projectId',
        'size',
        'updatedAt',
        'uploadId',
      ].sort(),
    );
  });

  it('holds no File or Blob instances in zustand state', () => {
    useUploadStore.getState().upsertSession(session());
    const state = useUploadStore.getState().sessions;
    const blobs: unknown[] = [];
    const walk = (value: unknown): void => {
      if (value instanceof Blob) {
        blobs.push(value);
        return;
      }
      if (typeof value === 'object' && value !== null) {
        for (const entry of Object.values(value as Record<string, unknown>)) {
          walk(entry);
        }
      }
    };
    walk(state);
    expect(blobs).toEqual([]);
  });

  it('clears the file registry on remove and reset', () => {
    const file = new File([new Uint8Array([9])], 'clip.mp4', { type: 'video/mp4' });
    useUploadStore.getState().upsertSession(session());
    setSessionFile('prj_1', file);
    useUploadStore.getState().removeSession('prj_1');
    expect(getSessionFile('prj_1')).toBeNull();
  });
});

describe('sanitizeSession', () => {
  it('rejects payloads without project, upload, or phase', () => {
    expect(sanitizeSession(null)).toBeNull();
    expect(sanitizeSession({})).toBeNull();
    expect(sanitizeSession({ projectId: 'prj_1', uploadId: 'upl_1', phase: 'nope' })).toBeNull();
  });

  it('sorts and dedupes completed parts within range', () => {
    const cleaned = sanitizeSession({ ...session(), completedParts: [3, 1, 3, 99], partCount: 7 });
    expect(cleaned?.completedParts).toEqual([1, 3]);
  });

  it('drops invalid rejection reasons but keeps the session', () => {
    const cleaned = sanitizeSession({ ...session(), rejectionReason: 'bogus' });
    expect(cleaned?.projectId).toBe('prj_1');
    expect(cleaned?.rejectionReason).toBeUndefined();
  });

  it('ignores unknown stored schemas field-by-field', () => {
    window.localStorage.setItem(UPLOADS_STORAGE_KEY, JSON.stringify({ prj_1: { nope: true } }));
    expect(loadPersistedSessions()).toEqual({});
  });
});
