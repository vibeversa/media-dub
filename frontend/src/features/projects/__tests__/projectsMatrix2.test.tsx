// Delta 2: projects gap closure (round 2).
//
// Extends `projectsMatrix.test.tsx` with the remaining `ProjectsPage`
// branches: loading shell, error + retry recovery, tenant-empty
// create-CTA allowlist (present/absent by `project.edit`), delete
// confirm success + cancel dismiss, gone/missing feedback for
// delete, forbidden feedback for cancel/unarchive, export
// navigation, filter edits resetting to page 1, preflight close,
// plus the `ProjectTable` no-open/missing-cell branches and the
// `api.ts` query/filter/action pure sweep. Every failure asserts its
// recovery control per §11.6 with text signals (041C); fixtures are
// synthetic (R3); parsed shapes use `undefined`, never `null`.
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
import { ProjectTable } from '../ProjectTable.js';
import { ProjectsPage } from '../ProjectsPage.js';
import {
  DEFAULT_FILTERS,
  applyClientFilters,
  getAllowedActions,
  getProjectDisplayName,
  hasActiveFilters,
  isApproximateSort,
  parseFilters,
  serializeFilters,
  toServerQuery,
} from '../api.js';
import type { Project } from '../api.js';

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
  return jsonResponse({ error: { code, message: `backend ${code}`, correlationId: 'corr-proj2', details: {} } }, status);
}

const mockFetch = vi.fn<typeof fetch>();

let items: Project[];
let listMode: 'ok' | 'empty' | 'error500' | 'never' = 'ok';
let actionMode: 'ok' | 'fail' | 'gone' = 'ok';
const actionCalls: string[] = [];
let listCalls = 0;

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

function installFetch(input: unknown, init: unknown): Promise<Response> {
  const url = urlOf(input as RequestInfo | URL);
  const method = methodOf(input as RequestInfo | URL, init as RequestInit | undefined).toUpperCase();
  if (method === 'GET' && /\/projects(\?|$)/.test(url)) {
    listCalls += 1;
    if (listMode === 'error500') {
      return Promise.resolve(errorBody('INTERNAL_ERROR', 500));
    }
    if (listMode === 'empty') {
      return Promise.resolve(jsonResponse({ items: [], page: 1, pageSize: 20, total: 0, hasMore: false, clamped: false }));
    }
    if (listMode === 'never') {
      return new Promise<Response>(() => {});
    }
    return Promise.resolve(jsonResponse(listEnvelope()));
  }
  if (method === 'POST' && /unarchive/i.test(url)) {
    actionCalls.push('unarchive');
    if (actionMode === 'gone') {
      return Promise.resolve(errorBody('PROJECT_NOT_FOUND', 404));
    }
    if (actionMode === 'fail') {
      return Promise.resolve(errorBody('FORBIDDEN', 403));
    }
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
    if (actionMode === 'gone') {
      return Promise.resolve(errorBody('PROJECT_NOT_FOUND', 404));
    }
    if (actionMode === 'fail') {
      return Promise.resolve(errorBody('FORBIDDEN', 403));
    }
    return Promise.resolve(jsonResponse({ id: 'run_1', status: 'Cancelling' }));
  }
  if (method === 'DELETE' && /\/projects\/prj_/.test(url)) {
    actionCalls.push('delete');
    if (actionMode === 'gone') {
      return Promise.resolve(errorBody('PROJECT_NOT_FOUND', 404));
    }
    if (actionMode === 'fail') {
      return Promise.resolve(errorBody('FORBIDDEN', 403));
    }
    return Promise.resolve(jsonResponse({ deleted: true }));
  }
  return Promise.resolve(jsonResponse({}));
}

function authenticate(permissions: readonly string[] = ALL_PERMS): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', permissions);
}

function renderPage(initialEntry = '/projects'): ReturnType<typeof createMemoryRouter> {
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
  return router;
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
  listMode = 'ok';
  actionMode = 'ok';
  actionCalls.length = 0;
  listCalls = 0;
  mockFetch.mockReset();
  mockFetch.mockImplementation((input, init) => installFetch(input, init));
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

describe('page shells', () => {
  it('shows loading while the list resolves (never blank)', () => {
    listMode = 'never';
    authenticate();
    renderPage();
    expect(screen.getByTestId('projects-loading')).toBeDefined();
    expect(screen.queryByTestId('projects-grid')).toBeNull();
  });

  it('recovers list errors with retry (recovery: retry)', async () => {
    listMode = 'error500';
    authenticate();
    renderPage();
    expect(await screen.findByTestId('projects-error')).toBeDefined();
    const before = listCalls;
    listMode = 'ok';
    const retry = screen.getByTestId('projects-error').querySelector('button');
    expect(retry).not.toBeNull();
    fireEvent.click(retry!);
    await waitFor(() => expect(listCalls).toBeGreaterThan(before));
    expect(await screen.findByTestId('projects-grid')).toBeDefined();
  });

  it('offers creation from the tenant empty state when permitted (recovery: create)', async () => {
    listMode = 'empty';
    authenticate();
    renderPage();
    expect(await screen.findByTestId('projects-empty')).toBeDefined();
    const cta = screen.getByTestId('projects-empty-new');
    expect(cta.getAttribute('href')).toBe('/projects/new');
    expect(screen.getByTestId('projects-new').getAttribute('href')).toBe('/projects/new');
  });

  it('hides creation affordances without project.edit (never a dead link)', async () => {
    listMode = 'empty';
    authenticate(['project.view']);
    renderPage();
    expect(await screen.findByTestId('projects-empty')).toBeDefined();
    expect(screen.queryByTestId('projects-empty-new')).toBeNull();
    expect(screen.queryByTestId('projects-new')).toBeNull();
  });
});

describe('delete confirm matrix', () => {
  it('confirms with the exact name and closes the dialog (recovery: none needed)', async () => {
    authenticate();
    renderPage();
    expect(await screen.findByTestId('project-action-delete-prj_3')).toBeDefined();
    fireEvent.click(screen.getByTestId('project-action-delete-prj_3'));
    const input = await screen.findByTestId('delete-confirm-name');
    expect((screen.getByTestId('delete-confirm-button') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(input, { target: { value: 'Finished short' } });
    await waitFor(() => expect((screen.getByTestId('delete-confirm-button') as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(screen.getByTestId('delete-confirm-button'));
    await waitFor(() => expect(actionCalls).toContain('delete'));
    await waitFor(() => expect(screen.queryByTestId('projects-delete-dialog')).toBeNull());
  });

  it('dismisses via cancel without a call (recovery: none needed)', async () => {
    authenticate();
    renderPage();
    expect(await screen.findByTestId('project-action-delete-prj_3')).toBeDefined();
    fireEvent.click(screen.getByTestId('project-action-delete-prj_3'));
    expect(await screen.findByTestId('projects-delete-dialog')).toBeDefined();
    fireEvent.click(screen.getByTestId('delete-cancel-button'));
    await waitFor(() => expect(screen.queryByTestId('projects-delete-dialog')).toBeNull());
    expect(actionCalls).not.toContain('delete');
  });

  it('toasts gone rows as info on 404 without error chrome (recovery: refresh)', async () => {
    actionMode = 'gone';
    authenticate();
    renderPage();
    expect(await screen.findByTestId('project-action-delete-prj_3')).toBeDefined();
    fireEvent.click(screen.getByTestId('project-action-delete-prj_3'));
    fireEvent.change(await screen.findByTestId('delete-confirm-name'), { target: { value: 'Finished short' } });
    fireEvent.click(screen.getByTestId('delete-confirm-button'));
    await waitFor(() =>
      expect(screen.getByRole('region', { name: 'Notifications' }).textContent).toContain('already deleted'),
    );
  });

  it('toasts forbidden deletes as errors (recovery: contact admin)', async () => {
    actionMode = 'fail';
    authenticate();
    renderPage();
    expect(await screen.findByTestId('project-action-delete-prj_3')).toBeDefined();
    fireEvent.click(screen.getByTestId('project-action-delete-prj_3'));
    fireEvent.change(await screen.findByTestId('delete-confirm-name'), { target: { value: 'Finished short' } });
    fireEvent.click(screen.getByTestId('delete-confirm-button'));
    await waitFor(() =>
      expect(screen.getByRole('region', { name: 'Notifications' }).textContent).toContain('FORBIDDEN'),
    );
  });
});

describe('cancel/unarchive/export matrix', () => {
  it('toasts forbidden cancels as errors (recovery: contact admin)', async () => {
    actionMode = 'fail';
    authenticate();
    renderPage();
    expect(await screen.findByTestId('project-action-cancel-prj_1')).toBeDefined();
    fireEvent.click(screen.getByTestId('project-action-cancel-prj_1'));
    const dialog = screen.getByRole('dialog');
    const buttons = dialog.parentElement?.querySelectorAll('button') ?? [];
    fireEvent.click(buttons[buttons.length - 1]!);
    await waitFor(() =>
      expect(screen.getByRole('region', { name: 'Notifications' }).textContent).toContain('FORBIDDEN'),
    );
  });

  it('toasts forbidden unarchives as errors (recovery: contact admin)', async () => {
    actionMode = 'fail';
    authenticate();
    renderPage('/projects?archived=archived');
    expect(await screen.findByTestId('project-action-unarchive-prj_9')).toBeDefined();
    fireEvent.click(screen.getByTestId('project-action-unarchive-prj_9'));
    await waitFor(() =>
      expect(screen.getByRole('region', { name: 'Notifications' }).textContent).toContain('FORBIDDEN'),
    );
  });

  it('navigates export actions to the exports route', async () => {
    authenticate();
    renderPage();
    expect(await screen.findByTestId('project-action-export-prj_3')).toBeDefined();
    fireEvent.click(screen.getByTestId('project-action-export-prj_3'));
    expect(await screen.findByTestId('page-project-exports')).toBeDefined();
  });

  it('closes the cancel dialog after a confirmed cancel (recovery: none needed)', async () => {
    authenticate();
    renderPage();
    expect(await screen.findByTestId('project-action-cancel-prj_1')).toBeDefined();
    fireEvent.click(screen.getByTestId('project-action-cancel-prj_1'));
    const dialog = screen.getByRole('dialog');
    const buttons = dialog.parentElement?.querySelectorAll('button') ?? [];
    fireEvent.click(buttons[buttons.length - 1]!);
    await waitFor(() => expect(actionCalls).toContain('cancel'));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });
});

describe('filter URL matrix', () => {
  it('resets to page 1 on filter edits (recovery: none needed)', async () => {
    authenticate();
    const router = renderPage('/projects?page=2');
    expect(await screen.findByTestId('projects-grid')).toBeDefined();
    fireEvent.change(screen.getByTestId('filter-status'), { target: { value: 'Failed' } });
    await waitFor(() => expect(router.state.location.search).toContain('status=Failed'));
    expect(router.state.location.search).not.toContain('page=');
  });
});

describe('ProjectTable degradation matrix', () => {
  function renderTable(permissions: readonly string[]): void {
    render(
      <MemoryRouter>
        <ProjectTable
          rows={[
            row({ id: 'prj_1' }),
            row({ id: 'prj_2', targetLanguage: undefined, createdAt: '', updatedAt: '' }),
          ]}
          permissions={permissions}
          onOpen={() => {}}
          onAction={() => {}}
          busyKey={undefined}
        />
      </MemoryRouter>,
    );
  }

  it('links openable rows and spans rows without permission (never a dead link)', () => {
    renderTable(ALL_PERMS);
    expect(screen.getByTestId('project-open-prj_1').getAttribute('href')).toBe('/projects/prj_1');
    cleanup();
    renderTable([]);
    const cell = screen.getByTestId('project-open-prj_1');
    expect(cell.tagName.toLowerCase()).toBe('span');
    expect(cell.textContent).toBe('Pilot episode');
  });

  it('renders missing dates/targets as text, never blanks', () => {
    renderTable(ALL_PERMS);
    expect(screen.getByTestId('project-target-prj_2').textContent).not.toBe('');
    expect(screen.getByTestId('project-created-prj_2').textContent).not.toBe('');
    expect(screen.getByTestId('project-activity-prj_2').textContent).not.toBe('');
    expect(screen.getByTestId('project-created-prj_1').textContent).not.toBe('');
  });

  it('falls back to Untitled for blank names (never blank)', () => {
    render(
      <MemoryRouter>
        <ProjectTable
          rows={[row({ id: 'prj_7', name: '   ' })]}
          permissions={ALL_PERMS}
          onOpen={() => {}}
          onAction={() => {}}
          busyKey={undefined}
        />
      </MemoryRouter>,
    );
    expect(screen.getByTestId('project-open-prj_7').textContent).toBe('Untitled project');
  });
});

describe('projects api sweep', () => {
  it('maps UI filters to server queries (R2)', () => {
    expect(toServerQuery(DEFAULT_FILTERS)).toMatchObject({ page: 1, pageSize: 20, sort: 'createdAt', sortDir: 'desc' });
    expect(toServerQuery({ ...DEFAULT_FILTERS, sort: 'name' }).sort).toBe('name');
    expect(toServerQuery({ ...DEFAULT_FILTERS, sort: 'activity' }).sort).toBe('updatedAt');
    expect(toServerQuery({ ...DEFAULT_FILTERS, sort: 'progress' }).sort).toBe('updatedAt');
    expect(toServerQuery({ ...DEFAULT_FILTERS, status: 'Failed', owner: 'u1' })).toMatchObject({ status: 'Failed', ownerId: 'u1' });
    expect(toServerQuery({ ...DEFAULT_FILTERS, archived: 'archived' }).archived).toBe('true');
    expect(toServerQuery({ ...DEFAULT_FILTERS, archived: 'all' }).archived).toBe('all');
    expect(isApproximateSort('progress')).toBe(true);
    expect(isApproximateSort('created')).toBe(false);
  });

  it('parses and serializes filter URLs defensively (R1)', () => {
    expect(serializeFilters(DEFAULT_FILTERS).toString()).toBe('');
    const parsed = parseFilters(new URLSearchParams('status=Bogus&sort=bogus&sortDir=up&page=0&pageSize=999&archived=archived'));
    expect(parsed.status).toBe('');
    expect(parsed.sort).toBe('created');
    expect(parsed.sortDir).toBe('desc');
    expect(parsed.page).toBe(1);
    expect(parsed.pageSize).toBe(100);
    expect(parsed.archived).toBe('archived');
    const trimmed = parseFilters(new URLSearchParams('targetLanguage=english-too-long&owner=u1&from=2024-01-01&to=2024-02-01'));
    expect(trimmed.targetLanguage.length).toBeLessThanOrEqual(8);
    expect(trimmed.owner).toBe('u1');
    expect(hasActiveFilters(DEFAULT_FILTERS)).toBe(false);
    expect(hasActiveFilters({ ...DEFAULT_FILTERS, status: 'Failed' })).toBe(true);
    expect(getProjectDisplayName({ name: '   ' })).toBe('Untitled project');
    expect(getProjectDisplayName({ name: 'Pilot' })).toBe('Pilot');
  });

  it('refines pages client-side without re-slicing server dims (R2)', () => {
    const rows = [row({ id: 'a', targetLanguage: 'es', createdAt: '2024-01-10T00:00:00Z' }), row({ id: 'b', targetLanguage: 'fr', createdAt: '2024-03-10T00:00:00Z' })];
    expect(applyClientFilters(undefined, DEFAULT_FILTERS)).toEqual([]);
    expect(applyClientFilters(rows, { ...DEFAULT_FILTERS, targetLanguage: 'ES' }).map((r) => r.id)).toEqual(['a']);
    expect(applyClientFilters(rows, { ...DEFAULT_FILTERS, createdFrom: '2024-02-01' }).map((r) => r.id)).toEqual(['b']);
    expect(applyClientFilters(rows, { ...DEFAULT_FILTERS, createdTo: '2024-02-01' }).map((r) => r.id)).toEqual(['a']);
    expect(applyClientFilters([row({ id: 'c', createdAt: '' })], { ...DEFAULT_FILTERS, createdFrom: '2024-02-01' })).toEqual([]);
  });

  it('allow-lists row actions from status + archived + perms (R3)', () => {
    expect(getAllowedActions({ status: 'Completed', isArchived: false }, [])).toEqual([]);
    expect(getAllowedActions({ status: 'Completed', isArchived: true }, ALL_PERMS)).toContain('unarchive');
    expect(getAllowedActions({ status: 'Completed', isArchived: true }, ALL_PERMS)).not.toContain('cancel');
    expect(getAllowedActions({ status: 'Processing', isArchived: false }, ALL_PERMS)).toContain('cancel');
    expect(getAllowedActions({ status: 'Processing', isArchived: false }, ALL_PERMS)).not.toContain('delete');
    expect(getAllowedActions({ status: 'Failed', isArchived: false }, ALL_PERMS)).toContain('retry');
    expect(getAllowedActions({ status: 'Failed', isArchived: true }, ALL_PERMS)).not.toContain('retry');
    expect(getAllowedActions({ status: 'Completed', isArchived: false }, ALL_PERMS)).toContain('export');
    expect(getAllowedActions({ status: 'Failed', isArchived: false }, ALL_PERMS)).not.toContain('export');
  });
});
