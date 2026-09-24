import { act, cleanup, render } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import '../../../i18n/i18n.js';
import {
  clearTokenProvider,
  restoreInnerFetchForTests,
  setInnerFetchForTests,
  setTokenProvider,
} from '../../../api/client/index.js';
import { queryClient } from '../../../app/providers/queryClient.js';
import { useUploadStore } from '../uploadStore.js';
import type { PersistedUpload } from '../uploadStore.js';
import { useResumableUpload } from '../useResumableUpload.js';
import type { ResumableUpload, UploadEngineOptions } from '../useResumableUpload.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function errorEnvelope(code: string, message: string): Response {
  return jsonResponse({ error: { code, message, correlationId: 'corr-1', details: {} } }, 400);
}

interface DeferredPut {
  readonly part: number;
  cancelled: boolean;
  readonly release: () => void;
}

interface MockWorld {
  receivedParts: number[];
  partCount: number;
  uploadStatus: string;
  projectStatus: string;
  puts: { part: number; url: string }[];
  authorizes: number[];
  initiates: unknown[];
  aborts: number;
  completes: number;
  maxConcurrent: number;
  currentConcurrent: number;
  deferred: DeferredPut[];
  putMode: 'immediate' | 'deferred';
  failFirstPutForPart: number | null;
  failedOnce: boolean;
  abortedSignals: number;
}

let world: MockWorld;

function resetWorld(): void {
  world = {
    receivedParts: [],
    partCount: 0,
    uploadStatus: 'InProgress',
    projectStatus: 'MediaReady',
    puts: [],
    authorizes: [],
    initiates: [],
    aborts: 0,
    completes: 0,
    maxConcurrent: 0,
    currentConcurrent: 0,
    deferred: [],
    putMode: 'immediate',
    failFirstPutForPart: null,
    failedOnce: false,
    abortedSignals: 0,
  };
}

function abortError(): DOMException {
  return new DOMException('Aborted', 'AbortError');
}

function partOf(url: string): number {
  const tail = url.split('/').pop() ?? '';
  return Number.parseInt(tail, 10);
}

function urlOf(input: RequestInfo | URL): string {
  if (typeof input === 'string') {
    return input;
  }
  if (input instanceof URL) {
    return input.href;
  }
  return (input as Request).url;
}

function methodOf(input: RequestInfo | URL, init?: RequestInit): string {
  if (typeof input !== 'string' && !(input instanceof URL)) {
    const request = input as Request;
    if (typeof request.method === 'string' && request.method !== '') {
      return request.method;
    }
  }
  return init?.method ?? 'GET';
}

async function mockFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const url = urlOf(input);
  const method = methodOf(input, init);
  if (!url.includes('/api/v1/')) {
    const part = partOf(url);
    const signal = init?.signal ?? null;
    world.puts.push({ part, url });
    world.currentConcurrent += 1;
    world.maxConcurrent = Math.max(world.maxConcurrent, world.currentConcurrent);
    const finish = (): void => {
      world.currentConcurrent -= 1;
    };
    if (signal?.aborted === true) {
      world.abortedSignals += 1;
      finish();
      throw abortError();
    }
    if (world.failFirstPutForPart === part && !world.failedOnce) {
      world.failedOnce = true;
      finish();
      return new Response('expired', { status: 403 });
    }
    if (world.putMode === 'deferred') {      return new Promise<Response>((resolve, reject) => {
        const entry: DeferredPut = {
          part,
          cancelled: false,
          release: () => {
            if (entry.cancelled) {
              return;
            }
            if (!world.receivedParts.includes(part)) {
              world.receivedParts.push(part);
            }
            finish();
            resolve(new Response(null, { status: 200 }));
          },
        };
        world.deferred.push(entry);
        signal?.addEventListener(
          'abort',
          () => {
            entry.cancelled = true;
            world.abortedSignals += 1;
            finish();
            reject(abortError());
          },
          { once: true },
        );
      });
    }
    if (!world.receivedParts.includes(part)) {
      world.receivedParts.push(part);
    }
    // Yield while "in flight" so concurrent PUTs genuinely overlap (R5).
    await new Promise((resolve) => {
      setTimeout(resolve, 0);
    });
    finish();
    return new Response(null, { status: 200 });
  }

  const body = init?.body;
  const parsed = typeof body === 'string' && body !== '' ? (JSON.parse(body) as Record<string, unknown>) : {};

  if (url.includes('/uploads') && url.endsWith('/uploads') && method === 'POST') {
    world.initiates.push(parsed);
    world.partCount = typeof parsed['partCount'] === 'number' ? (parsed['partCount'] as number) : 0;
    world.uploadStatus = 'InProgress';
    return jsonResponse({ id: 'upl_1', status: 'InProgress', partUrls: [], receivedParts: [] }, 201);
  }
  if (url.includes('/uploads/upl_1/parts') && method === 'POST') {
    const partNumber = typeof parsed['partNumber'] === 'number' ? (parsed['partNumber'] as number) : 0;
    world.authorizes.push(partNumber);
    return jsonResponse({
      partNumber,
      url: `https://parts.example/${partNumber}`,
      expiresAt: '2024-01-15T12:15:00Z',
    });
  }
  if (url.includes('/uploads/upl_1/complete') && method === 'POST') {
    world.completes += 1;
    if (world.receivedParts.length < world.partCount) {
      return errorEnvelope('UPLOAD_INCOMPLETE', 'Upload is incomplete.');
    }
    world.uploadStatus = 'Completed';
    return jsonResponse({ id: 'upl_1', status: 'Completed', receivedParts: [...world.receivedParts] });
  }
  if (url.includes('/uploads/upl_1/abort') && method === 'POST') {
    world.aborts += 1;
    return jsonResponse({ id: 'upl_1', status: 'Aborted', receivedParts: [] });
  }
  if (url.includes('/uploads/upl_1') && method === 'GET') {
    return jsonResponse({ id: 'upl_1', status: world.uploadStatus, receivedParts: [...world.receivedParts] });
  }
  if (/\/api\/v1\/projects\/[^/]+$/.test(url) && method === 'GET') {
    return jsonResponse({
      id: 'prj_1',
      name: 'Pilot',
      status: world.projectStatus,
      settingsVersion: 1,
    });
  }
  return jsonResponse({});
}

function testFile(size: number, name = 'clip.mp4'): File {
  const bytes = new Uint8Array(size);
  for (let i = 0; i < size; i += 1) {
    bytes[i] = i % 251;
  }
  return new File([bytes], name, { type: 'video/mp4' });
}

let latest: ResumableUpload | undefined;

function Probe({ projectId, options }: { projectId: string; options?: UploadEngineOptions }) {
  const controls = useResumableUpload(projectId, 'es', options);
  latest = controls;
  return null;
}

function renderProbe(projectId = 'prj_1', options?: UploadEngineOptions): void {
  latest = undefined;
  render(<Probe projectId={projectId} options={options} />);
}

const TINY = { partSizeBytes: 4, pollIntervalMs: 5, maxValidationPolls: 12 };

function sessionSnapshot(): PersistedUpload | undefined {
  return useUploadStore.getState().sessions['prj_1'];
}

async function waitForPhase(phase: string): Promise<void> {
  for (let i = 0; i < 200; i += 1) {
    if (sessionSnapshot()?.phase === phase) {
      return;
    }
    await new Promise((resolve) => {
      setTimeout(resolve, 10);
    });
  }
  throw new Error(`Timed out waiting for phase ${phase}; now ${sessionSnapshot()?.phase}`);
}

beforeEach(() => {
  resetWorld();
  setInnerFetchForTests(mockFetch as typeof fetch);
  setTokenProvider(() => 'test-token');
  queryClient.clear();
  window.localStorage.clear();
  useUploadStore.getState().resetUploadsForTests();
  latest = undefined;
});

afterEach(() => {
  cleanup();
  restoreInnerFetchForTests();
  clearTokenProvider();
  queryClient.clear();
  window.localStorage.clear();
  useUploadStore.getState().resetUploadsForTests();
  vi.restoreAllMocks();
});

describe('full upload to ready', () => {
  it('uploads parts with concurrency cap 3, completes, and validates to ready', async () => {
    renderProbe('prj_1', TINY);
    await act(async () => {
      await latest?.start(testFile(25));
    });
    expect(sessionSnapshot()?.phase).toBe('ready');
    expect(sessionSnapshot()?.completedParts).toEqual([1, 2, 3, 4, 5, 6, 7]);
    expect(world.maxConcurrent).toBeLessThanOrEqual(3);
    expect(world.maxConcurrent).toBeGreaterThan(1);
    expect(world.completes).toBe(1);
  });

  it('sends the bundle initiate shape with part count', async () => {
    renderProbe('prj_1', TINY);
    await act(async () => {
      await latest?.start(testFile(9));
    });
    expect(world.initiates).toHaveLength(1);
    expect(world.initiates[0]).toMatchObject({ fileName: 'clip.mp4', sizeBytes: 9, partCount: 3 });
    expect(sessionSnapshot()?.phase).toBe('ready');
  });
});

describe('recovery from persisted metadata (R1)', () => {
  it('resume skips server-known parts without re-uploading them', async () => {
    renderProbe('prj_1', TINY);
    await act(async () => {
      await latest?.start(testFile(12));
    });
    expect(sessionSnapshot()?.phase).toBe('ready');
    const putsAfterFirst = world.puts.length;

    await act(async () => {
      await latest?.resume();
    });
    expect(world.puts.length).toBe(putsAfterFirst);
  });

  it('resume reconciles newly completed server parts before pumping', async () => {
    renderProbe('prj_1', TINY);
    const file = testFile(12);
    await act(async () => {
      await latest?.start(file);
    });
    expect(sessionSnapshot()?.phase).toBe('ready');

    world.receivedParts = [1];
    world.uploadStatus = 'InProgress';
    useUploadStore.getState().updateSession('prj_1', {
      phase: 'paused',
      completedParts: [],
      bytesUploaded: 0,
    });
    const putsBefore = world.puts.length;
    await act(async () => {
      await latest?.resume();
    });
    const retried = world.puts.slice(putsBefore).map((put) => put.part);
    expect(retried).not.toContain(1);
    expect(sessionSnapshot()?.completedParts).toEqual([1, 2, 3]);
  });
});

describe('pause and resume sequencing (R2)', () => {
  it('pause aborts in-flight PUTs and resume finishes without losing parts', async () => {
    world.putMode = 'deferred';
    renderProbe('prj_1', TINY);
    const started = latest?.start(testFile(25));
    for (let i = 0; i < 200 && world.puts.length < 3; i += 1) {
      await new Promise((resolve) => {
        setTimeout(resolve, 10);
      });
    }
    expect(world.puts.length).toBe(3);

    await act(async () => {
      latest?.pause();
    });
    expect(sessionSnapshot()?.phase).toBe('paused');
    expect(world.abortedSignals).toBe(3);
    const putsAtPause = world.puts.length;
    await new Promise((resolve) => {
      setTimeout(resolve, 50);
    });
    expect(world.puts.length).toBe(putsAtPause);
    await act(async () => {
      await started;
    });

    world.putMode = 'immediate';
    for (const entry of world.deferred) {
      entry.release();
    }
    await act(async () => {
      await latest?.resume();
    });
    await waitForPhase('ready');
    expect(sessionSnapshot()?.completedParts).toEqual([1, 2, 3, 4, 5, 6, 7]);
    const counts = new Map<number, number>();
    for (const put of world.puts) {
      counts.set(put.part, (counts.get(put.part) ?? 0) + 1);
    }
    expect(counts.get(1)).toBe(2);
    expect(counts.get(4)).toBe(1);
  });
});

describe('expired presigned URL recovery', () => {
  it('refreshes only the expired part URL and keeps completed parts', async () => {
    world.failFirstPutForPart = 1;
    renderProbe('prj_1', TINY);
    await act(async () => {
      await latest?.start(testFile(8));
    });
    expect(sessionSnapshot()?.phase).toBe('ready');
    expect(world.authorizes.filter((n) => n === 1)).toHaveLength(2);
    expect(sessionSnapshot()?.completedParts).toEqual([1, 2]);
  });
});

describe('client-side validation', () => {
  it('rejects zero-byte files before any request', async () => {
    renderProbe('prj_1', TINY);
    await act(async () => {
      await latest?.start(testFile(0));
    });
    expect(latest?.localError).toBe('empty');
    expect(world.initiates).toHaveLength(0);
    expect(world.puts).toHaveLength(0);
    expect(sessionSnapshot()).toBeUndefined();
  });
});

describe('fingerprint mismatch on reattach', () => {
  it('restarts with a notice when the disk file changed', async () => {
    renderProbe('prj_1', TINY);
    await act(async () => {
      await latest?.start(testFile(8, 'clip.mp4'));
    });
    expect(sessionSnapshot()?.phase).toBe('ready');

    world.receivedParts = [];
    world.uploadStatus = 'InProgress';
    useUploadStore.getState().updateSession('prj_1', { phase: 'paused', completedParts: [1] });
    await act(async () => {
      await latest?.reattach(testFile(9, 'clip.mp4'));
    });
    expect(latest?.fingerprintNotice).toBe(true);
    expect(world.aborts).toBe(1);
    expect(world.initiates).toHaveLength(2);
    await waitForPhase('ready');
  });
});

describe('cancel', () => {
  it('aborts server-side and clears local metadata', async () => {
    world.putMode = 'deferred';
    renderProbe('prj_1', TINY);
    const started = latest?.start(testFile(12));
    for (let i = 0; i < 200 && world.puts.length < 1; i += 1) {
      await new Promise((resolve) => {
        setTimeout(resolve, 10);
      });
    }
    await act(async () => {
      await latest?.cancel();
    });
    await act(async () => {
      await started;
    });
    expect(world.aborts).toBe(1);
    expect(sessionSnapshot()).toBeUndefined();
    expect(window.localStorage.getItem('dubbing.uploads.v1') ?? '').not.toContain('upl_1');
  });
});

describe('validation re-poll on mount', () => {
  it('resumes polling when returning during server validation (no stuck spinner)', async () => {
    useUploadStore.getState().upsertSession({
      projectId: 'prj_1',
      uploadId: 'upl_1',
      name: 'clip.mp4',
      size: 8,
      contentType: 'video/mp4',
      fingerprint: 'v1:x',
      language: 'es',
      partSize: 4,
      partCount: 2,
      completedParts: [1, 2],
      bytesUploaded: 8,
      phase: 'validating',
      autoPaused: false,
      idempotencyKey: 'key-1',
      updatedAt: '2024-01-15T12:00:00Z',
    });
    world.receivedParts = [1, 2];
    world.uploadStatus = 'Completed';
    world.projectStatus = 'MediaReady';
    renderProbe('prj_1', TINY);
    await waitForPhase('ready');
    expect(sessionSnapshot()?.phase).toBe('ready');
  });
});

describe('rejected mapping on complete', () => {
  it('maps DUPLICATE_MEDIA to the duplicate rejection', async () => {
    resetWorld();
    setInnerFetchForTests((async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = urlOf(input);
      const method = methodOf(input, init);
      if (!url.includes('/api/v1/')) {
        const part = partOf(url);
        if (!world.receivedParts.includes(part)) {
          world.receivedParts.push(part);
        }
        return new Response(null, { status: 200 });
      }
      if (url.includes('/uploads') && url.endsWith('/uploads') && method === 'POST') {
        world.partCount = 1;
        return jsonResponse({ id: 'upl_1', status: 'InProgress', receivedParts: [] }, 201);
      }
      if (url.includes('/parts') && method === 'POST') {
        return jsonResponse({ partNumber: 1, url: 'https://parts.example/1' });
      }
      if (url.includes('/complete') && method === 'POST') {
        return errorEnvelope('DUPLICATE_MEDIA', 'Already uploaded.');
      }
      if (/\/api\/v1\/projects\/[^/]+$/.test(url)) {
        return jsonResponse({ id: 'prj_1', name: 'Pilot', status: 'Uploading', settingsVersion: 1 });
      }
      return jsonResponse({});
    }) as typeof fetch);
    renderProbe('prj_1', TINY);
    await act(async () => {
      await latest?.start(testFile(3));
    });
    expect(sessionSnapshot()?.phase).toBe('rejected');
    expect(sessionSnapshot()?.rejectionReason).toBe('duplicate');
  });
});
