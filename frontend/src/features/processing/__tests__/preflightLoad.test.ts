import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  clearTokenProvider,
  restoreInnerFetchForTests,
  setInnerFetchForTests,
  setTokenProvider,
} from '../../../api/client/index.js';
import { loadPreflight } from '../usePreflight.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function errorEnvelope(code: string, status: number): Response {
  return jsonResponse({ error: { code, message: `backend ${code}`, correlationId: 'corr-1', details: {} } }, status);
}

function projectBody(overrides: Record<string, unknown> = {}): unknown {
  return {
    id: 'prj_1',
    name: 'Pilot',
    status: 'MediaReady',
    settingsVersion: 1,
    configHash: 'cfg_abc',
    sourceLanguage: 'en',
    targetLanguage: 'es',
    createdAt: '2024-01-15T12:00:00Z',
    updatedAt: '2024-01-15T12:00:00Z',
    ...overrides,
  };
}

function summaryBody(overrides: Record<string, unknown> = {}): unknown {
  return {
    projectCounts: { active: 1, archived: 0, total: 1 },
    recentOutputs: [],
    storage: { usedBytes: 1, quotaBytes: 100 },
    cost: { monthToDate: 12.5, currency: 'USD' },
    quota: { remaining: 5, resetsAt: '2026-09-25T00:00:00Z' },
    warnings: [],
    backlog: { pendingReviews: 0, runningJobs: 0 },
    ...overrides,
  };
}

function speakersBody(items: unknown[], total?: number): unknown {
  return { items, page: 1, pageSize: 100, total: total ?? items.length, hasMore: false };
}

interface MockPlan {
  projectStatus?: number;
  project?: unknown;
  summaryStatus?: number;
  summary?: unknown;
  speakers?: unknown;
  activeStatus?: 'missing' | 'run' | 'error';
}

let plan: MockPlan;

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
  if (url.includes('/processing/active') && method === 'GET') {
    if (plan.activeStatus === 'run') {
      return jsonResponse({ runId: 'run_9', status: 'Running', configHash: 'cfg_old' });
    }
    if (plan.activeStatus === 'error') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    return errorEnvelope('NOT_FOUND', 404);
  }
  if (url.includes('/speakers') && method === 'GET') {
    return jsonResponse(plan.speakers ?? speakersBody([]));
  }
  if (url.includes('/dashboard/summary') && method === 'GET') {
    if ((plan.summaryStatus ?? 200) !== 200) {
      return errorEnvelope('INTERNAL_ERROR', plan.summaryStatus ?? 500);
    }
    return jsonResponse(plan.summary ?? summaryBody());
  }
  if (/\/api\/v1\/projects\/[^/]+$/.test(url) && method === 'GET') {
    if ((plan.projectStatus ?? 200) !== 200) {
      return errorEnvelope('FORBIDDEN', plan.projectStatus ?? 403);
    }
    return jsonResponse(plan.project ?? projectBody());
  }
  return jsonResponse({});
}

beforeEach(() => {
  plan = { activeStatus: 'missing' };
  setInnerFetchForTests(mockFetch as typeof fetch);
  setTokenProvider(() => 'test-token');
});

afterEach(() => {
  restoreInnerFetchForTests();
  clearTokenProvider();
  vi.restoreAllMocks();
});

describe('loadPreflight assembly', () => {
  it('assembles the snapshot and treats active-run 404 as clear', async () => {
    const snapshot = await loadPreflight('prj_1');
    expect(snapshot.projectId).toBe('prj_1');
    expect(snapshot.configHash).toBe('cfg_abc');
    expect(snapshot.currency).toBe('USD');
    expect(snapshot.monthToDate).toBe(12.5);
    expect(snapshot.quota.remaining).toBe(5);
    expect(snapshot.activeRun).toBeNull();
    expect(snapshot.clonedVoices).toEqual([]);
    expect(snapshot.estimate.segmentCount).toBe(10);
  });

  it('surfaces an active run for the conflict state', async () => {
    plan.activeStatus = 'run';
    const snapshot = await loadPreflight('prj_1');
    expect(snapshot.activeRun).toEqual({ runId: 'run_9', status: 'Running' });
  });

  it('prices real speaker segments and flags truncation', async () => {
    plan.speakers = speakersBody(
      [
        { id: 'spk_1', speakerKey: 'A', displayName: 'A', segmentCount: 6, assignedVoice: null },
        { id: 'spk_2', speakerKey: 'B', displayName: 'B', segmentCount: 4, assignedVoice: null },
      ],
      250,
    );
    const snapshot = await loadPreflight('prj_1');
    expect(snapshot.estimate.segmentCount).toBe(10);
    expect(snapshot.speakerTotal).toBe(250);
    expect(snapshot.speakersTruncated).toBe(true);
  });

  it('collects cloned voices for consent gating', async () => {
    plan.speakers = speakersBody([
      {
        id: 'spk_1',
        speakerKey: 'SPK1',
        displayName: 'Alice',
        segmentCount: 2,
        assignedVoice: { voiceId: 'voice_clone', type: 'Cloned' },
      },
    ]);
    const snapshot = await loadPreflight('prj_1');
    expect(snapshot.clonedVoices).toEqual([
      { speakerId: 'spk_1', speakerKey: 'SPK1', displayName: 'Alice', voiceId: 'voice_clone' },
    ]);
    expect(snapshot.estimate.segmentCount).toBe(2);
  });

  it('rejects when the summary fetch fails (never start blind)', async () => {
    plan.summaryStatus = 500;
    await expect(loadPreflight('prj_1')).rejects.toThrow();
  });

  it('rejects when cost/quota sections are invalid', async () => {
    plan.summary = summaryBody({ cost: {}, quota: { remaining: 'many' } });
    await expect(loadPreflight('prj_1')).rejects.toThrow(/cost\/quota/);
  });

  it('rejects when the speaker list is not an array (consent cannot be assessed)', async () => {
    plan.speakers = { items: null, total: 0 };
    await expect(loadPreflight('prj_1')).rejects.toThrow(/speaker list/);
  });

  it('rejects non-404 active-run failures', async () => {
    plan.activeStatus = 'error';
    await expect(loadPreflight('prj_1')).rejects.toThrow();
  });

  it('rejects project fetch failures', async () => {
    plan.projectStatus = 403;
    await expect(loadPreflight('prj_1')).rejects.toThrow();
  });
});
