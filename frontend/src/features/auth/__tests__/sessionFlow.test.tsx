import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { RouterProvider, createMemoryRouter } from 'react-router-dom';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import '../../../i18n/i18n.js';
import {
  clearTokenProvider,
  restoreInnerFetchForTests,
  setInnerFetchForTests,
} from '../../../api/client/index.js';
import { normalizeError } from '../../../api/errors/index.js';
import { LocaleProvider } from '../../../app/providers/LocaleProvider.js';
import { queryClient } from '../../../app/providers/queryClient.js';
import { ROUTER_FUTURE_FLAGS, ROUTER_PROVIDER_FUTURE_FLAGS } from '../../../app/router.js';
import { useAppStore } from '../../../stores/index.js';
import { AuthForbiddenPage } from '../ForbiddenPage.js';
import { LoginPage } from '../LoginPage.js';
import { RequireAuth } from '../RequireAuth.js';
import { SessionExpiredDialog } from '../SessionExpiredDialog.js';
import { useAuthStore } from '../authStore.js';
import { resetRestoreStartedForTests } from '../useSession.js';

const LOGIN_BODY = {
  accessToken: 'access-1',
  refreshToken: 'refresh-1',
  tokenType: 'Bearer',
  expiresInSeconds: 900,
  userId: 'usr_1',
  tenantId: 'tenant_1',
};

const ME_BODY = {
  permissions: ['project.view'],
  roles: [],
  userId: 'usr_1',
  tenantId: 'tenant_1',
};

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function errorBody(code: string): unknown {
  return { error: { code, message: `backend ${code}`, correlationId: 'corr-9', details: {} } };
}

const mockFetch = vi.fn<typeof fetch>();

function mockLoginSuccess(): void {
  mockFetch.mockImplementation((input) => {
    const url = typeof input === 'string' ? input : (input as Request).url;
    if (url.endsWith('/auth/login')) {
      return Promise.resolve(jsonResponse(LOGIN_BODY));
    }
    if (url.endsWith('/me')) {
      return Promise.resolve(jsonResponse(ME_BODY));
    }
    return Promise.resolve(jsonResponse({}));
  });
}

function renderWithRouter(element: ReactNode, initialEntry: string, extraRoutes?: { path: string; element: ReactNode }[]) {
  const router = createMemoryRouter(
    [
      { path: '/login', element },
      ...(extraRoutes ?? []).map((r) => ({ path: r.path, element: r.element })),
    ],
    { initialEntries: [initialEntry], future: { ...ROUTER_FUTURE_FLAGS } },
  );
  render(
    <LocaleProvider>
      <RouterProvider router={router} future={{ ...ROUTER_PROVIDER_FUTURE_FLAGS }} />
    </LocaleProvider>,
  );
  return router;
}

beforeEach(() => {
  mockFetch.mockReset();
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

describe('login page', () => {
  const destination = <div data-testid="dest-page" />;

  function fillForm(): void {
    fireEvent.change(screen.getByTestId('auth-tenant'), { target: { value: 'acme' } });
    fireEvent.change(screen.getByTestId('auth-email'), { target: { value: 'owner@example.com' } });
    fireEvent.change(screen.getByTestId('auth-password'), { target: { value: 'secret' } });
  }

  it('logs in and restores the destination (R2)', async () => {
    mockLoginSuccess();
    renderWithRouter(<LoginPage />, '/login?next=%2Fprojects%2Fprj_1', [
      { path: '/projects/:id', element: destination },
    ]);
    fillForm();
    fireEvent.click(screen.getByTestId('auth-submit'));
    expect(await screen.findByTestId('dest-page')).toBeDefined();
    expect(useAuthStore.getState().status).toBe('authenticated');
    expect(useAppStore.getState().sessionStatus).toBe('authenticated');
  });

  it('sends a single request on double-click (button disabled while pending)', async () => {
    let release!: (response: Response) => void;
    const gate = new Promise<Response>((resolve) => {
      release = resolve;
    });
    let loginCalls = 0;
    mockFetch.mockImplementation((input) => {
      const url = typeof input === 'string' ? input : (input as Request).url;
      if (url.endsWith('/auth/login')) {
        loginCalls += 1;
        return gate;
      }
      if (url.endsWith('/me')) {
        return Promise.resolve(jsonResponse(ME_BODY));
      }
      return Promise.resolve(jsonResponse({}));
    });
    renderWithRouter(<LoginPage />, '/login', [{ path: '/dashboard', element: destination }]);
    fillForm();
    const submit = screen.getByTestId('auth-submit');
    fireEvent.click(submit);
    fireEvent.click(submit);
    await waitFor(() => {
      expect(submit.hasAttribute('disabled')).toBe(true);
    });
    release(jsonResponse(LOGIN_BODY));
    expect(await screen.findByTestId('dest-page')).toBeDefined();
    expect(loginCalls).toBe(1);
  });

  it('shows one generic error on 401 (no user enumeration)', async () => {
    mockFetch.mockImplementation((input) => {
      const url = typeof input === 'string' ? input : (input as Request).url;
      if (url.endsWith('/auth/login')) {
        return Promise.resolve(jsonResponse(errorBody('INVALID_CREDENTIALS'), 401));
      }
      return Promise.resolve(jsonResponse({}));
    });
    renderWithRouter(<LoginPage />, '/login', [{ path: '/dashboard', element: destination }]);
    fillForm();
    fireEvent.click(screen.getByTestId('auth-submit'));
    const alert = await screen.findByTestId('auth-error');
    expect(alert.textContent).toContain('Email, password, or tenant is incorrect. Check them and try again.');
    expect(screen.queryByTestId('dest-page')).toBeNull();
    expect(useAuthStore.getState().status).not.toBe('authenticated');
  });

  it('shows the expiry dialog when the session expired', () => {
    useAuthStore.setState({ status: 'expired' });
    renderWithRouter(<LoginPage />, '/login?next=%2Fdashboard', [
      { path: '/dashboard', element: destination },
    ]);
    expect(screen.getByTestId('session-expired-dialog')).toBeDefined();
    expect(screen.getByText('Session expired')).toBeDefined();
  });

  it('renders the standalone expiry dialog with a preserved destination', () => {
    renderWithRouter(<SessionExpiredDialog destination="/projects/prj_1" />, '/login');
    const dialog = screen.getByTestId('session-expired-dialog');
    expect(dialog.getAttribute('role')).toBe('alert');
    const link = screen.getByRole('link', { name: 'Sign in again' });
    expect(link.getAttribute('href')).toBe('/login?next=%2Fprojects%2Fprj_1');
  });
});

describe('auth guard wiring (R5 + R2)', () => {
  const content = <div data-testid="guarded-content" />;
  const loginStub = <div data-testid="login-stub" />;

  function renderGuarded(initialEntry: string) {
    const router = createMemoryRouter(
      [
        {
          element: <RequireAuth />,
          children: [{ path: '/projects/:id', element: content }],
        },
        { path: '/login', element: loginStub },
      ],
      { initialEntries: [initialEntry], future: { ...ROUTER_FUTURE_FLAGS } },
    );
    render(
      <LocaleProvider>
        <RouterProvider router={router} future={{ ...ROUTER_PROVIDER_FUTURE_FLAGS }} />
      </LocaleProvider>,
    );
    return router;
  }

  it('blocks the shell while unknown (skeleton, R5)', async () => {
    useAppStore.getState().setSession('unknown', []);
    renderGuarded('/projects/prj_1');
    expect(await screen.findByTestId('session-skeleton')).toBeDefined();
    expect(screen.queryByTestId('guarded-content')).toBeNull();
  });

  it('redirects anonymous users to login with ?next= preserved', async () => {
    useAppStore.getState().setSession('anonymous', []);
    const router = renderGuarded('/projects/prj_1');
    expect(await screen.findByTestId('login-stub')).toBeDefined();
    expect(screen.queryByTestId('guarded-content')).toBeNull();
    expect(router.state.location.pathname).toBe('/login');
    expect(router.state.location.search).toBe('?next=%2Fprojects%2Fprj_1');
  });

  it('redirects expired deep links to login with the destination intact', async () => {
    useAppStore.getState().setSession('expired', []);
    const router = renderGuarded('/projects/prj_1?tab=media');
    expect(await screen.findByTestId('login-stub')).toBeDefined();
    expect(router.state.location.search).toBe('?next=%2Fprojects%2Fprj_1%3Ftab%3Dmedia');
  });

  it('renders the outlet once authenticated', async () => {
    useAppStore.getState().setSession('authenticated', ['project.view']);
    renderGuarded('/projects/prj_1');
    expect(await screen.findByTestId('guarded-content')).toBeDefined();
  });
});

describe('401/403 mapping', () => {
  it('normalizes 401 to Auth and 403 to Auth with a request-access hint page', () => {
    const unauthorized = normalizeError(
      { status: 401, code: 'TOKEN_EXPIRED', message: 'expired', correlationId: 'c1', details: {} },
      { method: 'GET' },
    );
    expect(unauthorized.kind).toBe('Auth');
    expect(unauthorized.status).toBe(401);
    const forbidden = normalizeError(
      { status: 403, code: 'FORBIDDEN', message: 'denied', correlationId: 'c2', details: {} },
      { method: 'GET' },
    );
    expect(forbidden.kind).toBe('Auth');
    expect(forbidden.status).toBe(403);
  });

  it('renders the forbidden hint without firing requests (never retry-loops)', () => {
    renderWithRouter(<AuthForbiddenPage />, '/login');
    expect(screen.getByTestId('page-forbidden')).toBeDefined();
    expect(
      screen.getByText('If you need access, ask your tenant admin to grant it — this page reveals nothing further about the resource.'),
    ).toBeDefined();
    expect(mockFetch).not.toHaveBeenCalled();
  });
});
