import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  clearTokenProvider,
  getTokenOrUndefined,
  restoreInnerFetchForTests,
  setInnerFetchForTests,
  setTokenProvider,
} from '../../../api/client/index.js';
import { queryClient } from '../../../app/providers/queryClient.js';
import { useAppStore } from '../../../stores/index.js';
import { computeRefreshDelayMs, useAuthStore } from '../authStore.js';
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
  permissions: ['project.view', 'project.edit'],
  roles: ['ProjectOwner'],
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
  return { error: { code, message: `backend ${code}`, correlationId: 'corr-1', details: {} } };
}

const mockFetch = vi.fn<typeof fetch>();

function mockLoginAndMe(loginBody: unknown = LOGIN_BODY, meBody: unknown = ME_BODY): void {
  mockFetch.mockImplementation((input) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.href : (input as Request).url;
    if (url.endsWith('/auth/login')) {
      return Promise.resolve(jsonResponse(loginBody));
    }
    if (url.endsWith('/me')) {
      return Promise.resolve(jsonResponse(meBody));
    }
    if (url.endsWith('/auth/logout')) {
      return Promise.resolve(jsonResponse({ loggedOut: true }));
    }
    return Promise.resolve(jsonResponse({}));
  });
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
  restoreInnerFetchForTests();
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  queryClient.clear();
  vi.restoreAllMocks();
  vi.useRealTimers();
});

describe('auth store transitions', () => {
  it('logs in, registers the token provider, and syncs the shell', async () => {
    mockLoginAndMe();
    await useAuthStore.getState().login({ email: 'owner@example.com', password: 'secret', tenantSlug: 'acme' });
    const state = useAuthStore.getState();
    expect(state.status).toBe('authenticated');
    expect(state.accessToken).toBe('access-1');
    expect(state.permissions).toEqual(['project.view', 'project.edit']);
    expect(getTokenOrUndefined()).toBe('access-1');
    expect(useAppStore.getState().sessionStatus).toBe('authenticated');
    expect(useAppStore.getState().permissions).toEqual(['project.view', 'project.edit']);
  });

  it('keeps global state on failed login and records the code', async () => {
    mockFetch.mockImplementation((input) => {
      const url = typeof input === 'string' ? input : (input as Request).url;
      if (url.endsWith('/auth/login')) {
        return Promise.resolve(jsonResponse(errorBody('INVALID_CREDENTIALS'), 401));
      }
      return Promise.resolve(jsonResponse({}));
    });
    await expect(
      useAuthStore.getState().login({ email: 'owner@example.com', password: 'wrong', tenantSlug: 'acme' }),
    ).rejects.toBeDefined();
    const state = useAuthStore.getState();
    expect(state.status).toBe('unknown');
    expect(state.accessToken).toBeUndefined();
    expect(state.lastErrorCode).toBe('INVALID_CREDENTIALS');
  });

  it('restores to anonymous with no memory tokens (R1: reload ends the session)', async () => {
    await useAuthStore.getState().restore();
    expect(useAuthStore.getState().status).toBe('anonymous');
    expect(useAppStore.getState().sessionStatus).toBe('anonymous');
    expect(mockFetch).not.toHaveBeenCalled();
  });

  it('marks 403 identity failures as error (USER_DISABLED)', async () => {
    mockFetch.mockImplementation((input) => {
      const url = typeof input === 'string' ? input : (input as Request).url;
      if (url.endsWith('/auth/login')) {
        return Promise.resolve(jsonResponse(LOGIN_BODY));
      }
      return Promise.resolve(jsonResponse(errorBody('USER_DISABLED'), 403));
    });
    await expect(
      useAuthStore.getState().login({ email: 'owner@example.com', password: 'secret', tenantSlug: 'acme' }),
    ).rejects.toBeDefined();
    expect(useAuthStore.getState().status).toBe('error');
    expect(useAuthStore.getState().lastErrorCode).toBe('USER_DISABLED');
    expect(useAppStore.getState().sessionStatus).toBe('error');
  });

  it('expires on 401 refresh and ignores stale sessions afterwards', async () => {
    mockLoginAndMe();
    await useAuthStore.getState().login({ email: 'o@e.c', password: 's', tenantSlug: 'acme' });
    mockFetch.mockImplementation((input) => {
      const url = typeof input === 'string' ? input : (input as Request).url;
      if (url.endsWith('/auth/refresh')) {
        return Promise.resolve(jsonResponse(errorBody('TOKEN_EXPIRED'), 401));
      }
      return Promise.resolve(jsonResponse({}));
    });
    const ok = await useAuthStore.getState().handleUnauthorized();
    expect(ok).toBe(false);
    expect(useAuthStore.getState().status).toBe('expired');
    expect(useAppStore.getState().sessionStatus).toBe('expired');
    // Second 401 while expired is a no-op (never retry-loops).
    const calls = mockFetch.mock.calls.length;
    const again = await useAuthStore.getState().handleUnauthorized();
    expect(again).toBe(false);
    expect(mockFetch.mock.calls.length).toBe(calls);
  });

  it('ignores unauthorized handling when not authenticated (login-form 401s stay local)', async () => {
    const ok = await useAuthStore.getState().handleUnauthorized();
    expect(ok).toBe(false);
    expect(mockFetch).not.toHaveBeenCalled();
  });
});

describe('refresh coalescing (R3)', () => {
  it('shares one in-flight refresh across concurrent callers', async () => {
    mockLoginAndMe();
    await useAuthStore.getState().login({ email: 'o@e.c', password: 's', tenantSlug: 'acme' });
    const loginCalls = mockFetch.mock.calls.length;

    let releaseRefresh!: (response: Response) => void;
    const gate = new Promise<Response>((resolve) => {
      releaseRefresh = resolve;
    });
    let refreshCalls = 0;
    mockFetch.mockImplementation((input) => {
      const url = typeof input === 'string' ? input : (input as Request).url;
      if (url.endsWith('/auth/refresh')) {
        refreshCalls += 1;
        return gate;
      }
      if (url.endsWith('/me')) {
        return Promise.resolve(jsonResponse(ME_BODY));
      }
      return Promise.resolve(jsonResponse({}));
    });

    const store = useAuthStore.getState();
    const pending = [store.refreshNow(), store.refreshNow(), store.refreshNow()];
    await Promise.resolve();
    releaseRefresh(
      jsonResponse({ ...LOGIN_BODY, accessToken: 'access-2', refreshToken: 'refresh-2' }),
    );
    const results = await Promise.all(pending);
    expect(results).toEqual([true, true, true]);
    expect(refreshCalls).toBe(1);
    expect(useAuthStore.getState().accessToken).toBe('access-2');
    expect(mockFetch.mock.calls.length).toBeGreaterThan(loginCalls);
  });

  it('shares one in-flight login across double-submits', async () => {
    let releaseLogin!: (response: Response) => void;
    const gate = new Promise<Response>((resolve) => {
      releaseLogin = resolve;
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
    const store = useAuthStore.getState();
    const pending = [
      store.login({ email: 'o@e.c', password: 's', tenantSlug: 'acme' }),
      store.login({ email: 'o@e.c', password: 's', tenantSlug: 'acme' }),
    ];
    await Promise.resolve();
    releaseLogin(jsonResponse(LOGIN_BODY));
    await Promise.all(pending);
    expect(loginCalls).toBe(1);
    expect(useAuthStore.getState().status).toBe('authenticated');
  });
});

describe('silent refresh schedule (80% lifetime)', () => {
  it('computes 80% of the token lifetime', () => {
    expect(computeRefreshDelayMs(900)).toBe(720_000);
    expect(computeRefreshDelayMs(100)).toBe(80_000);
    expect(computeRefreshDelayMs(0)).toBe(0);
    expect(computeRefreshDelayMs(-5)).toBe(0);
  });

  it('fires one refresh at the 80% point', async () => {
    vi.useFakeTimers();
    try {
      mockFetch.mockImplementation((input) => {
        const url = typeof input === 'string' ? input : (input as Request).url;
        if (url.endsWith('/auth/login')) {
          return Promise.resolve(jsonResponse({ ...LOGIN_BODY, expiresInSeconds: 100 }));
        }
        if (url.endsWith('/auth/refresh')) {
          return Promise.resolve(jsonResponse({ ...LOGIN_BODY, accessToken: 'access-r' }));
        }
        if (url.endsWith('/me')) {
          return Promise.resolve(jsonResponse(ME_BODY));
        }
        return Promise.resolve(jsonResponse({}));
      });
      await useAuthStore.getState().login({ email: 'o@e.c', password: 's', tenantSlug: 'acme' });
      const refreshBefore = mockFetch.mock.calls.filter((call) =>
        String(call[0]).includes('/auth/refresh'),
      ).length;
      await vi.advanceTimersByTimeAsync(80_000);
      const refreshAfter = mockFetch.mock.calls.filter((call) =>
        String(call[0]).includes('/auth/refresh'),
      ).length;
      expect(refreshAfter).toBe(refreshBefore + 1);
      expect(useAuthStore.getState().accessToken).toBe('access-r');
    } finally {
      vi.useRealTimers();
    }
  });
});

describe('logout (R4)', () => {
  it('clears cache + stores before resolving, then broadcasts without token material', async () => {
    mockLoginAndMe();
    await useAuthStore.getState().login({ email: 'o@e.c', password: 's', tenantSlug: 'acme' });
    queryClient.setQueryData(['projects', 'list'], { items: [] });
    expect(queryClient.getQueryData(['projects', 'list'])).toBeDefined();

    const setSpy = vi.spyOn(Storage.prototype, 'setItem');
    await useAuthStore.getState().logout();

    expect(queryClient.getQueryData(['projects', 'list'])).toBeUndefined();
    const state = useAuthStore.getState();
    expect(state.status).toBe('anonymous');
    expect(state.accessToken).toBeUndefined();
    expect(state.refreshToken).toBeUndefined();
    expect(getTokenOrUndefined()).toBeUndefined();
    expect(useAppStore.getState().sessionStatus).toBe('anonymous');
    expect(useAppStore.getState().permissions).toEqual([]);
    const logoutCalls = mockFetch.mock.calls.filter((call) => String(call[0]).includes('/auth/logout'));
    expect(logoutCalls.length).toBe(1);

    const storedValues = setSpy.mock.calls.map((call) => String(call[1] ?? ''));
    expect(storedValues.length).toBeGreaterThan(0);
    for (const value of storedValues) {
      expect(value).not.toContain('access-1');
      expect(value).not.toContain('refresh-1');
    }
  });

  it('clears locally even when the server call fails', async () => {
    mockLoginAndMe();
    await useAuthStore.getState().login({ email: 'o@e.c', password: 's', tenantSlug: 'acme' });
    mockFetch.mockRejectedValue(new TypeError('fetch failed'));
    await useAuthStore.getState().logout();
    expect(useAuthStore.getState().status).toBe('anonymous');
    expect(useAppStore.getState().sessionStatus).toBe('anonymous');
  });
});

describe('no token persistence (R1)', () => {
  it('never writes token material to web storage across login/refresh/logout', async () => {
    mockLoginAndMe();
    const setSpy = vi.spyOn(Storage.prototype, 'setItem');
    const sessionSpy = vi.spyOn(window.sessionStorage.__proto__ as Storage, 'setItem');
    await useAuthStore.getState().login({ email: 'o@e.c', password: 's', tenantSlug: 'acme' });

    mockFetch.mockImplementation((input) => {
      const url = typeof input === 'string' ? input : (input as Request).url;
      if (url.endsWith('/auth/refresh')) {
        return Promise.resolve(jsonResponse({ ...LOGIN_BODY, accessToken: 'access-2', refreshToken: 'refresh-2' }));
      }
      if (url.endsWith('/me')) {
        return Promise.resolve(jsonResponse(ME_BODY));
      }
      if (url.endsWith('/auth/logout')) {
        return Promise.resolve(jsonResponse({ loggedOut: true }));
      }
      return Promise.resolve(jsonResponse({}));
    });
    await useAuthStore.getState().refreshNow();
    await useAuthStore.getState().logout();

    const values = [...setSpy.mock.calls, ...sessionSpy.mock.calls].map((call) => String(call[1] ?? ''));
    for (const secret of ['access-1', 'refresh-1', 'access-2', 'refresh-2']) {
      expect(values.filter((v) => v.includes(secret))).toEqual([]);
    }
  });

  it('registers the provider without touching storage', () => {
    setTokenProvider(() => 'mem-only');
    expect(getTokenOrUndefined()).toBe('mem-only');
    clearTokenProvider();
  });
});

describe('offline refresh', () => {
  it('retries once on reconnect, else expires', async () => {
    mockLoginAndMe();
    await useAuthStore.getState().login({ email: 'o@e.c', password: 's', tenantSlug: 'acme' });
    Object.defineProperty(window.navigator, 'onLine', { value: false, configurable: true });
    try {
      let refreshCalls = 0;
      mockFetch.mockImplementation((input) => {
        const url = typeof input === 'string' ? input : (input as Request).url;
        if (url.endsWith('/auth/refresh')) {
          refreshCalls += 1;
          return Promise.reject(new TypeError('fetch failed'));
        }
        if (url.endsWith('/me')) {
          return Promise.resolve(jsonResponse(ME_BODY));
        }
        return Promise.resolve(jsonResponse({}));
      });
      const pending = useAuthStore.getState().refreshNow({ offlineTimeoutMs: 500 });
      // Let the rejection propagate to the offline waiter before reconnecting.
      await new Promise((resolve) => {
        setTimeout(resolve, 10);
      });
      window.dispatchEvent(new Event('online'));
      Object.defineProperty(window.navigator, 'onLine', { value: true, configurable: true });
      const ok = await pending;
      expect(ok).toBe(false);
      expect(refreshCalls).toBe(2);
      expect(useAuthStore.getState().status).toBe('expired');
    } finally {
      Object.defineProperty(window.navigator, 'onLine', { value: true, configurable: true });
    }
  });
});
