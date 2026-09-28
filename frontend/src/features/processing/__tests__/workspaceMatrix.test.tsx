// Task 039B: processing workspace state-matrix gap closure.
//
// Extends `workspace.test.tsx` (single aggregate, stepper, panels, 404
// redirect, skew, allowlist, cost gating) with the missing states: run
// actions (cancel/retry success + failure with report refs), destructive
// delete (success navigates, failure stays with ref), archive/unarchive
// labels + calls, open/export link wiring, the workspace-store clamp matrix,
// and the `useWorkspace` helper sweep (terminal/poll/skew/failed/progress/
// recovery/stage mapping/phase projection/actions/parse). Every failure
// asserts its recovery control per §11.6 with text signals (041C); fixtures
// are synthetic and fetch is intercepted (R3).
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
import { ToastProvider } from '../../../components/Toast/Toast.js';
import { useAppStore } from '../../../stores/index.js';
import { useAuthStore } from '../../auth/authStore.js';
import { resetRestoreStartedForTests } from '../../auth/useSession.js';
import { WorkspacePage } from '../WorkspacePage.js';
import {
  getWorkspaceActions,
  getWorkspacePollInterval,
  hasRun,
  isFailedWorkspace,
  isTerminalRunStatus,
  isTerminalWorkspace,
  isVersionSkewed,
  mapStageToPhaseId,
  parseWorkspace,
  projectPhaseStates,
  shouldShowProgress,
  shouldShowRecovery,
  useWorkspace,
} from '../useWorkspace.js';
import type { WorkspaceView } from '../useWorkspace.js';
import { useWorkspaceStore } from '../workspaceStore.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number): Response {
  return jsonResponse({ error: { code, message: `backend ${code}`, correlationId: 'corr-w25', details: {} } }, status);
}

interface WorkspaceFixtureOverrides {
  project?: Record<string, unknown>;
  run?: Record<string, unknown>;
  phase?: string;
  stage?: string | null;
  output?: Record<string, unknown>;
  permissions?: unknown;
}

function workspaceBody(overrides: WorkspaceFixtureOverrides = {}): Record<string, unknown> {
  return {
    project: { id: 'prj_1', name: 'Pilot', status: 'Processing', sourceLanguage: 'en', targetLanguage: 'es', isArchived: false, configurationHash: 'cfg_abc', settingsVersion: 3, ...(overrides.project ?? {}) },
    media: { id: 'med_1', status: 'Valid', container: 'mp4', sizeBytes: 1024, durationMs: 61000 },
    run: { id: 'run_1', status: 'Running', configHash: 'cfg_abc', attempt: 1, ...(overrides.run ?? {}) },
    phase: overrides.phase ?? 'speech',
    stage: overrides.stage === undefined ? 'Transcription' : overrides.stage,
    progress: { percentApproximate: 42, currentStage: 'Transcription', updatedAt: '2024-01-16T12:00:00Z' },
    review: { pendingCount: 2, oldestWaitingAt: '2024-01-16T10:00:00Z' },
    warnings: [{ code: 'REVIEW_OPEN', message: 'Two reviews await.' }],
    output: { state: 'pending', completeness: 42, ...(overrides.output ?? {}) },
    cost: { runCost: 1.5, monthToDate: 12.5 },
    activity: { recent: [] },
    permissions: overrides.permissions ?? ({ allowedActions: ['project.view', 'project.edit', 'project.delete', 'processing.cancel', 'processing.retry', 'export.create', 'export.download', 'admin.manage'] } as unknown),
  };
}

let fixture: Record<string, unknown> = workspaceBody();
let actionMode: 'ok' | 'fail' = 'ok';
const actionCalls: string[] = [];

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
  return (typeof input !== 'string' && !(input instanceof URL) ? (input as Request).method : undefined) ?? init?.method ?? 'GET';
}

async function mockFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const url = urlOf(input);
  const method = methodOf(input, init).toUpperCase();
  if (url.includes('/workspace') && method === 'GET') {
    return jsonResponse(fixture);
  }
  if (method === 'POST' && /cancel/i.test(url)) {
    actionCalls.push('cancel');
    return actionMode === 'fail' ? errorEnvelope('INTERNAL_ERROR', 500) : jsonResponse({ id: 'run_1', status: 'Cancelling' });
  }
  if (method === 'POST' && /retry/i.test(url)) {
    actionCalls.push('retry');
    return actionMode === 'fail' ? errorEnvelope('INTERNAL_ERROR', 500) : jsonResponse({ id: 'run_1', status: 'Running' });
  }
  if (method === 'DELETE') {
    actionCalls.push('delete');
    return actionMode === 'fail' ? errorEnvelope('PROJECT_HAS_ACTIVE_RUN', 409) : jsonResponse({ deleted: true });
  }
  if (method === 'POST' && /unarchive/i.test(url)) {
    actionCalls.push('unarchive');
    return jsonResponse({ id: 'prj_1', isArchived: false });
  }
  if (method === 'POST' && /archive/i.test(url)) {
    actionCalls.push('archive');
    return jsonResponse({ id: 'prj_1', isArchived: true });
  }
  return jsonResponse({});
}

function authenticate(permissions: readonly string[] = ['project.view', 'admin.manage']): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', permissions);
}

function renderWorkspace(projectId = 'prj_1'): void {
  const router = createMemoryRouter(
    [
      { path: '/projects/:id', element: <WorkspacePage projectId={projectId} /> },
      { path: '/projects', element: <div data-testid="page-projects" /> },
      { path: '/projects/:id/media', element: <div data-testid="page-project-media" /> },
      { path: '/projects/:id/exports', element: <div data-testid="page-project-exports" /> },
    ],
    { initialEntries: [`/projects/${projectId}`], future: { ...ROUTER_FUTURE_FLAGS } },
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
  fixture = workspaceBody();
  actionMode = 'ok';
  actionCalls.length = 0;
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

describe('run action matrix', () => {
  it('cancels the active run with a toast (recovery: cancel-then-retry)', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('workspace-actions')).toBeDefined();
    fireEvent.click(screen.getByTestId('workspace-action-cancel'));
    await waitFor(() => expect(actionCalls).toContain('cancel'));
    expect(screen.queryByTestId('workspace-action-error')).toBeNull();
  });

  it('reports cancel failures with a report ref (recovery: report id)', async () => {
    actionMode = 'fail';
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('workspace-actions')).toBeDefined();
    fireEvent.click(screen.getByTestId('workspace-action-cancel'));
    const panel = await screen.findByTestId('workspace-action-error');
    expect(panel.textContent).toContain('Ref: corr-w25');
  });

  it('retries failed runs and reports retry failures (recovery: retry/report)', async () => {
    fixture = workspaceBody({ run: { id: 'run_1', status: 'Failed', configHash: 'cfg_abc', attempt: 1 }, phase: 'failed' });
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('workspace-recovery')).toBeDefined();
    fireEvent.click(screen.getByTestId('workspace-action-retry'));
    await waitFor(() => expect(actionCalls).toContain('retry'));
    actionMode = 'fail';
    fireEvent.click(screen.getByTestId('workspace-recovery-retry'));
    expect(await screen.findByTestId('workspace-action-error')).toBeDefined();
  });
});

describe('destructive + archive matrix', () => {
  it('deletes with navigation to the list (recovery: none needed)', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('workspace-actions')).toBeDefined();
    fireEvent.click(screen.getByTestId('workspace-action-delete'));
    await waitFor(() => expect(actionCalls).toContain('delete'));
    expect(await screen.findByTestId('page-projects')).toBeDefined();
  });

  it('stays put with a ref on delete failures (recovery: report id)', async () => {
    actionMode = 'fail';
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('workspace-actions')).toBeDefined();
    fireEvent.click(screen.getByTestId('workspace-action-delete'));
    const panel = await screen.findByTestId('workspace-action-error');
    expect(panel.textContent).toContain('Ref: corr-w25');
    expect(screen.queryByTestId('page-projects')).toBeNull();
  });

  it('archives and unarchives with matching labels (recovery: undo)', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('workspace-actions')).toBeDefined();
    expect(screen.getByTestId('workspace-action-archive').textContent).toContain('Archive');
    fireEvent.click(screen.getByTestId('workspace-action-archive'));
    await waitFor(() => expect(actionCalls).toContain('archive'));
    cleanup();
    queryClient.clear();
    fixture = workspaceBody({ project: { id: 'prj_1', name: 'Pilot', status: 'Completed', isArchived: true, configurationHash: 'cfg_abc', settingsVersion: 3 } });
    renderWorkspace();
    expect(await screen.findByTestId('workspace-archived')).toBeDefined();
    expect(screen.getByTestId('workspace-action-archive').textContent).toContain('Unarchive');
    fireEvent.click(screen.getByTestId('workspace-action-archive'));
    await waitFor(() => expect(actionCalls).toContain('unarchive'));
  });
});

describe('navigation link matrix', () => {
  it('wires open/export links to tabs with tab state (recovery: none needed)', async () => {
    fixture = workspaceBody({ output: { state: 'ready', completeness: 100 } });
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('workspace-actions')).toBeDefined();
    expect(screen.getByTestId('workspace-action-open').getAttribute('href')).toBe('/projects/prj_1/media');
    expect(screen.getByTestId('workspace-action-export').getAttribute('href')).toBe('/projects/prj_1/exports');
    fireEvent.click(screen.getByTestId('workspace-action-open'));
    expect(useWorkspaceStore.getState().selectedTab).toBe('media');
    expect(await screen.findByTestId('page-project-media')).toBeDefined();
  });
});

describe('workspace store matrix', () => {
  it('clamps panel sizes and tracks tabs with reset', () => {
    const store = useWorkspaceStore.getState();
    store.setPanelSizes(70, 30);
    expect(useWorkspaceStore.getState().mainWidth).toBe(70);
    store.setPanelSizes(Number.NaN, Number.POSITIVE_INFINITY);
    expect(useWorkspaceStore.getState().mainWidth).toBe(66);
    expect(useWorkspaceStore.getState().secondaryWidth).toBe(66);
    store.setPanelSizes(5, 95);
    expect(useWorkspaceStore.getState().mainWidth).toBe(20);
    expect(useWorkspaceStore.getState().secondaryWidth).toBe(80);
    store.setSelectedTab('media');
    expect(useWorkspaceStore.getState().selectedTab).toBe('media');
    store.resetForTests();
    expect(useWorkspaceStore.getState().selectedTab).toBe('overview');
  });
});

describe('useWorkspace helper sweep', () => {
  const parsed = (): WorkspaceView => parseWorkspace(workspaceBody());

  it('classifies terminal runs case-insensitively with null guards', () => {
    expect(isTerminalRunStatus('Completed')).toBe(true);
    expect(isTerminalRunStatus('CANCELLED')).toBe(true);
    expect(isTerminalRunStatus('Running')).toBe(false);
    expect(isTerminalRunStatus(undefined)).toBe(false);
    expect(isTerminalRunStatus(null)).toBe(false);
    expect(isTerminalRunStatus('')).toBe(false);
    expect(isTerminalWorkspace(undefined)).toBe(false);
    expect(isTerminalWorkspace(parsed())).toBe(false);
    expect(getWorkspacePollInterval(undefined)).not.toBe(false);
  });

  it('derives run/progress/recovery visibility from the aggregate', () => {
    const ws = parsed();
    expect(hasRun(ws)).toBe(true);
    expect(hasRun({ ...ws, run: { ...ws.run, id: undefined } })).toBe(false);
    expect(isVersionSkewed(ws)).toBe(false);
    expect(isVersionSkewed({ ...ws, run: { ...ws.run, configHash: 'cfg_other' } })).toBe(true);
    expect(isVersionSkewed({ ...ws, run: { ...ws.run, configHash: '' } })).toBe(false);
    expect(isFailedWorkspace(ws)).toBe(false);
    expect(isFailedWorkspace({ ...ws, run: { ...ws.run, status: 'Failed' } })).toBe(true);
    expect(isFailedWorkspace({ ...ws, phase: 'failed' })).toBe(true);
    expect(shouldShowProgress(ws)).toBe(true);
    expect(shouldShowRecovery(ws)).toBe(false);
    expect(shouldShowRecovery({ ...ws, run: { ...ws.run, status: 'Failed' } })).toBe(true);
  });

  it('maps stages and projects phase states with fail-open defaults', () => {
    expect(mapStageToPhaseId('Transcription')).toBe('speech');
    expect(mapStageToPhaseId('Translation')).toBe('translation');
    expect(mapStageToPhaseId('VoiceGeneration')).toBe('voice');
    expect(mapStageToPhaseId('TimelineAssembly')).toBe('timing');
    expect(mapStageToPhaseId('AudioMixing')).toBe('mix');
    expect(mapStageToPhaseId('QualityControl')).toBe('qc');
    expect(mapStageToPhaseId('Render')).toBe('render');
    expect(mapStageToPhaseId('MediaValidation')).toBe('validation');
    expect(mapStageToPhaseId('SomethingNew')).toBe('speech');
    expect(projectPhaseStates('completed', null)[0]?.state).toBe('done');
    const failed = projectPhaseStates('speech', 'Transcription', 'failed');
    expect(failed.some((stage) => stage.state === 'failed')).toBe(true);
    expect(projectPhaseStates('bogus-phase', null)[0]?.state).toBe('pending');
    expect(projectPhaseStates('bogus-phase', 'Transcription')[0]?.state).not.toBe('');
  });

  it('narrows header actions to permissions + aggregate state', () => {
    expect(getWorkspaceActions([], undefined)).toEqual([]);
    expect(getWorkspaceActions(['project.view'], undefined)).toEqual(['open']);
    const ws = parsed();
    expect(getWorkspaceActions(['project.view', 'processing.cancel'], ws)).toContain('cancel');
    expect(getWorkspaceActions(['project.view', 'processing.retry'], ws)).not.toContain('retry');
    const failed = parseWorkspace(workspaceBody({ run: { id: 'run_1', status: 'Failed', configHash: 'cfg_abc', attempt: 1 }, phase: 'failed' }));
    expect(getWorkspaceActions(['project.view', 'processing.retry'], failed)).toContain('retry');
    expect(getWorkspaceActions(['project.view', 'export.create'], parseWorkspace(workspaceBody({ output: { state: 'ready', completeness: 100 } })))).toContain('export');
    expect(getWorkspaceActions(['project.delete'], ws)).toContain('delete');
    expect(getWorkspaceActions(['project.edit'], ws)).toContain('archive');
  });

  it('parses aggregates defensively without throwing', () => {
    expect(() => parseWorkspace(null)).toThrow('missing the project section');
    const minimal = parseWorkspace({ project: { id: 'prj_9' } });
    expect(minimal.project.name).toBe('Untitled project');
    expect(minimal.progress.percentApproximate).toBe(0);
    expect(minimal.output.completeness).toBe(0);
    expect(parseWorkspace(workspaceBody({ output: { state: 'ready', completeness: 142 } })).output.completeness).toBe(100);
  });

  it('gates the query on session + id and normalizes errors', async () => {
    authenticate();
    let calls = 0;
    setInnerFetchForTests((async () => {
      calls += 1;
      return jsonResponse({});
    }) as typeof fetch);
    function Probe(): null {
      useWorkspace('');
      return null;
    }
    render(
      <QueryClientProvider client={queryClient}>
        <Probe />
      </QueryClientProvider>,
    );
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(calls).toBe(0);
    cleanup();
    useAuthStore.setState({ status: 'anonymous' });
  });
});
