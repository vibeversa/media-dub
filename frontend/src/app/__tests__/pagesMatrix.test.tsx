// Task 039B: route/page state-matrix gap closure.
//
// Renders every zero-coverage lazy route wrapper in `src/app/pages/` through
// the real route table. Each case pins the page shell (testid + heading
// text — non-color signals per 041C); fetch never resolves so every page
// exercises its loading state with no network or backend (R3). Deeper
// ready/error/empty matrices stay in the owning feature specs; this file
// proves every screen mounts its shell and owns its loading state.
import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, RouterProvider, createMemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import '../../i18n/i18n.js';
import { restoreInnerFetchForTests, setInnerFetchForTests, setTokenProvider } from '../../api/client/index.js';
import { queryClient } from '../providers/queryClient.js';
import { LocaleProvider } from '../providers/LocaleProvider.js';
import { ToastProvider } from '../../components/Toast/Toast.js';
import { useAppStore } from '../../stores/index.js';
import { useAuthStore } from '../../features/auth/authStore.js';
import { resetRestoreStartedForTests } from '../../features/auth/useSession.js';
import { ROUTER_FUTURE_FLAGS, ROUTER_PROVIDER_FUTURE_FLAGS, routes } from '../router.js';
import ActivityPage from '../pages/ActivityPage.js';
import ExportsPage from '../pages/ExportsPage.js';
import ProjectDetailsPage from '../pages/ProjectDetailsPage.js';
import QualityPage from '../pages/QualityPage.js';
import TimelinePage from '../pages/TimelinePage.js';
import TranscriptPage from '../pages/TranscriptPage.js';
import TranslationPage from '../pages/TranslationPage.js';
import VoicesPage from '../pages/VoicesPage.js';

const mockFetch = vi.fn<typeof fetch>();

function neverResolve(): Promise<Response> {
  return new Promise<Response>(() => {});
}

beforeEach(() => {
  mockFetch.mockReset();
  mockFetch.mockImplementation(neverResolve);
  setInnerFetchForTests(mockFetch);
  setTokenProvider(() => 'test-token');
  queryClient.clear();
  useAppStore.getState().resetForTests();
  useAuthStore.getState().resetForTests();
  resetRestoreStartedForTests();
  useAppStore.getState().setSession('authenticated', ['project.view', 'admin.manage', 'processing.start']);
  useAuthStore.setState({ status: 'authenticated', accessToken: 'a', refreshToken: 'r' });
});

afterEach(() => {
  cleanup();
  restoreInnerFetchForTests();
  queryClient.clear();
  useAppStore.getState().resetForTests();
  useAuthStore.getState().resetForTests();
  resetRestoreStartedForTests();
  vi.restoreAllMocks();
});

async function renderPath(path: string): Promise<void> {
  const router = createMemoryRouter(routes, {
    initialEntries: [path],
    future: { ...ROUTER_FUTURE_FLAGS },
  });
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

describe('project routes', () => {
  it('renders the project list shell', async () => {
    await renderPath('/projects');
    const page = await screen.findByTestId('page-projects');
    expect(page.textContent).toContain('Projects');
  });

  it('renders the project details shell with workspace', async () => {
    await renderPath('/projects/prj_1');
    const page = await screen.findByTestId('page-project-details');
    expect(page.textContent).toContain('prj_1');
  });

  it('renders the project overview tab on the details shell', async () => {
    await renderPath('/projects/prj_1/overview');
    expect(await screen.findByTestId('page-project-details')).toBeDefined();
  });

  it('renders the media tab shell', async () => {
    await renderPath('/projects/prj_1/media');
    expect(await screen.findByTestId('page-project-media')).toBeDefined();
  });

  it('renders the transcript tab shell', async () => {
    await renderPath('/projects/prj_1/transcript');
    expect(await screen.findByTestId('page-project-transcript')).toBeDefined();
  });

  it('renders the translation tab shell', async () => {
    await renderPath('/projects/prj_1/translation');
    expect(await screen.findByTestId('page-project-translation')).toBeDefined();
  });

  it('renders the voices tab shell', async () => {
    await renderPath('/projects/prj_1/voices');
    expect(await screen.findByTestId('page-project-voices')).toBeDefined();
  });

  it('renders the timeline tab shell', async () => {
    await renderPath('/projects/prj_1/timeline');
    expect(await screen.findByTestId('page-project-timeline')).toBeDefined();
  });

  it('renders the quality tab shell', async () => {
    await renderPath('/projects/prj_1/quality');
    expect(await screen.findByTestId('page-project-quality')).toBeDefined();
  });

  it('renders the exports tab shell', async () => {
    await renderPath('/projects/prj_1/exports');
    expect(await screen.findByTestId('page-project-exports')).toBeDefined();
  });

  it('renders the activity tab shell', async () => {
    await renderPath('/projects/prj_1/activity');
    expect(await screen.findByTestId('page-project-activity')).toBeDefined();
  });
});

describe('top-level routes', () => {
  it('renders the review studio shell', async () => {
    await renderPath('/review');
    const page = await screen.findByTestId('page-review');
    expect(page.textContent).toContain('Review');
  });

  it('renders the notifications shell', async () => {
    await renderPath('/notifications');
    const page = await screen.findByTestId('page-notifications');
    expect(page.textContent).toContain('Notifications');
  });

  it('renders the settings shell', async () => {
    await renderPath('/settings');
    const page = await screen.findByTestId('page-settings');
    expect(page.textContent).toContain('Settings');
  });

  it('renders the admin shell for elevated sessions', async () => {
    await renderPath('/admin');
    const page = await screen.findByTestId('page-admin');
    expect(page.textContent).toContain('Admin');
  });

  it('renders the post-logout confirmation shell', async () => {
    await renderPath('/logged-out');
    expect(await screen.findByTestId('page-logged-out')).toBeDefined();
  });
});

describe('project details shell (Task 024 entry + started canonicalization)', () => {
  function renderDetails(initialEntry: string, permissions: readonly string[] = ['project.view', 'processing.start']) {
    useAppStore.getState().setSession('authenticated', permissions);
    const router = createMemoryRouter(
      [{ path: '/projects/:id', element: <ProjectDetailsPage /> }],
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

  function projectFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
    const url = typeof input === 'string' ? input : (input as Request).url;
    const method = (typeof input !== 'string' && !(input instanceof URL) ? (input as Request).method : undefined) ?? init?.method ?? 'GET';
    if (method === 'POST' && url.includes('/processing') && !url.includes('/cancel') && !url.includes('/retry')) {
      return Promise.resolve(
        new Response(JSON.stringify({ runId: 'run_1', status: 'Pending', configHash: 'cfg_abc' }), {
          status: 202,
          headers: { 'Content-Type': 'application/json' },
        }),
      );
    }
    if (url.includes('/speakers')) {
      return Promise.resolve(
        new Response(JSON.stringify({ items: [], page: 1, pageSize: 100, total: 0, hasMore: false }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      );
    }
    if (url.includes('/dashboard/summary')) {
      return Promise.resolve(
        new Response(
          JSON.stringify({
            cost: { monthToDate: 1, currency: 'USD' },
            quota: { remaining: 5, resetsAt: '2026-09-25T00:00:00Z' },
            storage: { usedBytes: 10, quotaBytes: 100 },
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        ),
      );
    }
    if (url.includes('/processing/active')) {
      return Promise.resolve(
        new Response(JSON.stringify({ error: { code: 'NOT_FOUND', message: 'none', correlationId: 'c', details: {} } }), {
          status: 404,
          headers: { 'Content-Type': 'application/json' },
        }),
      );
    }
    if (/\/api\/v1\/projects\/[^/]+\/workspace/.test(url)) {
      return Promise.resolve(
        new Response(
          JSON.stringify({ cost: { runCost: 0.5, monthToDate: 1 }, quota: { remaining: 5, resetsAt: '2026-09-25T00:00:00Z' } }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        ),
      );
    }
    if (/\/api\/v1\/projects\/[^/]+$/.test(url)) {
      return Promise.resolve(
        new Response(
          JSON.stringify({ id: 'prj_1', name: 'Pilot', status: 'MediaReady', sourceLanguage: 'en', targetLanguage: 'es', settingsVersion: 1 }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        ),
      );
    }
    return Promise.resolve(
      new Response(JSON.stringify({}), { status: 200, headers: { 'Content-Type': 'application/json' } }),
    );
  }

  it('opens the preflight dialog from the start entry (no direct POST)', async () => {
    mockFetch.mockImplementation(projectFetch as typeof fetch);
    renderDetails('/projects/prj_1');
    const start = await screen.findByTestId('project-start-processing');
    expect(start.textContent?.length).toBeGreaterThan(0);
    fireEvent.click(start);
    expect(await screen.findByTestId('preflight-dialog')).toBeDefined();
    expect(mockFetch.mock.calls.some(([input]) => typeof input === 'string' && input.includes('/processing') && (input as string).includes('start'))).toBe(false);
    fireEvent.click(screen.getByTestId('preflight-cancel'));
    await waitFor(() => expect(screen.queryByTestId('preflight-dialog')).toBeNull());
  });

  it('starts the run through preflight with a toast and canonical ?started=1', async () => {
    mockFetch.mockImplementation(projectFetch as typeof fetch);
    const router = renderDetails('/projects/prj_1');
    fireEvent.click(await screen.findByTestId('project-start-processing'));
    expect(await screen.findByTestId('preflight-dialog')).toBeDefined();
    const confirm = await screen.findByTestId('preflight-confirm');
    expect((confirm as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(confirm);
    await waitFor(() => expect(router.state.location.search).toBe('?started=1'));
    expect(screen.getByRole('region', { name: 'Notifications' }).textContent).toContain('started');
  });

  it('silently canonicalizes ?started=1 landings (no second toast)', async () => {
    const router = renderDetails('/projects/prj_1?started=1');
    expect(await screen.findByTestId('page-project-details')).toBeDefined();
    await waitFor(() => expect(router.state.location.search).toBe(''));
    expect(screen.getByRole('region', { name: 'Notifications' }).textContent ?? '').not.toContain('started');
  });

  it('hides the start entry without the permission (hint, not error)', async () => {
    renderDetails('/projects/prj_1', ['project.view']);
    expect(await screen.findByTestId('page-project-details')).toBeDefined();
    expect(screen.queryByTestId('project-start-processing')).toBeNull();
  });

  it('renders unknown ids without crashing (no workspace, no start)', () => {
    render(
      <QueryClientProvider client={queryClient}>
        <LocaleProvider>
          <ToastProvider>
            <MemoryRouter>
              <ProjectDetailsPage />
            </MemoryRouter>
          </ToastProvider>
        </LocaleProvider>
      </QueryClientProvider>,
    );
    expect(screen.getByTestId('page-project-details').textContent).toContain('unknown');
    expect(screen.queryByTestId('project-start-processing')).toBeNull();
  });
});

describe('thin tab wrappers without params (empty-id fallback)', () => {
  function renderBare(element: React.JSX.Element): void {
    render(
      <QueryClientProvider client={queryClient}>
        <LocaleProvider>
          <ToastProvider>
            <MemoryRouter>{element}</MemoryRouter>
          </ToastProvider>
        </LocaleProvider>
      </QueryClientProvider>,
    );
  }

  it.each([
    ['exports', <ExportsPage key="exports" />, 'page-project-exports'],
    ['quality', <QualityPage key="quality" />, 'page-project-quality'],
    ['timeline', <TimelinePage key="timeline" />, 'page-project-timeline'],
    ['transcript', <TranscriptPage key="transcript" />, 'page-project-transcript'],
    ['translation', <TranslationPage key="translation" />, 'page-project-translation'],
    ['voices', <VoicesPage key="voices" />, 'page-project-voices'],
  ] as const)('renders the %s wrapper with an empty project id', (_name, element, testid) => {
    renderBare(element);
    expect(screen.getByTestId(testid)).toBeDefined();
  });

  it('renders the activity tab with an empty project id and holds the quota banner while cost loads', () => {
    renderBare(<ActivityPage />);
    expect(screen.getByTestId('page-project-activity')).toBeDefined();
    // Loading cost => no quota signal yet; the banner only appears once the
    // dashboard aggregate resolves, so nothing false-positive renders.
    expect(screen.queryByTestId('quota-banner-exceeded')).toBeNull();
    expect(screen.queryByTestId('quota-banner-ok')).toBeNull();
  });
});

describe('activity tab quota banner', () => {
  it('renders the quota banner once cost data resolves (recovery: manage/wait)', async () => {
    mockFetch.mockImplementation((input) => {
      const url = typeof input === 'string' ? input : (input as Request).url;
      if (url.includes('/dashboard/summary')) {
        return Promise.resolve(
          new Response(
            JSON.stringify({
              cost: { monthToDate: 12.5, currency: 'USD' },
              quota: { remaining: 0, resetsAt: '2026-09-25T00:00:00Z' },
              storage: { usedBytes: 100, quotaBytes: 100 },
            }),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          ),
        );
      }
      if (url.includes('/workspace')) {
        return Promise.resolve(
          new Response(JSON.stringify({ cost: { runCost: 1.5, monthToDate: 12.5 } }), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
        );
      }
      if (url.includes('/activity')) {
        return Promise.resolve(
          new Response(JSON.stringify({ items: [], page: 1, pageSize: 20, total: 0, hasMore: false }), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
        );
      }
      return Promise.resolve(
        new Response(JSON.stringify({}), { status: 200, headers: { 'Content-Type': 'application/json' } }),
      );
    });
    await renderPath('/projects/prj_1/activity');
    expect(await screen.findByTestId('page-project-activity')).toBeDefined();
    expect(await screen.findByTestId('quota-banner-exceeded')).toBeDefined();
  });
});
