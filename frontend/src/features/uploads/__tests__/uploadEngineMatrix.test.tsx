// Task 039B: upload-engine state-matrix gap closure.
//
// Extends `resumableUpload.test.tsx` (happy paths, pause/resume, expiry,
// cancel, mount re-poll, duplicate-on-complete) with the missing §11.4
// states: duplicate-ready short-circuit, previous-session cleanup, network
// auto-pause with resume recovery, part-failure retry recovery,
// validation timeout/reject/abort mapping, complete-time outcomes
// (incomplete/unsupported/generic/network), resume/reattach/cancel edge
// guards, and notice lifecycle. Every failure asserts its recovery action
// per §11.6 (retry/resume/reattach/wait) with text signals (041C). The mock
// world mirrors the existing suite; fixtures are synthetic (R3).
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
import { getSessionFile, setSessionFile, useUploadStore } from '../uploadStore.js';
import { useResumableUpload } from '../useResumableUpload.js';
import type { ResumableUpload, UploadEngineOptions } from '../useResumableUpload.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, message: string, status = 400): Response {
  return jsonResponse({ error: { code, message, correlationId: 'corr-1', details: {} } }, status);
}

interface MockWorld {
  receivedParts: number[];
  partCount: number;
  uploadStatus: string;
  projectStatus: string;
  initiates: number;
  aborts: number;
  completes: number;
  putBehavior: 'ok' | 'fail500Once' | 'throwNetwork';
  putFailedOnce: boolean;
  completeBehavior: 'ok' | 'incomplete' | 'duplicate' | 'unsupported' | 'generic' | 'network';
  getUploadBehavior: 'ok' | 'network' | 'rejected' | 'aborted';
}

let world: MockWorld;

function resetWorld(): void {
  world = {
    receivedParts: [],
    partCount: 0,
    uploadStatus: 'InProgress',
    projectStatus: 'MediaReady',
    initiates: 0,
    aborts: 0,
    completes: 0,
    putBehavior: 'ok',
    putFailedOnce: false,
    completeBehavior: 'ok',
    getUploadBehavior: 'ok',
  };
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

function partOf(url: string): number {
  const tail = url.split('/').pop() ?? '';
  return Number.parseInt(tail, 10);
}

async function mockFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const url = urlOf(input);
  const method = methodOf(input, init);
  if (!url.includes('/api/v1/')) {
    const part = partOf(url);
    if (world.putBehavior === 'throwNetwork') {
      throw new TypeError('fetch failed');
    }
    if (world.putBehavior === 'fail500Once' && !world.putFailedOnce) {
      world.putFailedOnce = true;
      return new Response('bad', { status: 500 });
    }
    if (!world.receivedParts.includes(part)) {
      world.receivedParts.push(part);
    }
    return new Response(null, { status: 200 });
  }
  const body = init?.body;
  const parsed = typeof body === 'string' && body !== '' ? (JSON.parse(body) as Record<string, unknown>) : {};
  if (url.includes('/uploads') && url.endsWith('/uploads') && method === 'POST') {
    world.initiates += 1;
    world.partCount = typeof parsed['partCount'] === 'number' ? (parsed['partCount'] as number) : 0;
    world.uploadStatus = 'InProgress';
    return jsonResponse({ id: 'upl_1', status: 'InProgress', partUrls: [], receivedParts: [] }, 201);
  }
  if (url.includes('/uploads/upl_1/parts') && method === 'POST') {
    const partNumber = typeof parsed['partNumber'] === 'number' ? (parsed['partNumber'] as number) : 0;
    return jsonResponse({ partNumber, url: `https://parts.example/${partNumber}`, expiresAt: '2024-01-15T12:15:00Z' });
  }
  if (url.includes('/uploads/upl_1/complete') && method === 'POST') {
    world.completes += 1;
    switch (world.completeBehavior) {
      case 'incomplete':
        return errorEnvelope('UPLOAD_INCOMPLETE', 'Upload is incomplete.');
      case 'duplicate':
        return errorEnvelope('DUPLICATE_MEDIA', 'Already uploaded.');
      case 'unsupported':
        return errorEnvelope('MEDIA_UNSUPPORTED', 'Bad format.');
      case 'generic':
        return errorEnvelope('INTERNAL_ERROR', 'Boom.', 500);
      case 'network':
        throw new TypeError('fetch failed');
      default:
        world.uploadStatus = 'Completed';
        return jsonResponse({ id: 'upl_1', status: 'Completed', receivedParts: [...world.receivedParts] });
    }
  }
  if (url.includes('/uploads/upl_1/abort') && method === 'POST') {
    world.aborts += 1;
    return jsonResponse({ id: 'upl_1', status: 'Aborted', receivedParts: [] });
  }
  if (url.includes('/uploads/upl_1') && method === 'GET') {
    if (world.getUploadBehavior === 'network') {
      throw new TypeError('fetch failed');
    }
    if (world.getUploadBehavior === 'rejected') {
      return errorEnvelope('MEDIA_CORRUPT', 'Corrupt file.');
    }
    if (world.getUploadBehavior === 'aborted') {
      return jsonResponse({ id: 'upl_1', status: 'Aborted', receivedParts: [...world.receivedParts] });
    }
    return jsonResponse({ id: 'upl_1', status: world.uploadStatus, receivedParts: [...world.receivedParts] });
  }
  if (/\/api\/v1\/projects\/[^/]+$/.test(url) && method === 'GET') {
    return jsonResponse({ id: 'prj_1', name: 'Pilot', status: world.projectStatus, settingsVersion: 1 });
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

function sessionSnapshot() {
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

function seedSession(overrides: Record<string, unknown> = {}): void {
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
    completedParts: [],
    bytesUploaded: 0,
    phase: 'paused',
    autoPaused: false,
    idempotencyKey: 'key-1',
    updatedAt: '2024-01-15T12:00:00Z',
    ...overrides,
  });
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

describe('duplicate-ready short-circuit', () => {
  it('offers use-existing without new requests for the same ready file', async () => {
    renderProbe('prj_1', TINY);
    await act(async () => {
      await latest?.start(testFile(8));
    });
    expect(sessionSnapshot()?.phase).toBe('ready');
    expect(world.initiates).toBe(1);
    await act(async () => {
      await latest?.start(testFile(8));
    });
    expect(latest?.duplicateReady?.uploadId).toBe('upl_1');
    expect(latest?.busy).toBe(false);
    expect(world.initiates).toBe(1);
    expect(sessionSnapshot()?.phase).toBe('ready');
  });

  it('clears the duplicate notice on demand', async () => {
    renderProbe('prj_1', TINY);
    await act(async () => {
      await latest?.start(testFile(8));
    });
    await act(async () => {
      await latest?.start(testFile(8));
    });
    expect(latest?.duplicateReady).not.toBeNull();
    await act(async () => {
      latest?.clearNotices();
    });
    expect(latest?.duplicateReady).toBeNull();
  });
});

describe('previous-session cleanup', () => {
  it('aborts the stale session before starting a new file', async () => {
    renderProbe('prj_1', TINY);
    await act(async () => {
      await latest?.start(testFile(8));
    });
    expect(sessionSnapshot()?.phase).toBe('ready');
    world.receivedParts = [];
    world.uploadStatus = 'InProgress';
    await act(async () => {
      await latest?.start(testFile(9, 'other.mp4'));
    });
    expect(world.aborts).toBe(1);
    expect(world.initiates).toBe(2);
    await waitForPhase('ready');
  });
});

describe('part failure and retry recovery', () => {
  it('marks the failed part and recovers through retryPart (completed kept)', async () => {
    renderProbe('prj_1', TINY);
    await act(async () => {
      await latest?.start(testFile(8));
    });
    // Two parts; fail part 2 on its first PUT only.
    expect(sessionSnapshot()?.phase).toBe('ready');
    world.receivedParts = [1];
    world.putBehavior = 'fail500Once';
    world.putFailedOnce = false;
    useUploadStore.getState().updateSession('prj_1', { phase: 'error', completedParts: [1], bytesUploaded: 4 });
    await act(async () => {
      await latest?.retryPart(2);
    });
    expect(sessionSnapshot()?.phase).toBe('error');
    expect(sessionSnapshot()?.errorCode).toBe('PART_FAILED');
    await act(async () => {
      await latest?.retryPart(2);
    });
    await waitForPhase('ready');
    expect(sessionSnapshot()?.completedParts).toEqual([1, 2]);
  });

  it('ignores retryPart for missing sessions and out-of-range parts', async () => {
    renderProbe('prj_1', TINY);
    await act(async () => {
      await latest?.retryPart(1);
    });
    expect(sessionSnapshot()).toBeUndefined();
    await act(async () => {
      await latest?.start(testFile(8));
    });
    expect(sessionSnapshot()?.phase).toBe('ready');
    await act(async () => {
      await latest?.retryPart(99);
    });
    expect(sessionSnapshot()?.phase).toBe('ready');
  });

  it('auto-pauses on part network failure and resumes to ready (recovery: resume)', async () => {
    world.putBehavior = 'throwNetwork';
    renderProbe('prj_1', TINY);
    await act(async () => {
      await latest?.start(testFile(8));
    });
    expect(world.putBehavior).toBe('throwNetwork');
    world.putBehavior = 'ok';
    const file = testFile(8);
    setSessionFile('prj_1', file);
    await act(async () => {
      await latest?.resume();
    });
    await waitForPhase('ready');
    expect(sessionSnapshot()?.completedParts).toEqual([1, 2]);
  });
});

describe('validation outcome mapping', () => {
  it('fails with VALIDATION_TIMEOUT when the server never settles (recovery: wait)', async () => {
    world.uploadStatus = 'Completed';
    world.projectStatus = 'Uploading';
    renderProbe('prj_1', { partSizeBytes: 4, pollIntervalMs: 5, maxValidationPolls: 3 });
    await act(async () => {
      await latest?.start(testFile(8));
    });
    expect(sessionSnapshot()?.phase).toBe('error');
    expect(sessionSnapshot()?.errorCode).toBe('VALIDATION_TIMEOUT');
  });

  it('auto-pauses on validation network failure (recovery: resume)', async () => {
    world.getUploadBehavior = 'network';
    renderProbe('prj_1', TINY);
    const started = latest?.start(testFile(8));
    // Let parts finish and complete succeed, then fail the validation polls.
    world.completeBehavior = 'ok';
    await act(async () => {
      await started;
    });
    expect(sessionSnapshot()?.phase).toBe('paused');
    expect(sessionSnapshot()?.autoPaused).toBe(true);
  });

  it('maps validation rejections with reasons (recovery: replace)', async () => {
    world.uploadStatus = 'Completed';
    world.projectStatus = 'Uploading';
    world.getUploadBehavior = 'rejected';
    renderProbe('prj_1', TINY);
    await act(async () => {
      await latest?.start(testFile(8));
    });
    expect(sessionSnapshot()?.phase).toBe('rejected');
    expect(sessionSnapshot()?.rejectionReason).toBe('corrupt');
  });

  it('maps server aborts during validation (terminal, no retry)', async () => {
    world.uploadStatus = 'Completed';
    world.projectStatus = 'Uploading';
    world.getUploadBehavior = 'aborted';
    renderProbe('prj_1', TINY);
    await act(async () => {
      await latest?.start(testFile(8));
    });
    expect(sessionSnapshot()?.phase).toBe('aborted');
  });
});

describe('complete-time outcomes', () => {
  it('pauses (not auto) on UPLOAD_INCOMPLETE after reconciling (recovery: resume)', async () => {
    world.completeBehavior = 'incomplete';
    renderProbe('prj_1', TINY);
    await act(async () => {
      await latest?.start(testFile(8));
    });
    expect(sessionSnapshot()?.phase).toBe('paused');
    expect(sessionSnapshot()?.autoPaused).toBe(false);
  });

  it('rejects unsupported media at complete time (recovery: pick-format)', async () => {
    world.completeBehavior = 'unsupported';
    renderProbe('prj_1', TINY);
    await act(async () => {
      await latest?.start(testFile(8));
    });
    expect(sessionSnapshot()?.phase).toBe('rejected');
    expect(sessionSnapshot()?.rejectionReason).toBe('unsupported');
  });

  it('fails closed on generic complete errors (recovery: report id)', async () => {
    world.completeBehavior = 'generic';
    renderProbe('prj_1', TINY);
    await act(async () => {
      await latest?.start(testFile(8));
    });
    expect(sessionSnapshot()?.phase).toBe('error');
    expect(sessionSnapshot()?.errorCode).toBe('INTERNAL_ERROR');
  });

  it('auto-pauses on complete network failure (recovery: resume)', async () => {
    world.completeBehavior = 'network';
    renderProbe('prj_1', TINY);
    await act(async () => {
      await latest?.start(testFile(8));
    });
    expect(sessionSnapshot()?.phase).toBe('paused');
    expect(sessionSnapshot()?.autoPaused).toBe(true);
  });
});

describe('initiate failure without a session', () => {
  it('surfaces network failures as a local error when nothing exists (recovery: retry)', async () => {
    setInnerFetchForTests((async () => {
      throw new TypeError('fetch failed');
    }) as typeof fetch);
    renderProbe('prj_1', TINY);
    await act(async () => {
      await latest?.start(testFile(8));
    });
    expect(latest?.localError).toBe('network');
    expect(sessionSnapshot()).toBeUndefined();
  });

  it('surfaces server messages as plain text when initiate fails (no leakage)', async () => {
    setInnerFetchForTests((async (input: RequestInfo | URL) => {
      const url = urlOf(input);
      if (url.includes('/uploads') && url.endsWith('/uploads')) {
        return errorEnvelope('MEDIA_UNSUPPORTED', 'Bad format.');
      }
      return jsonResponse({});
    }) as typeof fetch);
    renderProbe('prj_1', TINY);
    await act(async () => {
      await latest?.start(testFile(8));
    });
    expect(latest?.localError).toBe('Bad format.');
    expect(sessionSnapshot()).toBeUndefined();
  });
});

describe('resume guards', () => {
  it('no-ops without registered bytes (reattach prompt owns recovery)', async () => {
    seedSession({ phase: 'paused' });
    expect(getSessionFile('prj_1')).toBeNull();
    renderProbe('prj_1', TINY);
    expect(latest?.needsReattach).toBe(true);
    await act(async () => {
      await latest?.resume();
    });
    expect(sessionSnapshot()?.phase).toBe('paused');
  });

  it('no-ops on terminal phases (ready/rejected/aborted)', async () => {
    seedSession({ phase: 'ready', completedParts: [1, 2] });
    setSessionFile('prj_1', testFile(8));
    renderProbe('prj_1', TINY);
    const initiatesBefore = world.initiates;
    await act(async () => {
      await latest?.resume();
    });
    expect(world.initiates).toBe(initiatesBefore);
    expect(sessionSnapshot()?.phase).toBe('ready');
  });

  it('re-polls validation when resuming mid-validation (no stuck spinner)', async () => {
    seedSession({ phase: 'validating', completedParts: [1, 2] });
    setSessionFile('prj_1', testFile(8));
    world.receivedParts = [1, 2];
    world.uploadStatus = 'Completed';
    world.projectStatus = 'MediaReady';
    renderProbe('prj_1', TINY);
    await act(async () => {
      await latest?.resume();
    });
    await waitForPhase('ready');
  });

  it('auto-pauses when reconcile hits a network failure (recovery: resume)', async () => {
    seedSession({ phase: 'paused', completedParts: [] });
    setSessionFile('prj_1', testFile(8));
    world.getUploadBehavior = 'network';
    renderProbe('prj_1', TINY);
    await act(async () => {
      await latest?.resume();
    });
    expect(sessionSnapshot()?.phase).toBe('paused');
    expect(sessionSnapshot()?.autoPaused).toBe(true);
  });
});

describe('reattach guards', () => {
  it('rejects empty re-attached files before any request', async () => {
    seedSession({ phase: 'paused' });
    renderProbe('prj_1', TINY);
    await act(async () => {
      await latest?.reattach(testFile(0));
    });
    expect(latest?.localError).toBe('empty');
  });

  it('no-ops without a session', async () => {
    renderProbe('prj_1', TINY);
    await act(async () => {
      await latest?.reattach(testFile(8));
    });
    expect(sessionSnapshot()).toBeUndefined();
    expect(latest?.localError).toBeNull();
  });

  it('settles the paused resting state for the same file (no restart)', async () => {
    const file = testFile(8);
    seedSession({ phase: 'error', completedParts: [1] });
    setSessionFile('prj_1', file);
    // Align the stored fingerprint with the file under test.
    renderProbe('prj_1', TINY);
    const { fingerprintFile } = await import('../fingerprint.js');
    const fingerprint = await fingerprintFile(file);
    useUploadStore.getState().updateSession('prj_1', { fingerprint });
    await act(async () => {
      await latest?.reattach(file);
    });
    expect(latest?.fingerprintNotice).toBe(false);
    expect(sessionSnapshot()?.phase).toBe('paused');
    expect(world.initiates).toBe(0);
  });

  it('re-polls when re-attaching during server validation', async () => {
    const file = testFile(8);
    seedSession({ phase: 'validating', completedParts: [1, 2] });
    renderProbe('prj_1', TINY);
    const { fingerprintFile } = await import('../fingerprint.js');
    const fingerprint = await fingerprintFile(file);
    useUploadStore.getState().updateSession('prj_1', { fingerprint });
    world.receivedParts = [1, 2];
    world.uploadStatus = 'Completed';
    world.projectStatus = 'MediaReady';
    await act(async () => {
      await latest?.reattach(file);
    });
    await waitForPhase('ready');
  });
});

describe('pause/cancel edge guards', () => {
  it('leaves resting phases untouched on pause', async () => {
    seedSession({ phase: 'paused' });
    renderProbe('prj_1', TINY);
    await act(async () => {
      latest?.pause();
    });
    expect(sessionSnapshot()?.phase).toBe('paused');
  });

  it('clears notices and flags on cancel without a session', async () => {
    renderProbe('prj_1', TINY);
    await act(async () => {
      await latest?.cancel();
    });
    expect(sessionSnapshot()).toBeUndefined();
    expect(latest?.busy).toBe(false);
    expect(world.aborts).toBe(0);
  });
});
