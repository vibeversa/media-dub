// Task 039B: upload-store state-matrix gap closure.
//
// Covers the sanitizer/load/persist branches in `uploadStore.ts` (malformed
// storage payloads fail closed to "no session", numeric coercion, optional
// error fields, storage-failure tolerance, missing-session guards) and the
// `fingerprint.ts` reader fallbacks (FileReader path, read errors, empty
// inputs). Fixtures are synthetic; no media bytes leave the assertions (R3).
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  UPLOADS_STORAGE_KEY,
  getSessionFile,
  loadPersistedSessions,
  sanitizeSession,
  setSessionFile,
  useUploadStore,
} from '../uploadStore.js';
import type { PersistedUpload } from '../uploadStore.js';
import { fingerprintFile, hashBytes, isSameFingerprint } from '../fingerprint.js';

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

beforeEach(() => {
  window.localStorage.clear();
  useUploadStore.getState().resetUploadsForTests();
});

afterEach(() => {
  window.localStorage.clear();
  useUploadStore.getState().resetUploadsForTests();
  vi.restoreAllMocks();
});

describe('loadSessions failure matrix (fail-closed, never crashes)', () => {
  it('ignores blank, malformed, and non-map payloads', () => {
    window.localStorage.setItem(UPLOADS_STORAGE_KEY, '');
    expect(loadPersistedSessions()).toEqual({});
    window.localStorage.setItem(UPLOADS_STORAGE_KEY, '{broken');
    expect(loadPersistedSessions()).toEqual({});
    window.localStorage.setItem(UPLOADS_STORAGE_KEY, JSON.stringify([1, 2]));
    expect(loadPersistedSessions()).toEqual({});
    window.localStorage.setItem(UPLOADS_STORAGE_KEY, JSON.stringify('nope'));
    expect(loadPersistedSessions()).toEqual({});
  });

  it('drops invalid entries but keeps valid sessions', () => {
    window.localStorage.setItem(
      UPLOADS_STORAGE_KEY,
      JSON.stringify({ bad: { nope: true }, prj_1: session() }),
    );
    const loaded = loadPersistedSessions();
    expect(loaded['bad']).toBeUndefined();
    expect(loaded['prj_1']?.uploadId).toBe('upl_1');
  });
});

describe('sanitizeSession coercion matrix', () => {
  it('clamps numbers and drops non-numeric values to fallbacks', () => {
    const cleaned = sanitizeSession({
      ...session(),
      size: -5,
      partSize: 0,
      partCount: Number.NaN,
      bytesUploaded: Number.POSITIVE_INFINITY,
      completedParts: [2, 'x', 2.5, -1, 1],
      autoPaused: 'yes',
    });
    expect(cleaned?.size).toBe(0);
    expect(cleaned?.partSize).toBe(1);
    expect(cleaned?.partCount).toBe(1);
    expect(cleaned?.bytesUploaded).toBe(0);
    expect(cleaned?.completedParts).toEqual([1]);
    expect(cleaned?.autoPaused).toBe(false);
  });

  it('keeps valid rejection, error, and correlation fields with length caps', () => {
    const cleaned = sanitizeSession({
      ...session(),
      rejectionReason: 'duplicate',
      errorCode: 'X'.repeat(100),
      errorMessage: 'Y'.repeat(600),
      correlationId: 'Z'.repeat(100),
      autoPaused: true,
    });
    expect(cleaned?.rejectionReason).toBe('duplicate');
    expect(cleaned?.errorCode?.length).toBe(64);
    expect(cleaned?.errorMessage?.length).toBe(500);
    expect(cleaned?.correlationId?.length).toBe(64);
    expect(cleaned?.autoPaused).toBe(true);
  });

  it('drops blank error fields and coerces non-strings to empty', () => {
    const cleaned = sanitizeSession({
      ...session(),
      name: 7,
      errorCode: '',
      errorMessage: '',
      correlationId: '',
    });
    expect(cleaned?.name).toBe('');
    expect(cleaned?.errorCode).toBeUndefined();
    expect(cleaned?.errorMessage).toBeUndefined();
    expect(cleaned?.correlationId).toBeUndefined();
  });

  it('rejects non-object and id-less payloads', () => {
    expect(sanitizeSession(42)).toBeNull();
    expect(sanitizeSession({ projectId: '', uploadId: 'u', phase: 'paused' })).toBeNull();
    expect(sanitizeSession({ projectId: 'p', uploadId: '', phase: 'paused' })).toBeNull();
  });
});

describe('store guard matrix', () => {
  it('ignores updates/removals for missing sessions (no crash)', () => {
    useUploadStore.getState().updateSession('ghost', { phase: 'paused' });
    expect(useUploadStore.getState().sessions['ghost']).toBeUndefined();
    useUploadStore.getState().removeSession('ghost');
    expect(useUploadStore.getState().sessions).toEqual({});
  });

  it('clears file handles with null and survives storage failures', () => {
    const file = new File([new Uint8Array([1])], 'clip.mp4', { type: 'video/mp4' });
    useUploadStore.getState().upsertSession(session());
    setSessionFile('prj_1', file);
    setSessionFile('prj_1', null);
    expect(getSessionFile('prj_1')).toBeNull();
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('quota exceeded');
    });
    useUploadStore.getState().upsertSession(session({ projectId: 'prj_2' }));
    expect(useUploadStore.getState().sessions['prj_2']?.uploadId).toBe('upl_1');
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => {
      throw new Error('denied');
    });
    useUploadStore.getState().resetUploadsForTests();
    expect(useUploadStore.getState().sessions).toEqual({});
  });
});

describe('fingerprint reader matrix', () => {
  it('hashes empty input deterministically', () => {
    expect(hashBytes(new Uint8Array([]))).toMatch(/^[0-9a-f]{8}$/);
    expect(isSameFingerprint('', 'v1:x')).toBe(false);
  });

  it('reads through FileReader when arrayBuffer is absent (jsdom path)', async () => {
    const bytes = new Uint8Array([5, 6, 7, 8]);
    const blob = new Blob([bytes], { type: 'video/mp4' });
    const withoutArrayBuffer = Object.create(Object.getPrototypeOf(blob), {
      ...Object.getOwnPropertyDescriptors(blob),
      arrayBuffer: { value: undefined, configurable: true },
    }) as Blob;
    const asFile = new File([withoutArrayBuffer], 'clip.mp4', { type: 'video/mp4' });
    Object.defineProperty(asFile, 'arrayBuffer', { value: undefined, configurable: true });
    const print = await fingerprintFile(asFile);
    expect(print.startsWith('v1:4:video/mp4:')).toBe(true);
  });

  it('rejects when the bytes cannot be read (recovery: pick another file)', async () => {
    const blob = new Blob([new Uint8Array([1])], { type: 'video/mp4' });
    const broken = new File([blob], 'clip.mp4', { type: 'video/mp4' });
    Object.defineProperty(broken, 'arrayBuffer', { value: undefined, configurable: true });
    const originalReader = globalThis.FileReader;
    class FailingReader {
      public onload: (() => void) | null = null;
      public onerror: (() => void) | null = null;
      public readonly error = new Error('READ_FAILED');
      public readonly result: ArrayBuffer | null = null;
      public readAsArrayBuffer(): void {
        queueMicrotask(() => this.onerror?.());
      }
    }
    vi.stubGlobal('FileReader', FailingReader);
    try {
      await expect(fingerprintFile(broken)).rejects.toThrow('READ_FAILED');
    } finally {
      vi.stubGlobal('FileReader', originalReader);
    }
  });
});
