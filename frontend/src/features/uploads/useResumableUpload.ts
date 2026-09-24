import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiClient, newIdempotencyKey } from '../../api/client/index.js';
import { NETWORK_ERROR, normalizeError } from '../../api/errors/index.js';
import type { AppError } from '../../api/errors/index.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { queryClient } from '../../app/providers/queryClient.js';
import { fingerprintFile, isSameFingerprint } from './fingerprint.js';
import { getSessionFile, setSessionFile, useUploadStore } from './uploadStore.js';
import type { PersistedUpload } from './uploadStore.js';
import { deriveValidationOutcome, reasonFromErrorCode } from './uploadStates.js';

/**
 * Resumable multipart upload engine (Task 023).
 *
 * Orchestrates the bundle upload surface (`initiateUpload`,
 * `authorizeUploadPart`, `getUpload`, `completeUpload`, `abortUpload` from the
 * generated client — never hand-written shapes) with direct `PUT`s to the
 * presigned part URLs the server returns:
 *
 * - Parts are 8 MiB (mirrors `UploadService.PartSizeBytes`) with at most 3
 *   concurrent PUTs (R5). The backend requires no per-part checksums (S3
 *   ETags are authoritative on complete); the client hash exists only for
 *   duplicate detection and resume-identity (`fingerprint.ts`).
 * - Pause aborts every in-flight PUT via `AbortController` (R2); resume
 *   re-lists `receivedParts` server-side first so completed parts are never
 *   re-uploaded (R1). A 401/403 from a PUT refreshes just that part's URL and
 *   retries once, keeping completed parts.
 * - Network drops auto-pause with a resume prompt; zero-byte files are
 *   rejected client-side before any request; a re-attached file whose
 *   fingerprint mismatches restarts the upload with a notice.
 * - After `completeUpload`, `getUpload` + `getProject` are polled (bounded)
 *   through `validating` → `analyzing` → `ready` | `rejected`; a mount during
 *   those phases re-polls instead of sticking a spinner.
 * - Presigned URLs are used as-is and never logged, persisted, or sent to
 *   telemetry. `File`/`Blob` handles stay in the store file registry (memory
 *   only); zustand/`localStorage` hold metadata + progress numbers (R3).
 */

export const PART_SIZE_BYTES = 8 * 1024 * 1024;

export const MAX_PART_CONCURRENCY = 3;

const DEFAULT_POLL_INTERVAL_MS = 2000;

const DEFAULT_MAX_VALIDATION_POLLS = 30;

export interface UploadEngineOptions {
  /** Override part size (tests use tiny parts; production uses 8 MiB). */
  readonly partSizeBytes?: number;
  /** Delay between validation polls. */
  readonly pollIntervalMs?: number;
  /** Upper bound on validation polls before stopping with an error. */
  readonly maxValidationPolls?: number;
}

export type PartLiveStatus = 'uploading' | 'error';

export interface PartView {
  readonly partNumber: number;
  readonly status: 'pending' | 'uploading' | 'done' | 'error';
  /** Byte size of this part (last part may be smaller). */
  readonly bytes: number;
}

function delay(ms: number): Promise<void> {
  return new Promise<void>((resolve) => {
    setTimeout(resolve, ms);
  });
}

function isAbortError(error: unknown): boolean {
  return (
    (typeof error === 'object' && error !== null && (error as { name?: unknown }).name === 'AbortError') ||
    (error instanceof DOMException && error.name === 'AbortError')
  );
}

export interface ResumableUpload {
  readonly session: PersistedUpload | undefined;
  readonly parts: readonly PartView[];
  /** Persisted session exists but the bytes are gone (refresh) — prompt to re-attach. */
  readonly needsReattach: boolean;
  /** Zero-byte / unreadable-file message (no session created, no requests sent). */
  readonly localError: string | null;
  /** Set when a re-attached file mismatched and the upload restarted. */
  readonly fingerprintNotice: boolean;
  /** A ready session already holds this exact file (duplicate — offer use-existing). */
  readonly duplicateReady: PersistedUpload | null;
  readonly busy: boolean;
  readonly start: (file: File) => Promise<void>;
  readonly pause: () => void;
  readonly resume: () => Promise<void>;
  readonly cancel: () => Promise<void>;
  readonly retryPart: (partNumber: number) => Promise<void>;
  readonly reattach: (file: File) => Promise<void>;
  readonly clearNotices: () => void;
}

export function useResumableUpload(projectId: string, language: string, options?: UploadEngineOptions): ResumableUpload {
  const session = useUploadStore((s) => s.sessions[projectId]);
  const upsertSession = useUploadStore((s) => s.upsertSession);
  const updateSession = useUploadStore((s) => s.updateSession);
  const removeSession = useUploadStore((s) => s.removeSession);

  const optionsRef = useRef(options);
  optionsRef.current = options;

  const [live, setLive] = useState<Record<number, PartLiveStatus>>({});
  const [localError, setLocalError] = useState<string | null>(null);
  const [fingerprintNotice, setFingerprintNotice] = useState(false);
  const [duplicateReady, setDuplicateReady] = useState<PersistedUpload | null>(null);
  const [busy, setBusy] = useState(false);

  const generationRef = useRef(0);
  const pausedRef = useRef(false);
  const pumpingRef = useRef(false);
  const inflightRef = useRef(new Map<number, AbortController>());

  const partSizeOf = useCallback((): number => optionsRef.current?.partSizeBytes ?? PART_SIZE_BYTES, []);
  const pollIntervalOf = useCallback(
    (): number => optionsRef.current?.pollIntervalMs ?? DEFAULT_POLL_INTERVAL_MS,
    [],
  );
  const maxPollsOf = useCallback(
    (): number => optionsRef.current?.maxValidationPolls ?? DEFAULT_MAX_VALIDATION_POLLS,
    [],
  );

  const partCountOf = useCallback(
    (size: number): number => Math.max(1, Math.ceil(size / partSizeOf())),
    [partSizeOf],
  );

  const partBytesOf = useCallback(
    (size: number, partNumber: number): number => {
      const size_ = partSizeOf();
      return Math.max(0, Math.min(size_, size - (partNumber - 1) * size_));
    },
    [partSizeOf],
  );

  const readSession = useCallback((): PersistedUpload | undefined => {
    return useUploadStore.getState().sessions[projectId];
  }, [projectId]);

  const markPartLive = useCallback((partNumber: number, status: PartLiveStatus | null): void => {
    setLive((current) => {
      if (status === null) {
        if (current[partNumber] === undefined) {
          return current;
        }
        const next = { ...current };
        delete next[partNumber];
        return next;
      }
      return current[partNumber] === status ? current : { ...current, [partNumber]: status };
    });
  }, []);

  const failSession = useCallback(
    (error: AppError): void => {
      updateSession(projectId, {
        phase: 'error',
        autoPaused: false,
        errorCode: error.code,
        errorMessage: error.message,
        correlationId: error.correlationId,
        updatedAt: new Date().toISOString(),
      });
    },
    [projectId, updateSession],
  );

  const abortInflight = useCallback((): void => {
    for (const controller of inflightRef.current.values()) {
      try {
        controller.abort();
      } catch {
        // Aborting is best effort; workers observe the flag regardless.
      }
    }
    inflightRef.current.clear();
  }, []);

  const refreshCaches = useCallback((): void => {
    void queryClient.invalidateQueries({ queryKey: queryKeys.workspace.detail(projectId) });
    void queryClient.invalidateQueries({ queryKey: queryKeys.project.detail(projectId) });
  }, [projectId]);

  const putPart = useCallback(
    async (url: string, blob: Blob, signal: AbortSignal): Promise<void> => {
      const response = await globalThis.fetch(url, { method: 'PUT', body: blob, signal });
      if (response.ok) {
        return;
      }
      if (response.status === 401 || response.status === 403) {
        throw new Error('PART_URL_EXPIRED');
      }
      throw new Error(`PART_PUT_FAILED:${response.status}`);
    },
    [],
  );

  const authorizePart = useCallback(
    async (uploadId: string, partNumber: number, idempotencyKey: string): Promise<string> => {
      const auth = await apiClient.authorizeUploadPart(
        { path: { projectId, uploadId } },
        { partNumber },
        { idempotencyKey: `${idempotencyKey}:part-${partNumber}` },
      );
      const url = auth.url ?? '';
      if (url === '') {
        throw new Error('PART_URL_MISSING');
      }
      return url;
    },
    [projectId],
  );

  const reconcileParts = useCallback(async (): Promise<void> => {
    const current = readSession();
    if (current === undefined) {
      return;
    }
    const detail = await apiClient.getUpload({ path: { projectId, uploadId: current.uploadId } });
    const received = (detail.receivedParts ?? []).filter((n) => Number.isInteger(n) && n >= 1 && n <= current.partCount);
    const merged = [...new Set([...current.completedParts, ...received])].sort((a, b) => a - b);
    let bytes = 0;
    for (const part of merged) {
      bytes += partBytesOf(current.size, part);
    }
    updateSession(projectId, {
      completedParts: merged,
      bytesUploaded: bytes,
      updatedAt: new Date().toISOString(),
    });
  }, [partBytesOf, projectId, readSession, updateSession]);

  const uploadOnePart = useCallback(
    async (
      file: File,
      current: PersistedUpload,
      partNumber: number,
      generation: number,
    ): Promise<'done' | 'paused' | 'error' | 'error-network'> => {
      const controller = new AbortController();
      inflightRef.current.set(partNumber, controller);
      markPartLive(partNumber, 'uploading');
      try {
        const size_ = partSizeOf();
        const blob = file.slice((partNumber - 1) * size_, partNumber * size_);
        let url = await authorizePart(current.uploadId, partNumber, current.idempotencyKey);
        try {
          await putPart(url, blob, controller.signal);
        } catch (error) {
          if (isAbortError(error)) {
            return 'paused';
          }
          if (error instanceof Error && error.message === 'PART_URL_EXPIRED') {
            url = await authorizePart(current.uploadId, partNumber, current.idempotencyKey);
            await putPart(url, blob, controller.signal);
          } else {
            throw error;
          }
        }
        if (generation !== generationRef.current) {
          return 'paused';
        }
        const latest = readSession();
        if (latest === undefined) {
          return 'paused';
        }
        const merged = [...new Set([...latest.completedParts, partNumber])].sort((a, b) => a - b);
        let bytes = 0;
        for (const part of merged) {
          bytes += partBytesOf(latest.size, part);
        }
        updateSession(projectId, {
          completedParts: merged,
          bytesUploaded: bytes,
          updatedAt: new Date().toISOString(),
        });
        markPartLive(partNumber, null);
        return 'done';
      } catch (error) {
        if (isAbortError(error) || pausedRef.current || generation !== generationRef.current) {
          return 'paused';
        }
        if (error instanceof TypeError) {
          return 'error-network';
        }
        if (error instanceof Error && error.message.startsWith('PART_PUT_FAILED')) {
          markPartLive(partNumber, 'error');
          return 'error';
        }
        markPartLive(partNumber, 'error');
        return 'error';
      } finally {
        if (inflightRef.current.get(partNumber) === controller) {
          inflightRef.current.delete(partNumber);
        }
      }
    },
    [authorizePart, markPartLive, partSizeOf, partBytesOf, projectId, putPart, readSession, updateSession],
  );

  const pumpParts = useCallback(
    async (generation: number): Promise<'drained' | 'paused' | 'error'> => {
      if (pumpingRef.current) {
        return 'paused';
      }
      pumpingRef.current = true;
      try {
        for (;;) {
          if (pausedRef.current || generation !== generationRef.current) {
            return 'paused';
          }
          const current = readSession();
          const file = getSessionFile(projectId);
          if (current === undefined || file === null) {
            return 'paused';
          }
          const pending: number[] = [];
          for (let n = 1; n <= current.partCount; n += 1) {
            if (!current.completedParts.includes(n) && !inflightRef.current.has(n)) {
              pending.push(n);
            }
          }
          if (pending.length === 0 && inflightRef.current.size === 0) {
            return 'drained';
          }
          const batch = pending.slice(0, Math.max(0, MAX_PART_CONCURRENCY - inflightRef.current.size));
          if (batch.length === 0) {
            await delay(25);
            continue;
          }
          const outcomes = await Promise.all(batch.map((n) => uploadOnePart(file, current, n, generation)));
          if (outcomes.includes('error-network')) {
            return 'paused';
          }
          if (outcomes.includes('error')) {
            return 'error';
          }
        }
      } finally {
        pumpingRef.current = false;
      }
    },
    [projectId, readSession, uploadOnePart],
  );

  const pollValidation = useCallback(
    async (generation: number): Promise<void> => {
      const maxPolls = maxPollsOf();
      const interval = pollIntervalOf();
      for (let poll = 0; poll < maxPolls; poll += 1) {
        if (generation !== generationRef.current) {
          return;
        }
        const current = readSession();
        if (current === undefined) {
          return;
        }
        let uploadStatus = '';
        let projectStatus = '';
        try {
          const [upload, project] = await Promise.all([
            apiClient.getUpload({ path: { projectId, uploadId: current.uploadId } }),
            apiClient.getProject({ path: { projectId } }),
          ]);
          uploadStatus = upload.status ?? '';
          projectStatus = project.status ?? '';
        } catch (error) {
          const normalized = normalizeError(error, { method: 'GET' });
          if (normalized.code === NETWORK_ERROR) {
            pausedRef.current = true;
            updateSession(projectId, { phase: 'paused', autoPaused: true, updatedAt: new Date().toISOString() });
            return;
          }
          const reason = reasonFromErrorCode(normalized.code);
          updateSession(projectId, {
            phase: 'rejected',
            rejectionReason: reason,
            errorCode: normalized.code,
            errorMessage: normalized.message,
            correlationId: normalized.correlationId,
            updatedAt: new Date().toISOString(),
          });
          setLive({});
          return;
        }
        const outcome = deriveValidationOutcome({ uploadStatus, projectStatus }, poll);
        if (outcome.kind === 'ready') {
          updateSession(projectId, { phase: 'ready', autoPaused: false, updatedAt: new Date().toISOString() });
          setLive({});
          refreshCaches();
          return;
        }
        if (outcome.kind === 'rejected') {
          updateSession(projectId, {
            phase: 'rejected',
            rejectionReason: outcome.reason,
            autoPaused: false,
            updatedAt: new Date().toISOString(),
          });
          setLive({});
          refreshCaches();
          return;
        }
        if (outcome.kind === 'aborted') {
          updateSession(projectId, { phase: 'aborted', autoPaused: false, updatedAt: new Date().toISOString() });
          setLive({});
          return;
        }
        if (outcome.kind === 'validating' || outcome.kind === 'analyzing') {
          updateSession(projectId, { phase: outcome.kind, updatedAt: new Date().toISOString() });
        }
        await delay(interval);
      }
      const current = readSession();
      if (current !== undefined && generation === generationRef.current) {
        failSession({
          kind: 'Unknown',
          code: 'VALIDATION_TIMEOUT',
          message: 'Server validation is taking longer than expected. It is still safe to wait or check back later.',
          correlationId: current.correlationId ?? '',
          retryable: true,
          recoveryHint: 'Wait a moment, then reopen this page to check again.',
        });
      }
    },
    [failSession, maxPollsOf, pollIntervalOf, projectId, readSession, refreshCaches, updateSession],
  );

  const runToCompletion = useCallback(
    async (generation: number): Promise<void> => {
      const pumpResult = await pumpParts(generation);
      if (pumpResult !== 'drained' || generation !== generationRef.current) {
        if (pumpResult === 'paused' && generation === generationRef.current && pausedRef.current) {
          const latest = readSession();
          if (latest !== undefined && (latest.phase === 'uploading' || latest.phase === 'error')) {
            updateSession(projectId, { phase: 'paused', autoPaused: true, updatedAt: new Date().toISOString() });
          }
        }
        if (pumpResult === 'error' && generation === generationRef.current) {
          const latest = readSession();
          if (latest !== undefined && latest.phase === 'uploading') {
            failSession({
              kind: 'Unknown',
              code: 'PART_FAILED',
              message: 'A part failed to upload. Retry the failed part; completed parts are kept.',
              correlationId: latest.correlationId ?? '',
              retryable: true,
              recoveryHint: 'Retry the failed part; completed parts are kept.',
            });
          }
        }
        return;
      }
      const current = readSession();
      if (current === undefined) {
        return;
      }
      setBusy(true);
      try {
        await apiClient.completeUpload(
          { path: { projectId, uploadId: current.uploadId } },
          { idempotencyKey: `${current.idempotencyKey}:complete` },
        );
        updateSession(projectId, { phase: 'uploaded', autoPaused: false, updatedAt: new Date().toISOString() });
        setLive({});
        await pollValidation(generation);
      } catch (error) {
        const normalized = normalizeError(error, { method: 'POST' });
        if (normalized.code === NETWORK_ERROR) {
          pausedRef.current = true;
          updateSession(projectId, { phase: 'paused', autoPaused: true, updatedAt: new Date().toISOString() });
          return;
        }
        if (normalized.code === 'UPLOAD_INCOMPLETE') {
          try {
            await reconcileParts();
          } catch {
            // Reconcile is best effort; the next resume re-lists parts again.
          }
          updateSession(projectId, { phase: 'paused', autoPaused: false, updatedAt: new Date().toISOString() });
          return;
        }
        const reason = reasonFromErrorCode(normalized.code);
        if (normalized.code === 'DUPLICATE_MEDIA' || normalized.code === 'MEDIA_UNSUPPORTED' || normalized.code === 'MEDIA_CORRUPT') {
          updateSession(projectId, {
            phase: 'rejected',
            rejectionReason: reason,
            errorCode: normalized.code,
            errorMessage: normalized.message,
            correlationId: normalized.correlationId,
            updatedAt: new Date().toISOString(),
          });
          setLive({});
          refreshCaches();
          return;
        }
        failSession(normalized);
      } finally {
        if (generation === generationRef.current) {
          setBusy(false);
        }
      }
    },
    [failSession, pollValidation, projectId, pumpParts, readSession, reconcileParts, refreshCaches, updateSession],
  );

  const start = useCallback(
    async (file: File): Promise<void> => {
      setLocalError(null);
      setFingerprintNotice(false);
      setDuplicateReady(null);
      if (file.size < 1) {
        setLocalError('empty');
        return;
      }
      const generation = generationRef.current + 1;
      generationRef.current = generation;
      setBusy(true);
      try {
        const fingerprint = await fingerprintFile(file);
        const existing = readSession();
        if (existing !== undefined && existing.phase === 'ready' && isSameFingerprint(existing.fingerprint, fingerprint)) {
          setDuplicateReady(existing);
          setBusy(false);
          return;
        }
        if (existing !== undefined) {
          try {
            await apiClient.abortUpload(
              { path: { projectId, uploadId: existing.uploadId } },
              { idempotencyKey: `${existing.idempotencyKey}:abort` },
            );
          } catch {
            // Previous session cleanup is best effort (it may already be terminal).
          }
          removeSession(projectId);
        }
        const partCount = partCountOf(file.size);
        const idempotencyKey = newIdempotencyKey();
        const created = await apiClient.initiateUpload(
          { path: { projectId } },
          {
            fileName: file.name.slice(0, 256),
            contentType: file.type !== '' ? file.type : 'application/octet-stream',
            sizeBytes: file.size,
            partCount,
          },
          { idempotencyKey },
        );
        const received = (created.receivedParts ?? []).filter((n) => Number.isInteger(n) && n >= 1 && n <= partCount);
        let bytes = 0;
        for (const part of received) {
          bytes += partBytesOf(file.size, part);
        }
        upsertSession({
          projectId,
          uploadId: created.id,
          name: file.name.slice(0, 256),
          size: file.size,
          contentType: file.type !== '' ? file.type : 'application/octet-stream',
          fingerprint,
          language,
          partSize: partSizeOf(),
          partCount,
          completedParts: [...new Set(received)].sort((a, b) => a - b),
          bytesUploaded: bytes,
          phase: 'uploading',
          autoPaused: false,
          idempotencyKey,
          updatedAt: new Date().toISOString(),
        });
        setSessionFile(projectId, file);
        setLive({});
        pausedRef.current = false;
        await runToCompletion(generation);
      } catch (error) {
        if (generation !== generationRef.current) {
          return;
        }
        const normalized = normalizeError(error, { method: 'POST' });
        if (normalized.code === NETWORK_ERROR) {
          const current = readSession();
          if (current !== undefined) {
            pausedRef.current = true;
            updateSession(projectId, { phase: 'paused', autoPaused: true, updatedAt: new Date().toISOString() });
          } else {
            setLocalError('network');
          }
          return;
        }
        const reason = reasonFromErrorCode(normalized.code);
        const current = readSession();
        if (current !== undefined) {
          updateSession(projectId, {
            phase: normalized.code === 'DUPLICATE_MEDIA' ? 'rejected' : 'error',
            ...(normalized.code === 'DUPLICATE_MEDIA' ? { rejectionReason: reason } : {}),
            errorCode: normalized.code,
            errorMessage: normalized.message,
            correlationId: normalized.correlationId,
            updatedAt: new Date().toISOString(),
          });
        } else {
          // Initiate itself failed: no session exists, so surface the
          // server message as plain text (envelope messages are UI-safe).
          setLocalError(normalized.message);
        }
      } finally {
        if (generation === generationRef.current) {
          setBusy(false);
        }
      }
    },
    [language, partBytesOf, partCountOf, partSizeOf, projectId, readSession, removeSession, runToCompletion, updateSession, upsertSession],
  );

  const pause = useCallback((): void => {
    pausedRef.current = true;
    abortInflight();
    const current = readSession();
    if (current !== undefined && current.phase === 'uploading') {
      updateSession(projectId, { phase: 'paused', autoPaused: false, updatedAt: new Date().toISOString() });
    }
  }, [projectId, abortInflight, readSession, updateSession]);

  const resume = useCallback(async (): Promise<void> => {
    setLocalError(null);
    const file = getSessionFile(projectId);
    if (file === null) {
      return;
    }
    const current = readSession();
    if (current === undefined || current.phase === 'ready' || current.phase === 'rejected' || current.phase === 'aborted') {
      return;
    }
    if (current.phase === 'uploaded' || current.phase === 'validating' || current.phase === 'analyzing') {
      const generation = generationRef.current + 1;
      generationRef.current = generation;
      setBusy(true);
      try {
        await pollValidation(generation);
      } finally {
        if (generation === generationRef.current) {
          setBusy(false);
        }
      }
      return;
    }
    const generation = generationRef.current + 1;
    generationRef.current = generation;
    pausedRef.current = false;
    setBusy(true);
    try {
      try {
        await reconcileParts();
      } catch (error) {
        const normalized = normalizeError(error, { method: 'GET' });
        if (normalized.code === NETWORK_ERROR) {
          pausedRef.current = true;
          updateSession(projectId, { phase: 'paused', autoPaused: true, updatedAt: new Date().toISOString() });
          return;
        }
        throw error;
      }
      const reconciled = readSession();
      if (reconciled === undefined) {
        return;
      }
      updateSession(projectId, { phase: 'uploading', autoPaused: false, updatedAt: new Date().toISOString() });
      const pumpResult = await pumpParts(generation);
      if (generation !== generationRef.current || pausedRef.current) {
        const latest = readSession();
        if (latest !== undefined && latest.phase === 'uploading' && generation === generationRef.current) {
          updateSession(projectId, {
            phase: 'paused',
            autoPaused: pumpResult !== 'error',
            updatedAt: new Date().toISOString(),
          });
        }
        return;
      }
      if (pumpResult === 'drained') {
        await runToCompletion(generation);
      } else if (pumpResult === 'error') {
        const latest = readSession();
        if (latest !== undefined && latest.phase === 'uploading') {
          failSession({
            kind: 'Unknown',
            code: 'PART_FAILED',
            message: 'A part failed to upload. Retry the failed part; completed parts are kept.',
            correlationId: latest.correlationId ?? '',
            retryable: true,
            recoveryHint: 'Retry the failed part; completed parts are kept.',
          });
        }
      }
    } finally {
      if (generation === generationRef.current) {
        setBusy(false);
      }
    }
  }, [failSession, pollValidation, projectId, pumpParts, readSession, reconcileParts, runToCompletion, updateSession]);

  const cancel = useCallback(async (): Promise<void> => {
    generationRef.current += 1;
    pausedRef.current = true;
    abortInflight();
    setLive({});
    const current = readSession();
    if (current !== undefined) {
      try {
        await apiClient.abortUpload(
          { path: { projectId, uploadId: current.uploadId } },
          { idempotencyKey: `${current.idempotencyKey}:abort` },
        );
      } catch {
        // Abort is idempotent server-side; local cleanup proceeds regardless.
      }
      removeSession(projectId);
    }
    pausedRef.current = false;
    setBusy(false);
    setLocalError(null);
    setFingerprintNotice(false);
    setDuplicateReady(null);
  }, [abortInflight, projectId, readSession, removeSession]);

  const retryPart = useCallback(
    async (partNumber: number): Promise<void> => {
      const current = readSession();
      const file = getSessionFile(projectId);
      if (current === undefined || file === null) {
        return;
      }
      if (partNumber < 1 || partNumber > current.partCount) {
        return;
      }
      const generation = generationRef.current + 1;
      generationRef.current = generation;
      pausedRef.current = false;
      setBusy(true);
      try {
        const remaining = current.completedParts.filter((n) => n !== partNumber);
        let bytes = 0;
        for (const part of remaining) {
          bytes += partBytesOf(current.size, part);
        }
        updateSession(projectId, {
          completedParts: remaining,
          bytesUploaded: bytes,
          phase: 'uploading',
          autoPaused: false,
          errorCode: undefined,
          errorMessage: undefined,
          updatedAt: new Date().toISOString(),
        });
        const outcome = await uploadOnePart(file, { ...current, completedParts: remaining }, partNumber, generation);
        if (outcome === 'error-network') {
          pausedRef.current = true;
          updateSession(projectId, { phase: 'paused', autoPaused: true, updatedAt: new Date().toISOString() });
          return;
        }
        if (outcome === 'error') {
          failSession({
            kind: 'Unknown',
            code: 'PART_FAILED',
            message: `Part ${partNumber} failed to upload. Retry it when ready.`,
            correlationId: current.correlationId ?? '',
            retryable: true,
            recoveryHint: 'Retry the failed part; completed parts are kept.',
          });
          return;
        }
        if (outcome === 'done') {
          await runToCompletion(generation);
        }
      } finally {
        if (generation === generationRef.current) {
          setBusy(false);
        }
      }
    },
    [failSession, partBytesOf, projectId, readSession, runToCompletion, updateSession, uploadOnePart],
  );

  const reattach = useCallback(
    async (file: File): Promise<void> => {
      setLocalError(null);
      setFingerprintNotice(false);
      const current = readSession();
      if (current === undefined) {
        return;
      }
      if (file.size < 1) {
        setLocalError('empty');
        return;
      }
      const fingerprint = await fingerprintFile(file);
      if (!isSameFingerprint(current.fingerprint, fingerprint)) {
        await cancel();
        await start(file);
        setFingerprintNotice(true);
        return;
      }
      setSessionFile(projectId, file);
      if (current.phase === 'uploaded' || current.phase === 'validating' || current.phase === 'analyzing') {
        const generation = generationRef.current + 1;
        generationRef.current = generation;
        setBusy(true);
        try {
          await pollValidation(generation);
        } finally {
          if (generation === generationRef.current) {
            setBusy(false);
          }
        }
        return;
      }
      try {
        await reconcileParts();
      } catch {
        // Best effort; resume re-lists again.
      }
      updateSession(projectId, { phase: 'paused', autoPaused: false, updatedAt: new Date().toISOString() });
    },
    [cancel, pollValidation, projectId, readSession, reconcileParts, start, updateSession],
  );

  const clearNotices = useCallback((): void => {
    setLocalError(null);
    setFingerprintNotice(false);
    setDuplicateReady(null);
  }, []);

  useEffect(() => {
    const current = useUploadStore.getState().sessions[projectId];
    const inflight = inflightRef.current;
    if (current === undefined) {
      return () => {
        generationRef.current += 1;
        for (const controller of inflight.values()) {
          try {
            controller.abort();
          } catch {
            // Unmount cleanup is best effort.
          }
        }
        inflight.clear();
      };
    }
    if (current.phase === 'uploaded' || current.phase === 'validating' || current.phase === 'analyzing') {
      const generation = generationRef.current + 1;
      generationRef.current = generation;
      setBusy(true);
      void pollValidation(generation).finally(() => {
        if (generation === generationRef.current) {
          setBusy(false);
        }
      });
    }
    return () => {
      generationRef.current += 1;
      for (const controller of inflight.values()) {
        try {
          controller.abort();
        } catch {
          // Unmount cleanup is best effort.
        }
      }
      inflight.clear();
    };
  }, [projectId, pollValidation]);

  const parts = useMemo<readonly PartView[]>(() => {
    if (session === undefined) {
      return [];
    }
    const views: PartView[] = [];
    for (let n = 1; n <= session.partCount; n += 1) {
      const status = session.completedParts.includes(n) ? 'done' : (live[n] ?? 'pending');
      views.push({ partNumber: n, status, bytes: partBytesOf(session.size, n) });
    }
    return views;
  }, [live, partBytesOf, session]);

  const needsReattach = session !== undefined && getSessionFile(projectId) === null && (session.phase === 'uploading' || session.phase === 'paused');

  return {
    session,
    parts,
    needsReattach,
    localError,
    fingerprintNotice,
    duplicateReady,
    busy,
    start,
    pause,
    resume,
    cancel,
    retryPart,
    reattach,
    clearNotices,
  };
}
