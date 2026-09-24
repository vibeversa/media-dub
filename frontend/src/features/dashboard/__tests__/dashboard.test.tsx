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
import { queryKeys } from '../../../api/queryKeys/index.js';
import { LocaleProvider } from '../../../app/providers/LocaleProvider.js';
import { ROUTER_FUTURE_FLAGS, ROUTER_PROVIDER_FUTURE_FLAGS } from '../../../app/router.js';
import { queryClient } from '../../../app/providers/queryClient.js';
import { useAppStore } from '../../../stores/index.js';
import { useAuthStore } from '../../auth/authStore.js';
import { resetRestoreStartedForTests } from '../../auth/useSession.js';
import { DashboardPage } from '../DashboardPage.js';

const FULL_SUMMARY = {
  projectCounts: { active: 2, archived: 1, total: 3 },
  recentOutputs: [
    {
      id: 'out-1',
      projectId: 'prj_1',
      mediaKind: 'audio',
      container: 'mp3',
      createdAt: '2024-01-15T12:00:00Z',
    },
  ],
  storage: { usedBytes: 8_000_000_000, quotaBytes: 100_000_000_000 },
  cost: { monthToDate: 1234.5, currency: 'USD' },
  quota: { remaining: 10, resetsAt: '2026-09-25T00:00:00Z' },
  warnings: [{ code: 'PROJECT_FAILED', message: 'Project failed.', projectId: 'prj_1' }],
  backlog: { pendingReviews: 4, runningJobs: 2 },
};

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function errorBody(code: string): unknown {
  return { error: { code, message: `backend ${code}`, correlationId: 'corr-dash', details: {} } };
}

const mockFetch = vi.fn<typeof fetch>();

function mockSummary(body: unknown = FULL_SUMMARY, status = 200): void {
  mockFetch.mockImplementation((input) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.href : (input as Request).url;
    if (url.includes('/dashboard/summary')) {
      return Promise.resolve(jsonResponse(body, status));
    }
    return Promise.resolve(jsonResponse({}));
  });
}

function renderDashboard(initialEntry = '/dashboard') {
  const router = createMemoryRouter(
    [
      { path: '/dashboard', element: <DashboardPage /> },
      { path: '/projects', element: <div data-testid="page-projects" /> },
      { path: '/projects/new', element: <div data-testid="page-project-create" /> },
      { path: '/projects/:id', element: <div data-testid="page-project-details" /> },
      { path: '/review', element: <div data-testid="page-review" /> },
      { path: '/settings', element: <div data-testid="page-settings" /> },
    ],
    { initialEntries: [initialEntry], future: { ...ROUTER_FUTURE_FLAGS } },
  );
  render(
    <QueryClientProvider client={queryClient}>
      <LocaleProvider>
        <RouterProvider router={router} future={{ ...ROUTER_PROVIDER_FUTURE_FLAGS }} />
      </LocaleProvider>
    </QueryClientProvider>,
  );
  return router;
}

function authenticate(permissions: readonly string[] = ['project.view', 'admin.manage']): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', permissions);
}

beforeEach(() => {
  mockFetch.mockReset();
  setInnerFetchForTests(mockFetch);
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  queryClient.clear();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
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

describe('summary render (R1 single aggregate)', () => {
  it('renders every section from one summary call with drill-down links', async () => {
    authenticate();
    mockSummary();
    const router = renderDashboard();
    void router;
    expect(await screen.findByTestId('dashboard-stat-card')).toBeDefined();
    expect(screen.getByTestId('dashboard-stat-total').textContent).toBe('3');
    expect(screen.getByTestId('dashboard-recent-outputs')).toBeDefined();
    expect(screen.getByTestId('dashboard-output-out-1')).toBeDefined();
    expect(screen.getByTestId('dashboard-storage-cost')).toBeDefined();
    expect(screen.getByTestId('dashboard-cost-total').textContent).toContain('1,234');
    expect(screen.getByTestId('dashboard-quota')).toBeDefined();
    expect(screen.getByTestId('dashboard-warnings')).toBeDefined();
    expect(screen.getByTestId('dashboard-backlog')).toBeDefined();
    expect(screen.getByTestId('dashboard-backlog-reviews').textContent).toBe('4');

    expect(screen.getByTestId('dashboard-stat-link').getAttribute('href')).toBe('/projects');
    expect(screen.getByTestId('dashboard-output-link-out-1').getAttribute('href')).toBe('/projects/prj_1');
    expect(screen.getByTestId('dashboard-backlog-reviews-link').getAttribute('href')).toBe('/review');
    expect(screen.getByTestId('dashboard-backlog-projects-link').getAttribute('href')).toBe('/projects');
    expect(screen.getByTestId('dashboard-quota-manage').getAttribute('href')).toBe('/settings');
    expect(screen.getByTestId('dashboard-storage-manage').getAttribute('href')).toBe('/settings');
    expect(screen.getByTestId('dashboard-warning-PROJECT_FAILED')).toBeDefined();
    expect(screen.getByTestId('dashboard-warning-link-PROJECT_FAILED').getAttribute('href')).toBe(
      '/projects/prj_1',
    );

    for (const call of mockFetch.mock.calls) {
      expect(String(call[0])).toContain('/dashboard/summary');
    }
  });

  it('navigates drill-down links to the filtered list and workspace views', async () => {
    authenticate();
    mockSummary();
    renderDashboard();
    expect(await screen.findByTestId('dashboard-stat-card')).toBeDefined();
    fireEvent.click(screen.getByTestId('dashboard-stat-link'));
    expect(await screen.findByTestId('page-projects')).toBeDefined();
  });

  it('hides warnings when the section is empty (never an empty card)', async () => {
    authenticate();
    mockSummary({ ...FULL_SUMMARY, warnings: [] });
    renderDashboard();
    expect(await screen.findByTestId('dashboard-stat-card')).toBeDefined();
    expect(screen.queryByTestId('dashboard-warnings')).toBeNull();
  });
});

describe('empty tenant (R4)', () => {
  it('shows onboarding with a CTA to the creation entry point', async () => {
    authenticate(['project.view']);
    mockSummary({
      ...FULL_SUMMARY,
      projectCounts: { active: 0, archived: 0, total: 0 },
      recentOutputs: [],
      warnings: [],
    });
    renderDashboard();
    expect(await screen.findByTestId('dashboard-empty')).toBeDefined();
    expect(screen.queryByTestId('dashboard-grid')).toBeNull();
    const cta = screen.getByTestId('dashboard-empty-cta');
    expect(cta.getAttribute('href')).toBe('/projects/new');
    fireEvent.click(cta);
    expect(await screen.findByTestId('page-project-create')).toBeDefined();
  });
});

describe('partial isolation (R3)', () => {
  it('keeps other cards live when one section is missing, with per-card retry', async () => {
    authenticate();
    const partial = { ...FULL_SUMMARY, storage: undefined };
    mockSummary(partial);
    renderDashboard();
    expect(await screen.findByTestId('dashboard-stat-card')).toBeDefined();
    const storageCard = await screen.findByTestId('dashboard-storage-cost');
    expect(storageCard.getAttribute('role')).toBe('alert');
    expect(screen.getByTestId('dashboard-backlog')).toBeDefined();
    expect(screen.getByTestId('dashboard-quota')).toBeDefined();

    const callsBefore = mockFetch.mock.calls.length;
    mockSummary();
    fireEvent.click(screen.getByTestId('dashboard-storage-retry'));
    await waitFor(() => {
      expect(mockFetch.mock.calls.length).toBeGreaterThan(callsBefore);
    });
    expect(await screen.findByTestId('dashboard-storage-used')).toBeDefined();
  });
});

describe('global error', () => {
  it('shows ErrorState with retry and recovers on refetch', async () => {
    authenticate();
    let calls = 0;
    mockFetch.mockImplementation((input) => {
      const url = typeof input === 'string' ? input : (input as Request).url;
      if (url.includes('/dashboard/summary')) {
        calls += 1;
        if (calls === 1) {
          return Promise.resolve(jsonResponse(errorBody('INTERNAL_ERROR'), 500));
        }
        return Promise.resolve(jsonResponse(FULL_SUMMARY));
      }
      return Promise.resolve(jsonResponse({}));
    });
    renderDashboard();
    expect(await screen.findByTestId('dashboard-error')).toBeDefined();
    expect(screen.queryByTestId('dashboard-grid')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByTestId('dashboard-stat-card')).toBeDefined();
    expect(calls).toBeGreaterThan(1);
  });
});

describe('usage permission gating', () => {
  it('hides cost/quota without the usage permission (no error flash)', async () => {
    authenticate(['project.view']);
    mockSummary();
    renderDashboard();
    expect(await screen.findByTestId('dashboard-stat-card')).toBeDefined();
    expect(screen.queryByTestId('dashboard-storage-cost')).toBeNull();
    expect(screen.queryByTestId('dashboard-quota')).toBeNull();
    expect(screen.queryByTestId('dashboard-error')).toBeNull();
    expect(screen.queryByTestId('dashboard-storage-retry')).toBeNull();
  });
});

describe('quota warning threshold (R2)', () => {
  function renderBareDashboard() {
    return render(
      <QueryClientProvider client={queryClient}>
        <LocaleProvider>
          <MemoryRouter>
            <DashboardPage />
          </MemoryRouter>
        </LocaleProvider>
      </QueryClientProvider>,
    );
  }

  it('shows the warning meter at 80% and the blocked state when exhausted', async () => {
    authenticate();
    mockSummary({
      ...FULL_SUMMARY,
      storage: { usedBytes: 85, quotaBytes: 100 },
      quota: { remaining: 3, resetsAt: '2026-09-25T00:00:00Z' },
    });
    const { unmount } = renderBareDashboard();
    expect(await screen.findByTestId('dashboard-quota-warning')).toBeDefined();
    expect(screen.queryByTestId('dashboard-quota-blocked')).toBeNull();
    unmount();
    cleanup();
    queryClient.clear();

    authenticate();
    mockSummary({
      ...FULL_SUMMARY,
      storage: { usedBytes: 100, quotaBytes: 100 },
      quota: { remaining: 0, resetsAt: '2026-09-25T00:00:00Z' },
    });
    renderBareDashboard();
    expect(await screen.findByTestId('dashboard-quota-blocked')).toBeDefined();
  });

  it('stays quiet below 80% with quota remaining', async () => {
    authenticate();
    mockSummary({
      ...FULL_SUMMARY,
      storage: { usedBytes: 10, quotaBytes: 100 },
      quota: { remaining: 9, resetsAt: '2026-09-25T00:00:00Z' },
    });
    renderBareDashboard();
    expect(await screen.findByTestId('dashboard-quota')).toBeDefined();
    expect(screen.queryByTestId('dashboard-quota-warning')).toBeNull();
    expect(screen.queryByTestId('dashboard-quota-blocked')).toBeNull();
  });
});

describe('stale cache', () => {
  it('renders stale data with a background-refresh indicator', async () => {
    authenticate();
    queryClient.setQueryData(queryKeys.dashboard.summary(), FULL_SUMMARY);
    let release!: (response: Response) => void;
    const gate = new Promise<Response>((resolve) => {
      release = resolve;
    });
    mockFetch.mockImplementation((input) => {
      const url = typeof input === 'string' ? input : (input as Request).url;
      if (url.includes('/dashboard/summary')) {
        return gate;
      }
      return Promise.resolve(jsonResponse({}));
    });
    render(
      <QueryClientProvider client={queryClient}>
        <LocaleProvider>
          <MemoryRouter>
            <DashboardPage />
          </MemoryRouter>
        </LocaleProvider>
      </QueryClientProvider>,
    );
    expect(await screen.findByTestId('dashboard-stat-card')).toBeDefined();
    void queryClient.invalidateQueries({ queryKey: queryKeys.dashboard.summary() });
    expect(await screen.findByTestId('dashboard-stale-indicator')).toBeDefined();
    expect(screen.getByTestId('dashboard-stat-total').textContent).toBe('3');
    release(jsonResponse({ ...FULL_SUMMARY, backlog: { pendingReviews: 9, runningJobs: 1 } }));
    await waitFor(() => {
      expect(screen.queryByTestId('dashboard-stale-indicator')).toBeNull();
    });
    expect(screen.getByTestId('dashboard-backlog-reviews').textContent).toBe('9');
  });
});
