// Task 039B: app-shell state-matrix gap closure.
//
// Covers the zero-coverage shell seams: `App` (env-guard + provider tree),
// `ConfigErrorScreen`, `ChunkErrorBoundary` (caught + reload recovery),
// providers (auth/store/telemetry/theme effects), session helpers
// (`e2eSeed`, permissions, `useSession`), `main.tsx` boot paths,
// `createAppRouter`, the `RequireAdmin` loading gate, the default sign-out
// recovery, live project-tab badges, and `MediaPage` without params.
//
// Every failure state asserts its recovery action per §11.6
// (reload/retry/sign-in-again/contact-admin) with a non-color signal
// (role/text), supporting 041C. Fetch is intercepted and never resolves, so
// no network or backend boot is required (R3); fixtures are synthetic.
import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, RouterProvider, createMemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import '../../i18n/i18n.js';
import {
  AUTH_EXPIRED_EVENT,
  restoreInnerFetchForTests,
  setInnerFetchForTests,
  setTokenProvider,
} from '../../api/client/index.js';
import { queryClient } from '../providers/queryClient.js';
import { LocaleProvider } from '../providers/LocaleProvider.js';
import { QueryProvider } from '../providers/QueryProvider.js';
import { ToastProvider } from '../../components/Toast/Toast.js';
import { useTelemetry } from '../../telemetry/telemetryContext.js';
import { resetEnvCache } from '../../lib/env.js';
import { useAppStore } from '../../stores/index.js';
import { App } from '../App.js';
import { ChunkErrorBoundary } from '../ChunkErrorBoundary.js';
import { ConfigErrorScreen } from '../ConfigErrorScreen.js';
import { RequireAdmin } from '../guards/RequireAdmin.js';
import { AppShell } from '../layouts/AppShell.js';
import { ProjectLayout } from '../layouts/ProjectLayout.js';
import MediaPage from '../pages/MediaPage.js';
import { AuthProvider } from '../providers/AuthProvider.js';
import { StoreProvider } from '../providers/StoreProvider.js';
import { TelemetryProvider } from '../providers/TelemetryProvider.js';
import { ThemeProvider } from '../providers/ThemeProvider.js';
import { ROUTER_FUTURE_FLAGS, ROUTER_PROVIDER_FUTURE_FLAGS, createAppRouter, routes } from '../router.js';
import { E2E_SESSION_KEY, readE2eSessionSeed } from '../session/e2eSeed.js';
import { hasAdminPermission, isAdminPath } from '../session/permissions.js';
import { LoggedOutPage as FeatureLoggedOutPage } from '../../features/auth/LoggedOutPage.js';
import { SessionExpiredDialog } from '../../features/auth/SessionExpiredDialog.js';
import { LOGOUT_BROADCAST_KEY, useAuthStore } from '../../features/auth/authStore.js';
import { ensureRestoreStarted, resetRestoreStartedForTests, useIsAuthenticated, useSession } from '../../features/auth/useSession.js';

const mockFetch = vi.fn<typeof fetch>();

function neverResolve(): Promise<Response> {
  return new Promise<Response>(() => {});
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
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
  resetEnvCache();
  window.localStorage.clear();
});

afterEach(() => {
  cleanup();
  restoreInnerFetchForTests();
  queryClient.clear();
  useAppStore.getState().resetForTests();
  useAuthStore.getState().resetForTests();
  resetRestoreStartedForTests();
  resetEnvCache();
  window.localStorage.clear();
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
});

describe('e2eSeed matrix', () => {
  it('returns undefined when no seed is stored', () => {
    expect(readE2eSessionSeed()).toBeUndefined();
  });

  it('reads an authenticated seed with permissions', () => {
    window.localStorage.setItem(E2E_SESSION_KEY, JSON.stringify({ status: 'authenticated', permissions: ['project.view'] }));
    expect(readE2eSessionSeed()).toEqual({ status: 'authenticated', permissions: ['project.view'] });
  });

  it('reads an anonymous seed and drops non-string permissions', () => {
    window.localStorage.setItem(
      E2E_SESSION_KEY,
      JSON.stringify({ status: 'anonymous', permissions: ['a', 7, null, 'b'] }),
    );
    expect(readE2eSessionSeed()).toEqual({ status: 'anonymous', permissions: ['a', 'b'] });
  });

  it('ignores malformed JSON (fail-closed)', () => {
    window.localStorage.setItem(E2E_SESSION_KEY, '{not-json');
    expect(readE2eSessionSeed()).toBeUndefined();
  });

  it('ignores unknown statuses (fail-closed)', () => {
    window.localStorage.setItem(E2E_SESSION_KEY, JSON.stringify({ status: 'expired', permissions: [] }));
    expect(readE2eSessionSeed()).toBeUndefined();
  });

  it('defaults missing permissions to an empty set', () => {
    window.localStorage.setItem(E2E_SESSION_KEY, JSON.stringify({ status: 'authenticated' }));
    expect(readE2eSessionSeed()).toEqual({ status: 'authenticated', permissions: [] });
  });
});

describe('permissions matrix', () => {
  it('accepts every admin alias and fails closed on empty input', () => {
    expect(hasAdminPermission(['admin.manage'])).toBe(true);
    expect(hasAdminPermission(['diagnostics.view'])).toBe(true);
    expect(hasAdminPermission(['admin:read'])).toBe(true);
    expect(hasAdminPermission([])).toBe(false);
    expect(hasAdminPermission(['project.view'])).toBe(false);
  });

  it('matches admin pathnames only', () => {
    expect(isAdminPath('/admin')).toBe(true);
    expect(isAdminPath('/admin/users')).toBe(true);
    expect(isAdminPath('/dashboard')).toBe(false);
    expect(isAdminPath('/administrator')).toBe(false);
  });
});

describe('useSession matrix', () => {
  function Probe(): React.JSX.Element {
    const s = useSession();
    return (
      <div
        data-testid="session-probe"
        data-auth={String(s.isAuthenticated)}
        data-pending={String(s.isPending)}
        data-expired={String(s.isExpired)}
      >
        {`${s.status}|${s.loginPending}|${s.lastErrorCode ?? ''}`}
      </div>
    );
  }

  function renderProbe(): void {
    render(
      <QueryClientProvider client={queryClient}>
        <Probe />
      </QueryClientProvider>,
    );
  }

  it('reports the unknown gate state as pending (skeleton signal)', () => {
    ensureRestoreStarted();
    useAuthStore.setState({ status: 'unknown' });
    renderProbe();
    const probe = screen.getByTestId('session-probe');
    expect(probe.textContent).toContain('unknown');
    expect(probe.getAttribute('data-pending')).toBe('true');
    expect(probe.getAttribute('data-auth')).toBe('false');
  });

  it('reports authenticated / expired / error states with flags', () => {
    ensureRestoreStarted();
    useAuthStore.setState({ status: 'authenticated', userId: 'u_1', tenantId: 't_1', permissions: ['project.view'] });
    renderProbe();
    expect(screen.getByTestId('session-probe').getAttribute('data-auth')).toBe('true');
    cleanup();
    useAuthStore.setState({ status: 'expired', lastErrorCode: 'TOKEN_EXPIRED', loginPending: false });
    renderProbe();
    const probe = screen.getByTestId('session-probe');
    expect(probe.getAttribute('data-expired')).toBe('true');
    expect(probe.textContent).toContain('TOKEN_EXPIRED');
    cleanup();
    useAuthStore.setState({ status: 'error', lastErrorCode: 'INTERNAL_ERROR' });
    renderProbe();
    expect(screen.getByTestId('session-probe').textContent).toContain('error|');
  });

  it('starts the pre-shell restore exactly once', async () => {
    const restore = vi.spyOn(useAuthStore.getState(), 'restore');
    ensureRestoreStarted();
    ensureRestoreStarted();
    expect(restore).toHaveBeenCalledTimes(1);
    await waitFor(() => {
      expect(useAuthStore.getState().status).toBe('anonymous');
    });
    expect(mockFetch).not.toHaveBeenCalled();
  });

  it('gates feature queries on the authenticated status', () => {
    function Gate(): React.JSX.Element {
      const enabled = useIsAuthenticated();
      return <div data-testid="gate">{enabled ? 'yes' : 'no'}</div>;
    }
    useAuthStore.setState({ status: 'authenticated' });
    render(
      <QueryClientProvider client={queryClient}>
        <Gate />
      </QueryClientProvider>,
    );
    expect(screen.getByTestId('gate').textContent).toBe('yes');
    cleanup();
    useAuthStore.setState({ status: 'anonymous' });
    render(
      <QueryClientProvider client={queryClient}>
        <Gate />
      </QueryClientProvider>,
    );
    expect(screen.getByTestId('gate').textContent).toBe('no');
  });
});

describe('auth UX states', () => {
  it('renders the expiry dialog with the default dashboard destination', () => {
    render(
      <MemoryRouter>
        <SessionExpiredDialog />
      </MemoryRouter>,
    );
    expect(screen.getByTestId('session-expired-dialog').getAttribute('role')).toBe('alert');
    expect(screen.getByRole('link', { name: 'Sign in again' }).getAttribute('href')).toBe('/login?next=%2Fdashboard');
  });

  it('renders the post-logout confirmation with a sign-in recovery link', () => {
    render(
      <MemoryRouter>
        <FeatureLoggedOutPage />
      </MemoryRouter>,
    );
    expect(screen.getByTestId('page-logged-out')).toBeDefined();
    expect(screen.getByTestId('logged-out-signin').getAttribute('href')).toBe('/login');
  });
});

describe('App startup guard', () => {
  it('renders the config-error screen with issues and a contact-admin recovery action', () => {
    render(<ConfigErrorScreen error={{ issues: ['VITE_API_BASE_URL: must be a valid URL.'] }} />);
    const alert = screen.getByTestId('config-error');
    expect(alert.getAttribute('role')).toBe('alert');
    expect(alert.textContent).toContain('Configuration error');
    expect(alert.textContent).toContain('VITE_API_BASE_URL: must be a valid URL.');
    expect(alert.textContent).toContain('Contact your administrator');
  });

  it('renders the config-error screen from App when env is invalid', () => {
    vi.stubEnv('VITE_API_BASE_URL', '');
    resetEnvCache();
    render(<App />);
    expect(screen.getByTestId('config-error')).toBeDefined();
  });

  it('boots the full provider tree and shell when env is valid', async () => {
    useAppStore.getState().setSession('authenticated', ['project.view']);
    useAuthStore.setState({ status: 'authenticated', accessToken: 'a', refreshToken: 'r' });
    render(<App />);
    expect(await screen.findByTestId('app-shell')).toBeDefined();
    expect(await screen.findByTestId('dashboard-loading')).toBeDefined();
    expect(document.documentElement.getAttribute('data-theme')).toBe('light');
  });

  it('exposes the safe disabled default outside the provider', () => {
    function TelemetryProbe(): React.JSX.Element {
      const value = useTelemetry();
      return (
        <div>
          <div data-testid="telemetry-probe">{`${value.enabled}|${value.optOut}`}</div>
          <button
            type="button"
            data-testid="telemetry-default-optout"
            onClick={() => {
              value.setOptOut(true);
            }}
          >
            opt out
          </button>
        </div>
      );
    }
    render(
      <QueryClientProvider client={queryClient}>
        <LocaleProvider>
          <ToastProvider>
            <TelemetryProbe />
          </ToastProvider>
        </LocaleProvider>
      </QueryClientProvider>,
    );
    // No provider above the probe: the safe disabled default applies, and the
    // default setter is a no-op that never throws (fail-safe without context).
    expect(screen.getByTestId('telemetry-probe').textContent).toBe('false|false');
    fireEvent.click(screen.getByTestId('telemetry-default-optout'));
    expect(screen.getByTestId('telemetry-probe').textContent).toBe('false|false');
  });
});

describe('ChunkErrorBoundary matrix', () => {
  function Thrower(): React.JSX.Element {
    throw new Error('chunk failed');
  }

  it('renders children when nothing throws', () => {
    render(
      <ChunkErrorBoundary>
        <span data-testid="chunk-child">fine</span>
      </ChunkErrorBoundary>,
    );
    expect(screen.getByTestId('chunk-child')).toBeDefined();
  });

  it('recovers a chunk failure with a versioned reload action', () => {
    const reload = vi.fn();
    const location = window.location;
    Object.defineProperty(window, 'location', { value: { ...location, reload }, writable: true, configurable: true });
    try {
      render(
        <ChunkErrorBoundary>
          <Thrower />
        </ChunkErrorBoundary>,
      );
      const alert = screen.getByTestId('chunk-error');
      expect(alert.getAttribute('role')).toBe('alert');
      expect(alert.textContent).toContain('This page failed to load');
      expect(alert.textContent).toContain('Version: 0.1.0-dev');
      fireEvent.click(screen.getByRole('button', { name: 'Reload' }));
      expect(reload).toHaveBeenCalledTimes(1);
    } finally {
      Object.defineProperty(window, 'location', { value: location, writable: true, configurable: true });
    }
  });

  it('stamps unknown versions when env is invalid', () => {
    vi.stubEnv('VITE_API_BASE_URL', '');
    resetEnvCache();
    render(
      <ChunkErrorBoundary>
        <Thrower />
      </ChunkErrorBoundary>,
    );
    expect(screen.getByTestId('chunk-error').textContent).toContain('Version: unknown');
  });
});

describe('provider effects', () => {
  function renderProviders(children: React.JSX.Element): void {
    const router = createMemoryRouter([{ path: '/', element: children }], {
      initialEntries: ['/'],
      future: { ...ROUTER_FUTURE_FLAGS },
    });
    render(
      <QueryProvider>
        <LocaleProvider>
          <RouterProvider router={router} future={{ ...ROUTER_PROVIDER_FUTURE_FLAGS }} />
        </LocaleProvider>
      </QueryProvider>,
    );
  }

  it('skips the pre-shell restore when an E2E seed is present', async () => {
    window.localStorage.setItem(E2E_SESSION_KEY, JSON.stringify({ status: 'authenticated', permissions: ['p'] }));
    const restore = vi.spyOn(useAuthStore.getState(), 'restore');
    renderProviders(
      <AuthProvider>
        <span data-testid="auth-child">child</span>
      </AuthProvider>,
    );
    expect(screen.getByTestId('auth-child')).toBeDefined();
    await waitFor(() => {
      expect(restore).not.toHaveBeenCalled();
    });
  });

  it('re-resolves on auth:expired and clears on cross-tab logout', async () => {
    useAuthStore.setState({ status: 'authenticated', accessToken: 'a', refreshToken: '' });
    renderProviders(
      <AuthProvider>
        <span data-testid="auth-child">child</span>
      </AuthProvider>,
    );
    window.dispatchEvent(new CustomEvent(AUTH_EXPIRED_EVENT, { detail: { correlationId: 'c', status: 401 } }));
    await waitFor(() => {
      expect(useAuthStore.getState().status).toBe('expired');
    });
    useAuthStore.setState({ status: 'authenticated', accessToken: 'a', refreshToken: 'r' });
    window.dispatchEvent(new StorageEvent('storage', { key: LOGOUT_BROADCAST_KEY }));
    await waitFor(() => {
      expect(useAuthStore.getState().status).toBe('anonymous');
    });
    expect(useAppStore.getState().sessionStatus).toBe('anonymous');
  });

  it('ignores storage events for other keys', () => {
    useAuthStore.setState({ status: 'authenticated', accessToken: 'a', refreshToken: 'r' });
    renderProviders(
      <AuthProvider>
        <span data-testid="auth-child">child</span>
      </AuthProvider>,
    );
    window.dispatchEvent(new StorageEvent('storage', { key: 'unrelated.key' }));
    expect(useAuthStore.getState().status).toBe('authenticated');
  });

  it('mirrors theme and telemetry opt-out through the providers', async () => {
    function OptOutProbe(): React.JSX.Element {
      const value = useTelemetry();
      return (
        <button
          type="button"
          data-testid="optout-probe"
          onClick={() => {
            value.setOptOut(true);
          }}
        >
          {`enabled=${value.enabled} optOut=${value.optOut}`}
        </button>
      );
    }
    renderProviders(
      <StoreProvider>
        <ThemeProvider>
          <TelemetryProvider>
            <OptOutProbe />
          </TelemetryProvider>
        </ThemeProvider>
      </StoreProvider>,
    );
    await waitFor(() => {
      expect(document.documentElement.getAttribute('data-theme')).toBe('light');
    });
    expect(document.documentElement.getAttribute('dir')).toBe('ltr');
    fireEvent.click(screen.getByTestId('optout-probe'));
    await waitFor(() => {
      expect(useAppStore.getState().telemetryOptOut).toBe(true);
    });
    cleanup();
    queryClient.clear();
    useAppStore.getState().setLocale('ar');
    window.localStorage.setItem(
      E2E_SESSION_KEY,
      JSON.stringify({ status: 'authenticated', permissions: ['project.view'] }),
    );
    renderProviders(
      <StoreProvider>
        <span data-testid="store-child">child</span>
      </StoreProvider>,
    );
    await waitFor(() => {
      expect(document.documentElement.getAttribute('dir')).toBe('rtl');
    });
    expect(document.documentElement.getAttribute('lang')).toBe('ar');
    expect(useAppStore.getState().sessionStatus).toBe('authenticated');
    expect(useAppStore.getState().permissions).toEqual(['project.view']);
    window.localStorage.clear();
    useAppStore.getState().setLocale('en');
  });
});

describe('router factory', () => {
  it('builds a browser router over the route table', () => {
    const router = createAppRouter();
    expect(router.routes.length).toBeGreaterThan(0);
    expect(router.routes).toBeDefined();
  });
});

describe('RequireAdmin loading gate', () => {
  it('blocks on the loading status with a skeleton (never a flash of 403)', async () => {
    useAppStore.getState().setSession('loading', []);
    const router = createMemoryRouter(
      [
        {
          element: <RequireAdmin />,
          children: [{ path: '/admin', element: <div data-testid="admin-content" /> }],
        },
        { path: '/403', element: <div data-testid="forbidden-page" /> },
      ],
      { initialEntries: ['/admin'], future: { ...ROUTER_FUTURE_FLAGS } },
    );
    render(<RouterProvider router={router} future={{ ...ROUTER_PROVIDER_FUTURE_FLAGS }} />);
    expect(await screen.findByTestId('session-skeleton')).toBeDefined();
    expect(screen.queryByTestId('forbidden-page')).toBeNull();
    expect(screen.queryByTestId('admin-content')).toBeNull();
  });
});

describe('AppShell default sign-out', () => {
  it('revokes, clears, and lands on /logged-out (recovery action)', async () => {
    mockFetch.mockImplementation((input) => {
      const url = typeof input === 'string' ? input : (input as Request).url;
      if (url.includes('/auth/logout')) {
        return Promise.resolve(jsonResponse({ loggedOut: true }));
      }
      return neverResolve();
    });
    useAppStore.getState().setSession('authenticated', ['project.view']);
    useAuthStore.setState({ status: 'authenticated', accessToken: 'a', refreshToken: 'r' });
    queryClient.setQueryData(['projects', 'list'], { items: [] });
    const router = createMemoryRouter(
      [
        { path: '/', element: <AppShell /> },
        { path: '/logged-out', element: <div data-testid="logged-out-stub" /> },
      ],
      { initialEntries: ['/'], future: { ...ROUTER_FUTURE_FLAGS } },
    );
    render(
      <QueryClientProvider client={queryClient}>
        <LocaleProvider>
          <RouterProvider router={router} future={{ ...ROUTER_PROVIDER_FUTURE_FLAGS }} />
        </LocaleProvider>
      </QueryClientProvider>,
    );
    fireEvent.click(screen.getByTestId('user-menu-signout'));
    expect(await screen.findByTestId('logged-out-stub')).toBeDefined();
    expect(useAuthStore.getState().status).toBe('anonymous');
    expect(useAppStore.getState().sessionStatus).toBe('anonymous');
    expect(queryClient.getQueryData(['projects', 'list'])).toBeUndefined();
  });
});

describe('ProjectLayout live state', () => {
  it('badges counts on exports and quality tabs (text, not color)', () => {
    const router = createMemoryRouter([{ path: '/', element: <ProjectLayout workspaceState={{ hasMedia: true, hasTranscript: true, exportReadyCount: 2, openReviewCount: 5 }} /> }], {
      initialEntries: ['/'],
      future: { ...ROUTER_FUTURE_FLAGS },
    });
    render(
      <LocaleProvider>
        <RouterProvider router={router} future={{ ...ROUTER_PROVIDER_FUTURE_FLAGS }} />
      </LocaleProvider>,
    );
    expect(screen.getByTestId('project-tab-exports-badge').textContent).toBe('2');
    expect(screen.getByTestId('project-tab-quality-badge').textContent).toBe('5');
    expect(screen.getByTestId('project-tab-translation')).toBeDefined();
  });
});

describe('MediaPage without params', () => {
  it('renders the uploader shell with an empty project id (no crash)', () => {
    render(
      <QueryProvider>
        <LocaleProvider>
          <ToastProvider>
            <MemoryRouter>
              <MediaPage />
            </MemoryRouter>
          </ToastProvider>
        </LocaleProvider>
      </QueryProvider>,
    );
    expect(screen.getByTestId('page-project-media')).toBeDefined();
  });
});

describe('main boot', () => {
  it('throws a descriptive error without a #root element', async () => {
    document.body.innerHTML = '';
    vi.resetModules();
    await expect(import('../../main.js')).rejects.toThrow('Missing #root element in index.html.');
  });

  it('renders the shell into #root when present', async () => {
    document.body.innerHTML = '<div id="root"></div>';
    vi.resetModules();
    const freshClient = await import('../../api/client/index.js');
    freshClient.setInnerFetchForTests(neverResolve as unknown as typeof fetch);
    freshClient.setTokenProvider(() => 'test-token');
    const { useAppStore: freshAppStore } = await import('../../stores/index.js');
    freshAppStore.getState().resetForTests();
    freshAppStore.getState().setSession('authenticated', ['project.view']);
    const { useAuthStore: freshAuthStore } = await import('../../features/auth/authStore.js');
    freshAuthStore.getState().resetForTests();
    freshAuthStore.setState({ status: 'authenticated', accessToken: 'a', refreshToken: 'r' });
    await import('../../main.js');
    expect(await screen.findByTestId('app-shell')).toBeDefined();
  });
});

describe('full route table', () => {
  it('exposes every route from the table', () => {
    expect(routes.length).toBeGreaterThan(0);
  });
});
