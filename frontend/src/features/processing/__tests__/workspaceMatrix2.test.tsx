// Task 039B: processing workspace residual state-matrix closure (part 2).
//
// Closes the branches and handlers left cold by `workspace.test.tsx` +
// `workspaceMatrix.test.tsx`: the load-error `ErrorState` recovery (retry
// refetches the aggregate), the archive mutation failure path, the stale
// banner refresh, and every secondary-column navigation link (export,
// review, quality, output, activity) that records the selected tab. Also
// covers the defensive display fallbacks the aggregate can omit: zero/oversize
// media bytes, sub-minute durations, missing progress stamps, a failed run
// with no reported status, empty review/warnings/activity slices, an absent
// media id, missing config hash/languages, and the pre-run placeholder.
//
// Every failure asserts its recovery control per §11.6 with text signals
// (role/label/testid — never color-only, supporting 041C); fixtures are
// synthetic (`corr-*` correlation ids, no real tenants/media/transcripts) and
// fetch is intercepted via `setInnerFetchForTests` (R3).
import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
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
import { queryClient } from '../../../app/providers/queryClient.js';
import { ROUTER_FUTURE_FLAGS, ROUTER_PROVIDER_FUTURE_FLAGS } from '../../../app/router.js';
import { ToastProvider } from '../../../components/Toast/Toast.js';
import { useAppStore } from '../../../stores/index.js';
import { useAuthStore } from '../../auth/authStore.js';
import { resetRestoreStartedForTests } from '../../auth/useSession.js';
import { WorkspacePage } from '../WorkspacePage.js';
import { useWorkspaceStore } from '../workspaceStore.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number): Response {
  return jsonResponse({ error: { code, message: `backend ${code}`, correlationId: 'corr-w25b', details: {} } }, status);
}

interface FixtureOverrides {
  readonly project?: Record<string, unknown>;
  readonly media?: Record<string, unknown>;
  readonly run?: Record<string, unknown>;
  readonly phase?: string;
  readonly stage?: string | null;
  readonly progress?: Record<string, unknown>;
  readonly review?: Record<string, unknown>;
  readonly warnings?: unknown;
  readonly activity?: unknown;
  readonly output?: Record<string, unknown>;
  readonly allowedActions?: readonly string[];
}

const ALL_ACTIONS: readonly string[] = [
  'project.view',
  'project.edit',
  'project.delete',
  'processing.cancel',
  'processing.retry',
  'export.create',
];

function workspaceBody(overrides: FixtureOverrides = {}): Record<string, unknown> {
  return {
    project: {
      id: 'prj_1',
      name: 'Pilot',
      status: 'Processing',
      sourceLanguage: 'en',
      targetLanguage: 'es',
      isArchived: false,
      configurationHash: 'cfg_abc',
      settingsVersion: 3,
      ...(overrides.project ?? {}),
    },
    media: { id: 'med_1', status: 'Valid', container: 'mp4', sizeBytes: 1024, durationMs: 61_000, ...(overrides.media ?? {}) },
    run: { id: 'run_1', status: 'Running', configHash: 'cfg_abc', attempt: 1, ...(overrides.run ?? {}) },
    phase: overrides.phase ?? 'speech',
    stage: overrides.stage === undefined ? 'Transcription' : overrides.stage,
    progress: { percentApproximate: 42, currentStage: 'Transcription', updatedAt: '2024-01-16T12:00:00Z', ...(overrides.progress ?? {}) },
    review: { pendingCount: 2, oldestWaitingAt: '2024-01-16T10:00:00Z', ...(overrides.review ?? {}) },
    warnings: overrides.warnings === undefined ? ['REVIEW_OPEN: Two reviews await.'] : overrides.warnings,
    output: { state: 'pending', completeness: 42, ...(overrides.output ?? {}) },
    cost: { runCost: 1.5, monthToDate: 12.5 },
    activity:
      overrides.activity === undefined
        ? { recent: [{ id: 'act_1', summary: 'Run started', occurredAt: '2024-01-16T11:00:00Z' }] }
        : overrides.activity,
    permissions: { allowedActions: overrides.allowedActions ?? ALL_ACTIONS },
  };
}

interface World {
  mode: 'ok' | 'error';
  mutationMode: 'ok' | 'fail';
  workspaceCalls: number;
  mutations: string[];
}

let world: World;
let fixture: Record<string, unknown>;

function urlOf(input: RequestInfo | URL): string {
  if (typeof input === 'string') return input;
  if (input instanceof URL) return input.href;
  return (input as Request).url;
}

function methodOf(input: RequestInfo | URL, init?: RequestInit): string {
  return (typeof input !== 'string' && !(input instanceof URL) ? (input as Request).method : undefined) ?? init?.method ?? 'GET';
}

async function mockFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const url = urlOf(input);
  const method = methodOf(input, init).toUpperCase();
  if (url.includes('/workspace') && method === 'GET') {
    world.workspaceCalls += 1;
    if (world.mode === 'error') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    return jsonResponse(fixture);
  }
  if (method !== 'GET') {
    const action = /unarchive/i.test(url)
      ? 'unarchive'
      : /archive/i.test(url)
        ? 'archive'
        : /retry/i.test(url)
          ? 'retry'
          : /cancel/i.test(url)
            ? 'cancel'
            : 'delete';
    world.mutations.push(action);
    if (world.mutationMode === 'fail') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    return jsonResponse({ id: 'run_1', status: 'Running' });
  }
  return jsonResponse({});
}

function authenticate(permissions: readonly string[] = ['project.view', 'admin.manage']): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', permissions);
}

function renderWorkspace(): void {
  const router = createMemoryRouter(
    [
      { path: '/projects/:id', element: <WorkspacePage projectId="prj_1" /> },
      { path: '/projects', element: <div data-testid="page-projects" /> },
      { path: '/review', element: <div data-testid="page-review" /> },
      { path: '/projects/:id/media', element: <div data-testid="page-project-media" /> },
      { path: '/projects/:id/quality', element: <div data-testid="page-project-quality" /> },
      { path: '/projects/:id/exports', element: <div data-testid="page-project-exports" /> },
      { path: '/projects/:id/activity', element: <div data-testid="page-project-activity" /> },
    ],
    { initialEntries: ['/projects/prj_1'], future: { ...ROUTER_FUTURE_FLAGS } },
  );
  render(
    <QueryClientProvider client={queryClient}>
      <LocaleProvider>
        <ToastProvider>
          <RouterProvider router={router} future={{ ...ROUTER_PROVIDER_FUTURE_FLAGS }} />
        </ToastProvider>
      </LocaleProvider>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  world = { mode: 'ok', mutationMode: 'ok', workspaceCalls: 0, mutations: [] };
  fixture = workspaceBody();
  setInnerFetchForTests(mockFetch as typeof fetch);
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  useWorkspaceStore.getState().resetForTests();
  resetRestoreStartedForTests();
  queryClient.clear();
});

afterEach(() => {
  cleanup();
  restoreInnerFetchForTests();
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  useWorkspaceStore.getState().resetForTests();
  resetRestoreStartedForTests();
  queryClient.clear();
  vi.restoreAllMocks();
});

describe('workspace load error recovery (recovery: retry)', () => {
  it('renders a blocking load error and refetches the aggregate on retry', async () => {
    world.mode = 'error';
    authenticate();
    renderWorkspace();
    const panel = await screen.findByTestId('workspace-error');
    const alert = within(panel).getByRole('alert');
    expect(alert.textContent).toContain('Workspace unavailable');
    expect(alert.textContent).toContain('Ref: corr-w25b');
    expect(screen.getByRole('button', { name: 'Retry' })).toBeDefined();
    const callsBeforeRetry = world.workspaceCalls;
    world.mode = 'ok';
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(world.workspaceCalls).toBeGreaterThan(callsBeforeRetry));
    expect(await screen.findByTestId('workspace-header')).toBeDefined();
    // The retry replaced the error with real data rather than appending a
    // second panel; the aggregate rows only exist post-retry.
    expect(screen.queryByTestId('workspace-error')).toBeNull();
    expect(screen.getByTestId('workspace-actions')).toBeDefined();
  });
});

describe('archive mutation failure (recovery: retry the action)', () => {
  it('surfaces a blocking archive failure with its support reference and stays on the page', async () => {
    world.mutationMode = 'fail';
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('workspace-actions')).toBeDefined();
    fireEvent.click(screen.getByTestId('workspace-action-archive'));
    const alert = await screen.findByTestId('workspace-action-error');
    expect(alert.textContent).toContain('backend INTERNAL_ERROR');
    expect(alert.textContent).toContain('Ref: corr-w25b');
    // A failed archive never navigates away and keeps the action available so
    // the user can retry the same mutation.
    expect(screen.queryByTestId('page-projects')).toBeNull();
    expect(screen.getByTestId('workspace-action-archive').textContent).toContain('Archive');
    world.mutationMode = 'ok';
    fireEvent.click(screen.getByTestId('workspace-action-archive'));
    await waitFor(() => expect(screen.queryByTestId('workspace-action-error')).toBeNull());
  });
});

describe('stale aggregate banner (recovery: refresh)', () => {
  it('offers an explicit refresh when the run hash drifted from the project', async () => {
    fixture = workspaceBody({ run: { id: 'run_1', status: 'Running', configHash: 'cfg_drifted', attempt: 1 } });
    authenticate();
    renderWorkspace();
    const stale = await screen.findByTestId('workspace-stale');
    expect(stale.textContent).toContain('Data may be out of date');
    expect(screen.getByTestId('workspace-refresh').textContent).toContain('Refresh workspace');
    const callsBeforeRefresh = world.workspaceCalls;
    fireEvent.click(screen.getByTestId('workspace-refresh'));
    await waitFor(() => expect(world.workspaceCalls).toBeGreaterThan(callsBeforeRefresh));
  });
});

describe('secondary-column navigation links', () => {
  it('offers no export action until the output is ready', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('workspace-actions')).toBeDefined();
    expect(screen.queryByTestId('workspace-action-export')).toBeNull();
    expect(screen.getByTestId('workspace-output-state').textContent).toBe('Pending');
  });

  it('opens the exports tab from the header export action', async () => {
    fixture = workspaceBody({ output: { state: 'ready', completeness: 100 } });
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('workspace-actions')).toBeDefined();
    expect(screen.getByTestId('workspace-output-state').textContent).toBe('Ready');
    fireEvent.click(screen.getByTestId('workspace-action-export'));
    expect(useWorkspaceStore.getState().selectedTab).toBe('exports');
    expect(await screen.findByTestId('page-project-exports')).toBeDefined();
  });

  it('records the review tab from the review link', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('workspace-review')).toBeDefined();
    expect(screen.getByTestId('workspace-review-count').textContent).toContain('2');
    fireEvent.click(screen.getByTestId('workspace-review-link'));
    expect(await screen.findByTestId('page-review')).toBeDefined();
    expect(useWorkspaceStore.getState().selectedTab).toBe('quality');
  });

  it('records the quality tab from the warnings link', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('workspace-warnings')).toBeDefined();
    fireEvent.click(screen.getByTestId('workspace-quality-link'));
    expect(await screen.findByTestId('page-project-quality')).toBeDefined();
    expect(useWorkspaceStore.getState().selectedTab).toBe('quality');
  });

  it('records the exports tab from the output panel link', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('workspace-output')).toBeDefined();
    fireEvent.click(screen.getByTestId('workspace-output-link'));
    expect(await screen.findByTestId('page-project-exports')).toBeDefined();
    expect(useWorkspaceStore.getState().selectedTab).toBe('exports');
  });

  it('records the activity tab from the activity link', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('workspace-activity')).toBeDefined();
    expect(screen.getByTestId('workspace-activity-row-act_1').textContent).toContain('Run started');
    fireEvent.click(screen.getByTestId('workspace-activity-link'));
    expect(await screen.findByTestId('page-project-activity')).toBeDefined();
    expect(useWorkspaceStore.getState().selectedTab).toBe('activity');
  });
});

describe('aggregate display fallbacks', () => {
  it('renders a zero-byte media figure and an unknown duration without breaking the panel', async () => {
    fixture = workspaceBody({ media: { id: 'med_1', status: 'Valid', container: 'mp4', sizeBytes: 0, durationMs: 0 } });
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('workspace-media')).toBeDefined();
    expect(screen.getByTestId('workspace-media-size').textContent).toBe('0 B');
    expect(screen.getByTestId('workspace-media-duration').textContent).toBe('—');
  });

  it('caps oversize media figures at the largest unit', async () => {
    fixture = workspaceBody({ media: { id: 'med_2', status: 'Valid', container: 'mp4', sizeBytes: 5_000_000_000_000, durationMs: 61_000 } });
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('workspace-media')).toBeDefined();
    expect(screen.getByTestId('workspace-media-size').textContent).toContain('TB');
    expect(screen.getByTestId('workspace-media-duration').textContent).toContain('m ');
  });

  it('formats sub-minute durations as seconds only', async () => {
    fixture = workspaceBody({ media: { id: 'med_1', status: 'Valid', container: 'mp4', sizeBytes: 2048, durationMs: 30_000 } });
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('workspace-media')).toBeDefined();
    expect(screen.getByTestId('workspace-media-duration').textContent).toBe('30s');
  });

  it('omits progress stamps the aggregate does not report', async () => {
    fixture = workspaceBody({ progress: { percentApproximate: 0, currentStage: '', updatedAt: '' } });
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('workspace-stage-progress')).toBeDefined();
    expect(screen.queryByTestId('workspace-progress-current')).toBeNull();
    expect(screen.queryByTestId('workspace-progress-updated')).toBeNull();
  });

  it('describes a failed run that reports no status using the phase', async () => {
    fixture = workspaceBody({ run: { id: 'run_1', status: '', configHash: 'cfg_abc', attempt: 2 }, phase: 'failed', stage: null });
    authenticate();
    renderWorkspace();
    const recovery = await screen.findByTestId('workspace-recovery');
    expect(recovery.textContent).toContain('Run did not complete');
    expect(recovery.textContent).toContain('failed');
    expect(screen.getByTestId('workspace-run-status').textContent).toBe('—');
    expect(screen.getByTestId('workspace-recovery-retry')).toBeDefined();
  });

  it('hides the recovery retry when the retry permission is absent', async () => {
    fixture = workspaceBody({
      run: { id: 'run_1', status: 'Failed', configHash: 'cfg_abc', attempt: 1 },
      phase: 'failed',
      allowedActions: ['project.view'],
    });
    authenticate();
    renderWorkspace();
    const recovery = await screen.findByTestId('workspace-recovery');
    expect(recovery.textContent).toContain('Run did not complete');
    // Forbidden sub-action: no retry control renders, and no run detail leaks.
    expect(screen.queryByTestId('workspace-recovery-retry')).toBeNull();
    expect(screen.queryByTestId('workspace-action-retry')).toBeNull();
  });

  it('renders empty review, warnings, activity, and media placeholders', async () => {
    fixture = workspaceBody({ review: { pendingCount: 0 }, warnings: [], activity: { recent: [] }, media: { id: '' } });
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('workspace-review-empty')).toBeDefined();
    expect(screen.getByTestId('workspace-warnings-empty').textContent).toBe('No warnings.');
    expect(screen.getByTestId('workspace-activity-empty').textContent).toBe('No activity yet.');
    expect(screen.getByTestId('workspace-media-empty').textContent).toBe('No media yet.');
    // The media summary table is replaced by the placeholder, not both.
    expect(screen.queryByTestId('workspace-media-status')).toBeNull();
  });

  it('renders an activity row without a timestamp', async () => {
    fixture = workspaceBody({ activity: { recent: [{ id: 'act_2', summary: 'Run finished' }] } });
    authenticate();
    renderWorkspace();
    const row = await screen.findByTestId('workspace-activity-row-act_2');
    expect(row.textContent).toBe('Run finished');
  });

  it('falls back to placeholders for missing config hash and languages', async () => {
    fixture = workspaceBody({
      project: {
        id: 'prj_1',
        name: 'Pilot',
        status: 'Created',
        sourceLanguage: '',
        targetLanguage: '',
        isArchived: false,
        configurationHash: '',
        settingsVersion: 0,
      },
    });
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('workspace-config')).toBeDefined();
    expect(screen.getByTestId('workspace-config-hash').textContent).toBe('Not reported yet');
    expect(screen.getByTestId('workspace-config-languages').textContent).toBe('— → —');
    expect(screen.getByTestId('workspace-config-version').textContent).toBe('0');
  });

  it('shows the prerun placeholder before any run exists', async () => {
    fixture = workspaceBody({
      run: { id: '', status: '', configHash: 'cfg_abc', attempt: 0 },
      stage: null,
      progress: { percentApproximate: 0, currentStage: '', updatedAt: '' },
    });
    authenticate();
    renderWorkspace();
    const prerun = await screen.findByTestId('workspace-prerun');
    expect(prerun.textContent).toContain('No run yet');
    expect(screen.getByTestId('workspace-run-empty')).toBeDefined();
    // Pre-run: no progress strip and no cancel control for a run that is not
    // started, so the header never implies work is in flight.
    expect(screen.queryByTestId('workspace-pipeline')).toBeNull();
    expect(screen.queryByTestId('workspace-progress')).toBeNull();
    expect(screen.queryByTestId('workspace-action-cancel')).toBeNull();
  });
});
