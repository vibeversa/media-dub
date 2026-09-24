import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import '../../../i18n/i18n.js';
import {
  clearTokenProvider,
  restoreInnerFetchForTests,
  setInnerFetchForTests,
  setTokenProvider,
} from '../../../api/client/index.js';
import { LocaleProvider } from '../../../app/providers/LocaleProvider.js';
import { ROUTER_FUTURE_FLAGS, ROUTER_PROVIDER_FUTURE_FLAGS } from '../../../app/router.js';
import { queryClient } from '../../../app/providers/queryClient.js';
import { useAppStore } from '../../../stores/index.js';
import { useAuthStore } from '../../auth/authStore.js';
import { resetRestoreStartedForTests } from '../../auth/useSession.js';
import { PreflightDialog } from '../PreflightDialog.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function errorEnvelope(code: string, status: number): Response {
  return jsonResponse({ error: { code, message: `backend ${code}`, correlationId: 'corr-9', details: {} } }, status);
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

function speakersBody(items: unknown[]): unknown {
  return { items, page: 1, pageSize: 100, total: items.length, hasMore: false };
}

function clonedSpeaker(): unknown {
  return {
    id: 'spk_1',
    projectId: 'prj_1',
    speakerKey: 'SPK1',
    displayName: 'Alice',
    segmentCount: 4,
    assignedVoice: { voiceProfileId: 'vp_1', voiceId: 'voice_clone', provider: 'acme', language: 'es', type: 'Cloned' },
  };
}

type StartBehavior = 'ok' | 'conflict' | 'quota' | 'error';

interface MockPlan {
  project?: unknown;
  summary?: unknown;
  summaryStatus?: number;
  speakers?: unknown;
  active?: 'missing' | 'run';
  start?: StartBehavior;
}

let plan: MockPlan;
const startPosts: { headers: Record<string, string>; body: unknown }[] = [];

function resetPlan(): void {
  plan = { active: 'missing', start: 'ok' };
  startPosts.length = 0;
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
  if (method === 'POST' && url.includes('/processing') && !url.includes('/cancel') && !url.includes('/retry')) {
    const headers: Record<string, string> = {};
    const raw = init?.headers;
    if (raw instanceof Headers) {
      raw.forEach((value, key) => {
        headers[key.toLowerCase()] = value;
      });
    } else if (typeof raw === 'object' && raw !== null) {
      for (const [key, value] of Object.entries(raw as Record<string, unknown>)) {
        if (typeof value === 'string') {
          headers[key.toLowerCase()] = value;
        }
      }
    }
    if (typeof input !== 'string' && !(input instanceof URL)) {
      (input as Request).headers.forEach((value, key) => {
        headers[key.toLowerCase()] = value;
      });
    }
    const rawBody = init?.body;
    startPosts.push({
      headers,
      body: typeof rawBody === 'string' && rawBody !== '' ? (JSON.parse(rawBody) as unknown) : null,
    });
    if (plan.start === 'conflict') {
      return errorEnvelope('RUN_ALREADY_ACTIVE', 409);
    }
    if (plan.start === 'quota') {
      return errorEnvelope('QUOTA_EXCEEDED', 429);
    }
    if (plan.start === 'error') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    return jsonResponse({ runId: 'run_1', status: 'Pending', configHash: 'cfg_abc' }, 202);
  }
  if (url.includes('/processing/active') && method === 'GET') {
    if (plan.active === 'run') {
      return jsonResponse({ runId: 'run_9', status: 'Running', configHash: 'cfg_old' });
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
    return jsonResponse(plan.project ?? projectBody());
  }
  return jsonResponse({});
}

const onClose = vi.fn();
const onStarted = vi.fn();

function renderDialog(): void {
  const router = createMemoryRouter([{ path: '/', element: <PreflightDialog projectId="prj_1" onClose={onClose} onStarted={onStarted} /> }], {
    initialEntries: ['/'],
    future: { ...ROUTER_FUTURE_FLAGS },
  });
  render(
    <QueryClientProvider client={queryClient}>
      <LocaleProvider>
        <RouterProvider router={router} future={{ ...ROUTER_PROVIDER_FUTURE_FLAGS }} />
      </LocaleProvider>
    </QueryClientProvider>,
  );
}

function authenticate(): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated', userId: 'usr_1' });
  useAppStore.getState().setSession('authenticated', ['project.view', 'processing.start', 'processing.retry']);
}

beforeEach(() => {
  resetPlan();
  onClose.mockReset();
  onStarted.mockReset();
  setInnerFetchForTests(mockFetch as typeof fetch);
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  queryClient.clear();
  window.localStorage.clear();
});

afterEach(() => {
  cleanup();
  restoreInnerFetchForTests();
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  queryClient.clear();
  vi.restoreAllMocks();
});

async function readyDialog(): Promise<void> {
  authenticate();
  renderDialog();
  expect(await screen.findByTestId('preflight-dialog')).toBeDefined();
  expect(await screen.findByTestId('preflight-confirm')).toBeDefined();
}

describe('estimate labeling (R2)', () => {
  it('labels the cost figure as an estimate adjacent to the amount', async () => {
    await readyDialog();
    expect(screen.getByTestId('preflight-estimate-label').textContent).toMatch(/estimate/i);
    const amount = screen.getByTestId('preflight-estimate-amount').textContent ?? '';
    expect(amount).toContain('$');
    expect(amount).toMatch(/estimate/i);
    expect((screen.getByTestId('preflight-confirm') as HTMLButtonElement).textContent).toContain('est.');
  });
});

describe('confirmation gating matrix', () => {
  it('enables confirm on a clean snapshot with no reason lines', async () => {
    await readyDialog();
    expect((screen.getByTestId('preflight-confirm') as HTMLButtonElement).disabled).toBe(false);
    expect(screen.getByTestId('preflight-blocks').textContent).toBe('');
  });

  it('blocks cloned-voice starts until consent is acknowledged (R3)', async () => {
    plan.speakers = speakersBody([clonedSpeaker()]);
    await readyDialog();
    const confirm = screen.getByTestId('preflight-confirm') as HTMLButtonElement;
    expect(confirm.disabled).toBe(true);
    expect(screen.getByTestId('preflight-block-consent')).toBeDefined();
    expect(screen.getByTestId('preflight-consent-voice-spk_1')).toBeDefined();

    fireEvent.click(screen.getByTestId('preflight-consent-check'));
    expect((screen.getByTestId('preflight-confirm') as HTMLButtonElement).disabled).toBe(false);
    expect(screen.getByTestId('preflight-consent-ack').textContent).toContain('usr_1');
  });

  it('requires an override reason while the daily quota is exhausted', async () => {
    plan.summary = summaryBody({ quota: { remaining: 0, resetsAt: '2026-09-25T00:00:00Z' } });
    await readyDialog();
    const confirm = screen.getByTestId('preflight-confirm') as HTMLButtonElement;
    expect(confirm.disabled).toBe(true);
    expect(screen.getByTestId('preflight-block-quota')).toBeDefined();

    fireEvent.change(screen.getByTestId('preflight-override-input'), { target: { value: 'launch cannot wait' } });
    expect((screen.getByTestId('preflight-confirm') as HTMLButtonElement).disabled).toBe(false);
  });

  it('disables start with a workspace link when a run is already active (R5)', async () => {
    plan.active = 'run';
    await readyDialog();
    expect(await screen.findByTestId('preflight-conflict')).toBeDefined();
    expect((screen.getByTestId('preflight-confirm') as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByTestId('preflight-block-conflict')).toBeDefined();
    expect(screen.getByTestId('preflight-conflict-workspace').getAttribute('href')).toBe('/projects/prj_1');
    fireEvent.click(screen.getByTestId('preflight-confirm'));
    expect(startPosts).toHaveLength(0);
  });

  it('warns without blocking on unvalidated or archived projects', async () => {
    plan.project = projectBody({ status: 'Uploading' });
    await readyDialog();
    expect(await screen.findByTestId('preflight-status-warning')).toBeDefined();
    expect((screen.getByTestId('preflight-confirm') as HTMLButtonElement).disabled).toBe(false);
    cleanup();

    resetPlan();
    plan.project = projectBody({ status: 'MediaReady', isArchived: true });
    await readyDialog();
    expect(await screen.findByTestId('preflight-archived-warning')).toBeDefined();
    expect((screen.getByTestId('preflight-confirm') as HTMLButtonElement).disabled).toBe(false);
  });
});

describe('start outcomes', () => {
  it('posts once with config hash + idempotency key and reports the run id', async () => {
    await readyDialog();
    fireEvent.click(screen.getByTestId('preflight-confirm'));
    await waitFor(() => {
      expect(onStarted).toHaveBeenCalledWith('run_1');
    });
    expect(startPosts).toHaveLength(1);
    expect(startPosts[0]?.body).toEqual({ configHash: 'cfg_abc' });
    expect(startPosts[0]?.headers['idempotency-key']).toMatch(/^[0-9a-f-]{36}$/);
  });

  it('double-click submits a single effective run', async () => {
    await readyDialog();
    const confirm = screen.getByTestId('preflight-confirm') as HTMLButtonElement;
    fireEvent.click(confirm);
    fireEvent.click(confirm);
    await waitFor(() => {
      expect(onStarted).toHaveBeenCalledWith('run_1');
    });
    expect(startPosts).toHaveLength(1);
  });

  it('switches to the conflict state on 409 without losing the dialog', async () => {
    plan.start = 'conflict';
    await readyDialog();
    fireEvent.click(screen.getByTestId('preflight-confirm'));
    expect(await screen.findByTestId('preflight-conflict')).toBeDefined();
    expect((screen.getByTestId('preflight-confirm') as HTMLButtonElement).disabled).toBe(true);
    expect(onStarted).not.toHaveBeenCalled();
  });

  it('renders the over-budget panel with the correlation reference on 429', async () => {
    plan.start = 'quota';
    await readyDialog();
    fireEvent.click(screen.getByTestId('preflight-confirm'));
    expect(await screen.findByTestId('preflight-quota-blocked')).toBeDefined();
    expect(screen.getByTestId('preflight-quota-blocked').textContent).toContain('corr-9');
    expect(onStarted).not.toHaveBeenCalled();
  });

  it('renders inline errors with the correlation reference on 500', async () => {
    plan.start = 'error';
    await readyDialog();
    fireEvent.click(screen.getByTestId('preflight-confirm'));
    expect(await screen.findByTestId('preflight-start-error')).toBeDefined();
    expect(screen.getByTestId('preflight-start-error').textContent).toContain('corr-9');
  });
});

describe('preflight load failures', () => {
  it('blocks starting with retry when the estimate cannot load', async () => {
    plan.summaryStatus = 500;
    authenticate();
    renderDialog();
    expect(await screen.findByTestId('preflight-load-error')).toBeDefined();
    expect(screen.queryByTestId('preflight-confirm')).toBeNull();

    plan.summaryStatus = 200;
    fireEvent.click(screen.getByTestId('preflight-retry'));
    expect(await screen.findByTestId('preflight-confirm')).toBeDefined();
  });

  it('closes without starting', async () => {
    await readyDialog();
    fireEvent.click(screen.getByTestId('preflight-cancel'));
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(startPosts).toHaveLength(0);
  });
});
