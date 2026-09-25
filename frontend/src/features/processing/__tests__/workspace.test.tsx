import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import '../../../i18n/i18n.js';
import {
  clearTokenProvider,
  restoreInnerFetchForTests,
  setInnerFetchForTests,
  setTokenProvider,
} from '../../../api/client/index.js';
import { queryKeys } from '../../../api/queryKeys/index.js';
import { LocaleProvider } from '../../../app/providers/LocaleProvider.js';
import { ROUTER_FUTURE_FLAGS, ROUTER_PROVIDER_FUTURE_FLAGS } from '../../../app/router.js';
import { queryClient } from '../../../app/providers/queryClient.js';
import { ToastProvider } from '../../../components/Toast/Toast.js';
import { useAppStore } from '../../../stores/index.js';
import { useAuthStore } from '../../auth/authStore.js';
import { resetRestoreStartedForTests } from '../../auth/useSession.js';
import { WorkspacePage } from '../WorkspacePage.js';
import {
  getWorkspaceActions,
  getWorkspacePollInterval,
  isVersionSkewed,
  parseWorkspace,
  projectPhaseStates,
} from '../useWorkspace.js';
import { useWorkspaceStore } from '../workspaceStore.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function errorEnvelope(code: string, status: number): Response {
  return jsonResponse({ error: { code, message: `backend ${code}`, correlationId: 'corr-ws', details: {} } }, status);
}

interface WorkspaceFixtureOverrides {
  project?: Record<string, unknown>;
  media?: Record<string, unknown>;
  run?: Record<string, unknown>;
  phase?: string;
  stage?: string | null;
  progress?: Record<string, unknown>;
  review?: Record<string, unknown>;
  warnings?: unknown;
  output?: Record<string, unknown>;
  cost?: Record<string, unknown>;
  activity?: unknown;
  permissions?: unknown;
}

function workspaceBody(overrides: WorkspaceFixtureOverrides = {}): Record<string, unknown> {
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
    media: {
      id: 'med_1',
      status: 'Valid',
      container: 'mp4',
      sizeBytes: 1024,
      durationMs: 61000,
      ...(overrides.media ?? {}),
    },
    run: {
      id: 'run_1',
      status: 'Running',
      configHash: 'cfg_abc',
      attempt: 1,
      ...(overrides.run ?? {}),
    },
    phase: overrides.phase ?? 'speech',
    stage: overrides.stage === undefined ? 'Transcription' : overrides.stage,
    progress: {
      percentApproximate: 42,
      currentStage: 'Transcription',
      updatedAt: '2024-01-16T12:00:00Z',
      ...(overrides.progress ?? {}),
    },
    review: {
      pendingCount: 2,
      oldestWaitingAt: '2024-01-16T10:00:00Z',
      ...(overrides.review ?? {}),
    },
    warnings: overrides.warnings ?? [{ code: 'REVIEW_OPEN', message: 'Two reviews await.' }],
    output: {
      state: 'pending',
      completeness: 42,
      ...(overrides.output ?? {}),
    },
    cost: {
      runCost: 1.5,
      monthToDate: 12.5,
      ...(overrides.cost ?? {}),
    },
    activity:
      overrides.activity ??
      ({
        recent: [
          { id: 'act_1', summary: 'Run started', occurredAt: '2024-01-16T11:00:00Z' },
          { id: 'act_2', summary: 'Media validated', occurredAt: '2024-01-15T12:00:00Z' },
        ],
      } as unknown),
    permissions:
      overrides.permissions ??
      ({
        allowedActions: [
          'project.view',
          'project.edit',
          'project.delete',
          'processing.cancel',
          'processing.retry',
          'export.create',
          'export.download',
          'admin.manage',
        ],
      } as unknown),
  };
}

const workspaceCalls: string[] = [];
const otherCalls: string[] = [];
let fixture: Record<string, unknown> = workspaceBody();
let workspaceStatus = 200;

async function mockFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const url = typeof input === 'string' ? input : input instanceof URL ? input.href : (input as Request).url;
  const method = (init?.method ?? 'GET').toUpperCase();
  if (url.includes('/workspace') && method === 'GET') {
    workspaceCalls.push(url);
    if (workspaceStatus !== 200) {
      return errorEnvelope('NOT_FOUND', workspaceStatus);
    }
    return jsonResponse(fixture);
  }
  otherCalls.push(`${method} ${url}`);
  return jsonResponse({});
}

function authenticate(permissions: readonly string[] = ['project.view', 'admin.manage']): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', permissions);
}

function renderWorkspace(projectId = 'prj_1', initialEntry = '/projects/prj_1'): ReturnType<typeof createMemoryRouter> {
  const router = createMemoryRouter(
    [
      { path: '/projects/:id', element: <WorkspacePage projectId={projectId} /> },
      { path: '/projects', element: <div data-testid="page-projects" /> },
      { path: '/review', element: <div data-testid="page-review" /> },
      { path: '/projects/:id/media', element: <div data-testid="page-project-media" /> },
      { path: '/projects/:id/quality', element: <div data-testid="page-project-quality" /> },
      { path: '/projects/:id/exports', element: <div data-testid="page-project-exports" /> },
      { path: '/projects/:id/activity', element: <div data-testid="page-project-activity" /> },
    ],
    { initialEntries: [initialEntry], future: { ...ROUTER_FUTURE_FLAGS } },
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
  return router;
}

beforeEach(() => {
  workspaceCalls.length = 0;
  otherCalls.length = 0;
  fixture = workspaceBody();
  workspaceStatus = 200;
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

describe('single aggregate request (R1)', () => {
  it('fetches the workspace once per mount with no per-panel reads', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('workspace-page')).toBeDefined();
    expect(await screen.findByTestId('workspace-header')).toBeDefined();
    expect(workspaceCalls).toHaveLength(1);
    expect(workspaceCalls[0]).toContain('/workspace');
    // Task 026 live progress may open the SSE stream and, after repeated
    // failures, poll the progress endpoint. Those are live-update reads, not
    // per-panel aggregate fan-out, so they are allowlisted here.
    const projectReads = otherCalls.filter(
      (call) => call.includes('/projects/') && !call.includes('/progress'),
    );
    expect(projectReads).toEqual([]);
    const cached = queryClient.getQueryData(queryKeys.workspace.detail('prj_1'));
    expect(cached).toBeDefined();
  });

  it('issues exactly one workspace call per refresh', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('workspace-header')).toBeDefined();
    expect(workspaceCalls).toHaveLength(1);
    await queryClient.invalidateQueries({ queryKey: queryKeys.workspace.detail('prj_1') });
    await waitFor(() => {
      expect(workspaceCalls.length).toBeGreaterThanOrEqual(2);
    });
    expect(workspaceCalls).toHaveLength(2);
  });
});

describe('stepper states from fixture aggregates', () => {
  it('marks pre-run phases queued', () => {
    const states = projectPhaseStates('created', null, undefined);
    expect(states).toHaveLength(8);
    for (const stage of states) {
      expect(stage.state).toBe('pending');
    }
  });

  it('marks completed aggregates all done', () => {
    const states = projectPhaseStates('completed', 'Render', 'Completed');
    for (const stage of states) {
      expect(stage.state).toBe('done');
    }
  });

  it('marks the failed frontier failed with earlier done', () => {
    const states = projectPhaseStates('failed', 'Translation', 'Failed');
    const byId = new Map(states.map((stage) => [stage.id, stage.state]));
    expect(byId.get('validation')).toBe('done');
    expect(byId.get('speech')).toBe('done');
    expect(byId.get('translation')).toBe('failed');
  });

  it('renders parallel notes only on the branch phases (R3)', async () => {
    authenticate();
    fixture = workspaceBody({ phase: 'translation', stage: 'Translation' });
    renderWorkspace();
    expect(await screen.findByTestId('workspace-stepper')).toBeDefined();
    expect(screen.getByTestId('workspace-stage-translation-parallel').textContent).toMatch(/runs in parallel/);
    expect(screen.getByTestId('workspace-stage-voice-parallel').textContent).toMatch(/runs in parallel/);
    expect(screen.queryByTestId('workspace-stage-speech-parallel')).toBeNull();
    expect(screen.queryByTestId('workspace-stage-mix-parallel')).toBeNull();
    expect(screen.queryByTestId('workspace-stage-render-parallel')).toBeNull();
  });
});

describe('no time predictions (R2)', () => {
  it('renders zero banned phrases in the workspace UI', async () => {
    authenticate();
    fixture = workspaceBody();
    const router = renderWorkspace();
    void router;
    expect(await screen.findByTestId('workspace-header')).toBeDefined();
    expect(await screen.findByTestId('workspace-stepper')).toBeDefined();
    const text = document.querySelector('[data-testid="workspace-page"]')?.textContent ?? '';
    expect(text).not.toMatch(/ETA/);
    expect(text).not.toMatch(/estimated time/);
    expect(text).not.toMatch(/remaining/);
  });
});

describe('state-adaptive panels (R4)', () => {
  it('hides progress for pre-run and shows the run EmptyState', async () => {
    authenticate();
    fixture = workspaceBody({
      run: { id: undefined, status: undefined, configHash: undefined, attempt: 0 },
      phase: 'created',
      stage: null,
      progress: { percentApproximate: 0 },
    });
    // Backend omits the run id pre-run; strip it explicitly.
    const runSection = fixture['run'] as Record<string, unknown>;
    delete runSection['id'];
    renderWorkspace();
    expect(await screen.findByTestId('workspace-prerun')).toBeDefined();
    expect(screen.queryByTestId('workspace-progress')).toBeNull();
    expect(screen.queryByTestId('workspace-stepper')).toBeNull();
    expect(screen.getByTestId('workspace-run-empty')).toBeDefined();
  });

  it('shows recovery actions for failed runs', async () => {
    authenticate();
    fixture = workspaceBody({ run: { id: 'run_9', status: 'Failed', configHash: 'cfg_abc', attempt: 2 }, phase: 'failed', stage: 'Translation' });
    renderWorkspace();
    expect(await screen.findByTestId('workspace-recovery')).toBeDefined();
    expect(screen.getByTestId('workspace-recovery-retry')).toBeDefined();
    expect(screen.getByTestId('workspace-action-retry')).toBeDefined();
  });

  it('stops polling hooks on terminal states', () => {
    const activeBase = workspaceBody({ run: { id: 'run_1', status: 'Running', configHash: 'cfg_abc', attempt: 1 } });
    const active = getWorkspacePollInterval(parseWorkspace(activeBase));
    expect(active).toBe(15_000);
    const terminalCases: Array<Record<string, unknown>> = [
      workspaceBody({ run: { id: 'run_1', status: 'Completed', configHash: 'cfg_abc', attempt: 1 }, phase: 'completed' }),
      workspaceBody({ run: { id: 'run_1', status: 'Failed', configHash: 'cfg_abc', attempt: 1 }, phase: 'failed' }),
      workspaceBody({ run: { id: 'run_1', status: 'Cancelled', configHash: 'cfg_abc', attempt: 1 }, phase: 'failed' }),
    ];
    for (const body of terminalCases) {
      expect(getWorkspacePollInterval(parseWorkspace(body))).toBe(false);
    }
  });
});

describe('workspace 404 redirect', () => {
  it('redirects deleted workspaces to the list with a toast', async () => {
    authenticate();
    workspaceStatus = 404;
    renderWorkspace();
    expect(await screen.findByTestId('page-projects')).toBeDefined();
    await waitFor(() => {
      expect(document.body.textContent).toContain('Project not found');
    });
  });
});

describe('version skew and auxiliary panels', () => {
  it('flags hash drift as skew and shows the stale banner with refresh', async () => {
    authenticate();
    fixture = workspaceBody({
      project: { configurationHash: 'cfg_new' },
      run: { id: 'run_1', status: 'Running', configHash: 'cfg_old', attempt: 1 },
    });
    expect(isVersionSkewed(parseWorkspace(fixture))).toBe(true);
    renderWorkspace();
    expect(await screen.findByTestId('workspace-stale')).toBeDefined();
    expect(screen.getByTestId('workspace-refresh')).toBeDefined();
  });

  it('gates header actions to the aggregate allowlist', async () => {
    authenticate();
    fixture = workspaceBody({
      output: { state: 'ready', completeness: 100 },
      permissions: { allowedActions: ['project.view'] },
    });
    const view = parseWorkspace(fixture);
    expect(getWorkspaceActions(view.allowedActions, view)).toEqual(['open']);
    renderWorkspace();
    expect(await screen.findByTestId('workspace-action-open')).toBeDefined();
    expect(screen.queryByTestId('workspace-action-cancel')).toBeNull();
    expect(screen.queryByTestId('workspace-action-retry')).toBeNull();
    expect(screen.queryByTestId('workspace-action-export')).toBeNull();
    expect(screen.queryByTestId('workspace-action-delete')).toBeNull();
  });

  it('hides cost without the usage permission and shows no error', async () => {
    authenticate(['project.view']);
    fixture = workspaceBody({ permissions: { allowedActions: ['project.view'] } });
    renderWorkspace();
    expect(await screen.findByTestId('workspace-header')).toBeDefined();
    expect(screen.queryByTestId('workspace-cost')).toBeNull();
    expect(screen.queryByTestId('workspace-error')).toBeNull();
  });

  it('shows config hash without secrets', async () => {
    authenticate();
    fixture = workspaceBody();
    renderWorkspace();
    expect(await screen.findByTestId('workspace-config-hash')).toBeDefined();
    expect(screen.getByTestId('workspace-config-hash').textContent).toContain('cfg_abc');
    const pageText = document.querySelector('[data-testid="workspace-page"]')?.textContent ?? '';
    expect(pageText.toLowerCase()).not.toContain('secret');
  });
});
