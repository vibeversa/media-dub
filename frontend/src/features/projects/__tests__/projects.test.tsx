import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { RouterProvider, createMemoryRouter } from 'react-router-dom';
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
import { ProjectsPage } from '../ProjectsPage.js';
import type { Project } from '../api.js';

const ALL_PERMS = [
  'project.view',
  'project.edit',
  'project.delete',
  'processing.cancel',
  'processing.retry',
  'export.create',
];

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

function envelope(items: Project[], overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    items,
    page: 1,
    pageSize: 20,
    total: items.length,
    sort: 'createdAt',
    sortDir: 'desc',
    hasMore: false,
    clamped: false,
    ...overrides,
  };
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function errorBody(code: string): unknown {
  return { error: { code, message: `backend ${code}`, correlationId: 'corr-proj', details: {} } };
}

const mockFetch = vi.fn<typeof fetch>();

let items: Project[];
let archiveStatus = 200;
let archiveBody: unknown = null;

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

function installListMock(): void {
  mockFetch.mockImplementation((input, init) => {
    const url = urlOf(input as RequestInfo | URL);
    const method = methodOf(input as RequestInfo | URL, init as RequestInit | undefined);
    if (method === 'DELETE' && /\/projects\/prj_/.test(url)) {
      const id = url.split('/').pop()?.split('?')[0] ?? '';
      items = items.filter((i) => i.id !== id);
      return Promise.resolve(jsonResponse({ id, deleted: true }, 202));
    }
    if (method === 'POST' && url.includes('/archive')) {
      const body = archiveBody ?? { id: 'prj_x', name: 'x', status: 'Completed', settingsVersion: 2 };
      return Promise.resolve(jsonResponse(body, archiveStatus));
    }
    if (method === 'POST' && url.includes('/unarchive')) {
      return Promise.resolve(jsonResponse({ id: 'prj_x', name: 'x', status: 'Completed', settingsVersion: 2 }));
    }
    if (method === 'POST' && url.includes('/processing/cancel')) {
      return Promise.resolve(jsonResponse({}));
    }
    if (method === 'POST' && url.includes('/processing') && !url.includes('/cancel') && !url.includes('/retry')) {
      return Promise.resolve(jsonResponse({}, 202));
    }
    if (url.includes('/api/v1/projects?') || url.endsWith('/api/v1/projects')) {
      const parsed = new URL(url);
      const page = Number.parseInt(parsed.searchParams.get('page') ?? '1', 10);
      const pageSize = Number.parseInt(parsed.searchParams.get('pageSize') ?? '20', 10);
      return Promise.resolve(jsonResponse(envelope(items, { page, pageSize })));
    }
    return Promise.resolve(jsonResponse({}));
  });
}

function renderProjects(initialEntry = '/projects') {
  const router = createMemoryRouter(
    [
      { path: '/projects', element: <ProjectsPage /> },
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

function authenticate(permissions: readonly string[] = ALL_PERMS): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', permissions);
}

function listCalls(): string[] {
  return mockFetch.mock.calls
    .map((call) => urlOf(call[0] as RequestInfo | URL))
    .filter((url) => url.includes('/api/v1/projects') && !/\/projects\/prj_/.test(url));
}

beforeEach(() => {
  mockFetch.mockReset();
  setInnerFetchForTests(mockFetch);
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  queryClient.clear();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  items = [
    row({ id: 'prj_1', name: 'Pilot episode', status: 'Completed' }),
    row({ id: 'prj_2', name: 'Trailer', status: 'Processing', updatedAt: '2024-01-17T12:00:00Z' }),
  ];
  archiveStatus = 200;
  archiveBody = null;
  installListMock();
});

afterEach(() => {
  cleanup();
  restoreInnerFetchForTests();
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  queryClient.clear();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  vi.restoreAllMocks();
});

describe('server-side list (R2)', () => {
  it('renders all columns and sends pagination/sort to the server', async () => {
    authenticate();
    renderProjects();
    expect(await screen.findByTestId('projects-grid')).toBeDefined();
    expect(screen.getByTestId('project-open-prj_1').textContent).toBe('Pilot episode');
    expect(screen.getByTestId('project-status-prj_1')).toBeDefined();
    expect(screen.getByTestId('project-created-prj_1').textContent).not.toBe('');
    expect(screen.getByTestId('project-activity-prj_2')).toBeDefined();

    const calls = listCalls();
    expect(calls.length).toBeGreaterThan(0);
    const first = new URL(calls[0] as string);
    expect(first.searchParams.get('page')).toBe('1');
    expect(first.searchParams.get('pageSize')).toBe('20');
    expect(first.searchParams.get('sort')).toBe('createdAt');
    expect(first.searchParams.get('sortDir')).toBe('desc');
  });

  it('excludes archived by default; archived filter sends the archived param (R4)', async () => {
    authenticate();
    renderProjects();
    expect(await screen.findByTestId('projects-grid')).toBeDefined();
    expect(new URL(listCalls()[0] as string).searchParams.get('archived')).toBeNull();

    fireEvent.change(screen.getByTestId('filter-archived'), { target: { value: 'all' } });
    await waitFor(() => {
      const calls = listCalls();
      expect(calls.some((url) => new URL(url).searchParams.get('archived') === 'all')).toBe(true);
    });
  });

  it('sends the status filter to the server', async () => {
    authenticate();
    renderProjects();
    expect(await screen.findByTestId('projects-grid')).toBeDefined();
    fireEvent.change(screen.getByTestId('filter-status'), { target: { value: 'Failed' } });
    await waitFor(() => {
      expect(listCalls().some((url) => new URL(url).searchParams.get('status') === 'Failed')).toBe(true);
    });
  });
});

describe('URL sync (R1)', () => {
  it('hydrates controls from the URL and writes page changes back', async () => {
    authenticate();
    items = Array.from({ length: 45 }, (_, i) =>
      row({ id: `prj_${i + 1}`, name: `Project ${i + 1}`, status: 'Completed' }),
    );
    const router = renderProjects('/projects?status=Failed&page=2');
    expect(await screen.findByTestId('projects-grid')).toBeDefined();
    expect((screen.getByTestId('filter-status') as HTMLSelectElement).value).toBe('Failed');
    expect(screen.getByTestId('projects-pagination').textContent).toContain('Page 2 of 3');

    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    await waitFor(() => {
      expect(router.state.location.search).toContain('page=3');
    });
    await waitFor(() => {
      expect(listCalls().some((url) => new URL(url).searchParams.get('page') === '3')).toBe(true);
    });
  });
});

describe('valid-actions-only rendering (R3)', () => {
  it('omits disallowed actions from the DOM (never shown-disabled)', async () => {
    authenticate(['project.view', 'project.delete']);
    renderProjects();
    expect(await screen.findByTestId('projects-grid')).toBeDefined();
    expect(screen.queryByTestId('project-action-retry-prj_1')).toBeNull();
    expect(screen.queryByTestId('project-action-cancel-prj_2')).toBeNull();
    expect(screen.getByTestId('project-action-delete-prj_1')).toBeDefined();
    expect(screen.queryByTestId('project-action-delete-prj_2')).toBeNull();
  });

  it('shows cancel/retry/export for privileged rows', async () => {
    authenticate();
    renderProjects();
    expect(await screen.findByTestId('projects-grid')).toBeDefined();
    expect(screen.getByTestId('project-action-cancel-prj_2')).toBeDefined();
    expect(screen.getByTestId('project-action-export-prj_1')).toBeDefined();
    expect(screen.queryByTestId('project-action-retry-prj_1')).toBeNull();
  });
});

describe('empty states', () => {
  it('shows filtered-empty with a working clear action', async () => {
    authenticate();
    items = [];
    const router = renderProjects('/projects?status=Failed');
    expect(await screen.findByTestId('projects-empty-filtered')).toBeDefined();
    fireEvent.click(screen.getByTestId('projects-clear-filters'));
    await waitFor(() => {
      expect(router.state.location.search).toBe('');
    });
    expect(await screen.findByTestId('projects-empty')).toBeDefined();
  });

  it('shows tenant-empty without a dead creation CTA', async () => {
    authenticate();
    items = [];
    renderProjects();
    expect(await screen.findByTestId('projects-empty')).toBeDefined();
    expect(screen.queryByTestId('projects-clear-filters')).toBeNull();
  });
});

describe('delete flow', () => {
  it('requires the exact typed name, then removes the row with a toast', async () => {
    authenticate();
    renderProjects();
    expect(await screen.findByTestId('projects-grid')).toBeDefined();
    fireEvent.click(screen.getByTestId('project-action-delete-prj_1'));
    expect(await screen.findByTestId('projects-delete-dialog')).toBeDefined();
    const confirm = screen.getByTestId('delete-confirm-button') as HTMLButtonElement;
    expect(confirm.disabled).toBe(true);

    fireEvent.change(screen.getByTestId('delete-confirm-name'), { target: { value: 'Wrong name' } });
    expect((screen.getByTestId('delete-confirm-button') as HTMLButtonElement).disabled).toBe(true);

    fireEvent.change(screen.getByTestId('delete-confirm-name'), { target: { value: 'Pilot episode' } });
    expect((screen.getByTestId('delete-confirm-button') as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(screen.getByTestId('delete-confirm-button'));

    expect(await screen.findByText('Project deleted.')).toBeDefined();
    await waitFor(() => {
      expect(screen.queryByTestId('project-actions-prj_1')).toBeNull();
    });
    expect(screen.getByTestId('project-actions-prj_2')).toBeDefined();
  });
});

describe('server-authoritative failures', () => {
  it('shows a toast and refreshes the list on 403', async () => {
    authenticate();
    archiveStatus = 403;
    archiveBody = errorBody('FORBIDDEN');
    renderProjects();
    expect(await screen.findByTestId('projects-grid')).toBeDefined();
    const callsBefore = listCalls().length;
    fireEvent.click(screen.getByTestId('project-action-archive-prj_1'));
    expect(await screen.findByText(/backend FORBIDDEN/)).toBeDefined();
    await waitFor(() => {
      expect(listCalls().length).toBeGreaterThan(callsBefore);
    });
    expect(screen.getByTestId('projects-grid')).toBeDefined();
  });
});

describe('navigation', () => {
  it('opens the workspace from the name link', async () => {
    authenticate();
    renderProjects();
    expect(await screen.findByTestId('projects-grid')).toBeDefined();
    fireEvent.click(screen.getByTestId('project-open-prj_1'));
    expect(await screen.findByTestId('page-project-details')).toBeDefined();
  });
});
