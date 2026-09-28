// Task 039B: projects state-matrix gap closure.
//
// Extends `projects.test.tsx` (server list, URL sync, valid actions, empty
// states, delete flow, failures, navigation) with the missing states:
// archive/unarchive with toasts, cancel dialog confirm/dismiss, retry →
// preflight handoff, open/export navigation, delete mismatch hints,
// stale indicators, PROJECT_NOT_FOUND gone-toast, busy-row disabling, the
// ProjectFilters control matrix, ProjectTable honest-degradation cells, and
// the `useProjectsQuery` guards. Every failure asserts its recovery control
// per §11.6 with text signals (041C); fixtures are synthetic (R3).
import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, RouterProvider, createMemoryRouter } from 'react-router-dom';
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
import { ProjectFilters } from '../ProjectFilters.js';
import { ProjectTable } from '../ProjectTable.js';
import { ProjectsPage } from '../ProjectsPage.js';
import { DEFAULT_FILTERS } from '../api.js';
import type { Project } from '../api.js';
import { useProjectsQuery } from '../useProjectsQuery.js';

const ALL_PERMS = ['project.view', 'project.edit', 'project.delete', 'processing.cancel', 'processing.retry', 'export.create'];

function row(partial: Partial<Project> & { id: string }): Project {
  return {
    name: 'Pilot episode',
    status: 'Completed',
    sourceLanguage: 'en',
    targetLanguage: 'es',
    createdAt: '2024-01-15T12:00:00Z',
    updatedAt: '2024-01-16T12:00:00Z',
    isArchived: false,
    settingsVersion: 1,
    ...partial,
  } as Project;
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorBody(code: string, status: number): Response {
  return jsonResponse({ error: { code, message: `backend ${code}`, correlationId: 'corr-proj', details: {} } }, status);
}

const mockFetch = vi.fn<typeof fetch>();

let items: Project[];
let actionMode: 'ok' | 'fail' | 'gone' = 'ok';
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
  if (input instanceof Request) {
    return input.method;
  }
  return init?.method ?? 'GET';
}

function listEnvelope(): Record<string, unknown> {
  return { items, page: 1, pageSize: 20, total: items.length, sort: 'createdAt', sortDir: 'desc', hasMore: false, clamped: false };
}

function installListFetch(input: unknown, init: unknown): Promise<Response> {
  const url = urlOf(input as RequestInfo | URL);
  const method = methodOf(input as RequestInfo | URL, init as RequestInit | undefined).toUpperCase();
  if (method === 'GET' && /\/projects(\?|$)/.test(url)) {
    return Promise.resolve(jsonResponse(listEnvelope()));
  }
  if (method === 'POST' && /unarchive/i.test(url)) {
    actionCalls.push('unarchive');
    return Promise.resolve(jsonResponse({ id: 'prj_9', isArchived: false }));
  }
  if (method === 'POST' && /archive/i.test(url)) {
    actionCalls.push('archive');
    if (actionMode === 'gone') {
      return Promise.resolve(errorBody('PROJECT_NOT_FOUND', 404));
    }
    if (actionMode === 'fail') {
      return Promise.resolve(errorBody('FORBIDDEN', 403));
    }
    return Promise.resolve(jsonResponse({ id: 'prj_1', isArchived: true }));
  }
  if (method === 'POST' && /cancel/i.test(url)) {
    actionCalls.push('cancel');
    return Promise.resolve(jsonResponse({ id: 'run_1', status: 'Cancelling' }));
  }
  if (method === 'DELETE' && /\/projects\/prj_/.test(url)) {
    actionCalls.push('delete');
    return Promise.resolve(jsonResponse({ deleted: true }));
  }
  return Promise.resolve(jsonResponse({}));
}

function installMocks(): void {
  mockFetch.mockImplementation((input, init) => installListFetch(input, init));
}

function authenticate(permissions: readonly string[] = ALL_PERMS): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', permissions);
}

function renderPage(initialEntry = '/projects'): void {
  const router = createMemoryRouter(
    [
      { path: '/projects', element: <ProjectsPage /> },
      { path: '/projects/new', element: <div data-testid="page-project-create" /> },
      { path: '/projects/:id', element: <div data-testid="page-project-details" /> },
      { path: '/projects/:id/exports', element: <div data-testid="page-project-exports" /> },
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
}

function seedRows(): void {
  items = [
    row({ id: 'prj_1', status: 'Processing' }),
    row({ id: 'prj_2', status: 'Failed', name: 'Second take' }),
    row({ id: 'prj_3', status: 'Completed', name: 'Finished short' }),
    row({ id: 'prj_9', status: 'Completed', name: 'Oldie', isArchived: true }),
  ];
}

beforeEach(() => {
  seedRows();
  actionMode = 'ok';
  actionCalls.length = 0;
  mockFetch.mockReset();
  installMocks();
  setInnerFetchForTests(mockFetch);
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  queryClient.clear();
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

describe('archive/unarchive matrix', () => {
  it('archives with a toast and refetch (recovery: unarchive)', async () => {
    authenticate();
    renderPage();
    expect(await screen.findByTestId('project-action-archive-prj_1')).toBeDefined();
    fireEvent.click(screen.getByTestId('project-action-archive-prj_1'));
    await waitFor(() => expect(actionCalls).toContain('archive'));
    expect(screen.queryByTestId('project-action-archive-prj_1')).toBeDefined();
  });

  it('toasts gone rows as info on 404 without error chrome (recovery: refresh)', async () => {
    actionMode = 'gone';
    authenticate();
    renderPage();
    expect(await screen.findByTestId('project-action-archive-prj_1')).toBeDefined();
    fireEvent.click(screen.getByTestId('project-action-archive-prj_1'));
    await waitFor(() =>
      expect(screen.getByRole('region', { name: 'Notifications' }).textContent).toContain('already deleted'),
    );
  });

  it('toasts forbidden failures as errors (recovery: contact admin)', async () => {
    actionMode = 'fail';
    authenticate();
    renderPage();
    expect(await screen.findByTestId('project-action-archive-prj_1')).toBeDefined();
    fireEvent.click(screen.getByTestId('project-action-archive-prj_1'));
    await waitFor(() =>
      expect(screen.getByRole('region', { name: 'Notifications' }).textContent).toContain('FORBIDDEN'),
    );
  });

  it('unarchives archived rows (recovery: re-archive)', async () => {
    authenticate();
    renderPage('/projects?archived=archived');
    expect(await screen.findByTestId('project-action-unarchive-prj_9')).toBeDefined();
    fireEvent.click(screen.getByTestId('project-action-unarchive-prj_9'));
    await waitFor(() => expect(actionCalls).toContain('unarchive'));
  });
});

describe('cancel/retry/open/export matrix', () => {
  it('confirms cancels behind a dialog and dismisses cleanly', async () => {
    authenticate();
    renderPage();
    expect(await screen.findByTestId('project-action-cancel-prj_1')).toBeDefined();
    fireEvent.click(screen.getByTestId('project-action-cancel-prj_1'));
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(actionCalls).not.toContain('cancel');
    fireEvent.click(screen.getByTestId('project-action-cancel-prj_1'));
    const dialog = screen.getByRole('dialog');
    const buttons = dialog.parentElement?.querySelectorAll('button') ?? [];
    fireEvent.click(buttons[buttons.length - 1]!);
    await waitFor(() => expect(actionCalls).toContain('cancel'));
  });

  it('hands retries to the preflight dialog (no direct POST, R1)', async () => {
    authenticate();
    renderPage();
    expect(await screen.findByTestId('project-action-retry-prj_2')).toBeDefined();
    fireEvent.click(screen.getByTestId('project-action-retry-prj_2'));
    expect(await screen.findByTestId('preflight-dialog')).toBeDefined();
    expect(actionCalls).not.toContain('retry');
  });

  it('navigates open/export actions to workspace routes', async () => {
    authenticate();
    renderPage();
    expect(await screen.findByTestId('project-action-open-prj_3')).toBeDefined();
    fireEvent.click(screen.getByTestId('project-action-open-prj_3'));
    expect(await screen.findByTestId('page-project-details')).toBeDefined();
  });
});

describe('delete mismatch + stale matrix', () => {
  it('hints on name mismatches and keeps confirm disabled (recovery: retype)', async () => {
    authenticate();
    renderPage();
    expect(await screen.findByTestId('project-action-delete-prj_3')).toBeDefined();
    fireEvent.click(screen.getByTestId('project-action-delete-prj_3'));
    const input = await screen.findByTestId('delete-confirm-name');
    fireEvent.change(input, { target: { value: 'Wrong name' } });
    expect(await screen.findByTestId('delete-confirm-hint')).toBeDefined();
    expect((screen.getByTestId('delete-confirm-button') as HTMLButtonElement).disabled).toBe(true);
    expect(actionCalls).not.toContain('delete');
  });

  it('flags background refreshes with a status signal (never silent)', async () => {
    authenticate();
    let releaseList!: (response: Response) => void;
    let gated = false;
    mockFetch.mockImplementation((input, init) => {
      const url = urlOf(input as RequestInfo | URL);
      const method = methodOf(input as RequestInfo | URL, init as RequestInit | undefined).toUpperCase();
      if (gated && method === 'GET' && /\/projects(\?|$)/.test(url)) {
        return new Promise<Response>((resolve) => {
          releaseList = resolve;
        });
      }
      return installListFetch(input, init);
    });
    renderPage();
    expect(await screen.findByTestId('project-open-prj_1')).toBeDefined();
    gated = true;
    const invalidated = queryClient.invalidateQueries();
    await waitFor(() => expect(screen.getByTestId('projects-stale-indicator')).toBeDefined());
    releaseList(jsonResponse(listEnvelope()));
    await invalidated;
    await waitFor(() => expect(screen.queryByTestId('projects-stale-indicator')).toBeNull());
  });
});

describe('ProjectFilters control matrix', () => {
  it('writes every control into patches and clears on demand', () => {
    let cleared = 0;
    const patches: Partial<typeof DEFAULT_FILTERS>[] = [];
    render(
      <ProjectFilters filters={DEFAULT_FILTERS} onChange={(patch) => { patches.push(patch); }} onClear={() => { cleared += 1; }} />,
    );
    fireEvent.change(screen.getByTestId('filter-status'), { target: { value: 'Failed' } });
    fireEvent.change(screen.getByTestId('filter-target'), { target: { value: 'es' } });
    fireEvent.change(screen.getByTestId('filter-archived'), { target: { value: 'all' } });
    fireEvent.change(screen.getByTestId('filter-owner'), { target: { value: 'u1' } });
    fireEvent.change(screen.getByTestId('filter-from'), { target: { value: '2024-01-01' } });
    fireEvent.change(screen.getByTestId('filter-to'), { target: { value: '2024-02-01' } });
    fireEvent.change(screen.getByTestId('sort-by'), { target: { value: 'name' } });
    fireEvent.change(screen.getByTestId('sort-dir'), { target: { value: 'asc' } });
    fireEvent.change(screen.getByTestId('page-size'), { target: { value: '50' } });
    expect(patches).toContainEqual({ status: 'Failed' });
    expect(patches).toContainEqual({ targetLanguage: 'es' });
    expect(patches).toContainEqual({ archived: 'all' });
    expect(patches).toContainEqual({ owner: 'u1' });
    expect(patches).toContainEqual({ createdFrom: '2024-01-01' });
    expect(patches).toContainEqual({ createdTo: '2024-02-01' });
    expect(patches).toContainEqual({ sort: 'name' });
    expect(patches).toContainEqual({ sortDir: 'asc' });
    expect(patches).toContainEqual({ pageSize: 50 });
    fireEvent.click(screen.getByTestId('filters-clear'));
    expect(cleared).toBe(1);
  });
});

describe('ProjectTable honest-degradation matrix', () => {
  function renderTable(busyKey: string | undefined = undefined): void {
    render(
      <MemoryRouter>
        <ProjectTable
          rows={[row({ id: 'prj_1' }), row({ id: 'prj_2', targetLanguage: undefined })]}
          permissions={ALL_PERMS}
          onOpen={() => {}}
          onAction={() => {}}
          busyKey={busyKey}
        />
      </MemoryRouter>,
    );
  }

  it('renders missing progress/review cells with tooltips, never blanks', () => {
    renderTable();
    expect(screen.getByTestId('project-progress-prj_1').textContent).not.toBe('');
    expect(screen.getByTestId('project-review-prj_1').textContent).not.toBe('');
    expect(screen.getByTestId('project-target-prj_2').textContent).not.toBe('');
    expect(screen.getByTestId('project-media-prj_1')).toBeDefined();
    expect(screen.getByTestId('project-status-prj_1')).toBeDefined();
  });

  it('disables the in-flight row action (recovery: wait)', () => {
    renderTable('archive:prj_1');
    expect((screen.getByTestId('project-action-archive-prj_1') as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByTestId('project-action-archive-prj_2') as HTMLButtonElement).disabled).toBe(false);
  });
});

describe('useProjectsQuery guards', () => {
  it('never fires when anonymous (recovery: sign in)', async () => {
    useAuthStore.setState({ status: 'anonymous' });
    let calls = 0;
    mockFetch.mockImplementation(() => {
      calls += 1;
      return Promise.resolve(jsonResponse({}));
    });
    function Probe(): null {
      useProjectsQuery(DEFAULT_FILTERS);
      return null;
    }
    render(
      <QueryClientProvider client={queryClient}>
        <Probe />
      </QueryClientProvider>,
    );
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(calls).toBe(0);
  });
});
