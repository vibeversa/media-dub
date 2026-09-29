// Delta 2: auth remaining-branch closure.
//
// Supplements authStore.test (login, refresh coalescing, silent schedule,
// logout, offline-reconnect expiry) with the uncovered branches: restore
// with live access tokens (valid, 401-with-refresh, 401-without-refresh,
// non-401 errors, refresh-only), refresh-then-identity failure, non-401
// refresh classification, offline timeout without reconnect, reconnect
// success, broadcast-storage failure tolerance, logoutLocal, unauthorized
// success, and delay computation guards. Synthetic envelopes only.
//
// Intentional-exclusion candidate: `waitForOnlineOnce`'s no-`window`
// fallback is unreachable under jsdom and left uncovered.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  clearTokenProvider,
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
  permissions: ['project.view'],
  roles: ['ProjectOwner'],
  userId: 'usr_1',
  tenantId: 'tenant_1',
};

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorBody(code: string): unknown {
  return { error: { code, message: `backend ${code}`, correlationId: 'corr-2', details: {} } };
}

const mockFetch = vi.fn<typeof fetch>();

function route(impl: (url: string) => Promise<Response>): void {
  mockFetch.mockImplementation((input) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.href : (input as Request).url;
    return impl(url);
  });
}

function loginThenMe(loginBody: unknown = LOGIN_BODY, meBody: unknown = ME_BODY): void {
  route((url) => {
    if (url.endsWith('/auth/login')) return Promise.resolve(jsonResponse(loginBody));
    if (url.endsWith('/me')) return Promise.resolve(jsonResponse(meBody));
    if (url.endsWith('/auth/logout')) return Promise.resolve(jsonResponse({ loggedOut: true }));
    if (url.endsWith('/auth/refresh')) return Promise.resolve(jsonResponse(LOGIN_BODY));
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
  Object.defineProperty(window.navigator, 'onLine', { value: true, configurable: true });
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
  Object.defineProperty(window.navigator, 'onLine', { value: true, configurable: true });
});

describe('restore branches', () => {
  it('authenticates directly when memory tokens still validate', async () => {
    useAuthStore.setState({ accessToken: 'access-1', refreshToken: 'refresh-1' });
    setTokenProvider(() => 'access-1');
    route((url) => {
      if (url.endsWith('/me')) return Promise.resolve(jsonResponse(ME_BODY));
      return Promise.resolve(jsonResponse({}));
    });
    await useAuthStore.getState().restore();
    expect(useAuthStore.getState().status).toBe('authenticated');
  });

  it('refreshes through a 401 identity check when a refresh token exists', async () => {
    useAuthStore.setState({ accessToken: 'access-1', refreshToken: 'refresh-1' });
    setTokenProvider(() => useAuthStore.getState().accessToken);
    let meCalls = 0;
    route((url) => {
      if (url.endsWith('/me')) {
        meCalls += 1;
        if (meCalls === 1) return Promise.resolve(jsonResponse(errorBody('TOKEN_EXPIRED'), 401));
        return Promise.resolve(jsonResponse(ME_BODY));
      }
      if (url.endsWith('/auth/refresh')) return Promise.resolve(jsonResponse({ ...LOGIN_BODY, accessToken: 'access-2' }));
      return Promise.resolve(jsonResponse({}));
    });
    await useAuthStore.getState().restore();
    expect(useAuthStore.getState().status).toBe('authenticated');
    expect(useAuthStore.getState().accessToken).toBe('access-2');
  });

  it('expires when identity rejects without any refresh token', async () => {
    useAuthStore.setState({ accessToken: 'access-1', refreshToken: undefined });
    setTokenProvider(() => 'access-1');
    route((url) => {
      if (url.endsWith('/me')) return Promise.resolve(jsonResponse(errorBody('TOKEN_EXPIRED'), 401));
      return Promise.resolve(jsonResponse({}));
    });
    await useAuthStore.getState().restore();
    expect(useAuthStore.getState().status).toBe('expired');
  });

  it('marks non-401 identity failures as error state', async () => {
    useAuthStore.setState({ accessToken: 'access-1', refreshToken: undefined });
    setTokenProvider(() => 'access-1');
    route((url) => {
      if (url.endsWith('/me')) return Promise.resolve(jsonResponse(errorBody('INTERNAL_ERROR'), 500));
      return Promise.resolve(jsonResponse({}));
    });
    await useAuthStore.getState().restore();
    expect(useAuthStore.getState().status).toBe('error');
    expect(useAuthStore.getState().lastErrorCode).toBe('INTERNAL_ERROR');
  });

  it('restores through the refresh token alone', async () => {
    loginThenMe();
    useAuthStore.setState({ accessToken: undefined, refreshToken: 'refresh-1' });
    route((url) => {
      if (url.endsWith('/auth/refresh')) return Promise.resolve(jsonResponse(LOGIN_BODY));
      if (url.endsWith('/me')) return Promise.resolve(jsonResponse(ME_BODY));
      return Promise.resolve(jsonResponse({}));
    });
    await useAuthStore.getState().restore();
    expect(useAuthStore.getState().status).toBe('authenticated');
  });

  it('shares one in-flight restore across concurrent callers', async () => {
    useAuthStore.setState({ accessToken: 'access-1', refreshToken: undefined });
    setTokenProvider(() => 'access-1');
    let meCalls = 0;
    route((url) => {
      if (url.endsWith('/me')) {
        meCalls += 1;
        return Promise.resolve(jsonResponse(ME_BODY));
      }
      return Promise.resolve(jsonResponse({}));
    });
    const store = useAuthStore.getState();
    await Promise.all([store.restore(), store.restore()]);
    expect(meCalls).toBe(1);
    expect(useAuthStore.getState().status).toBe('authenticated');
  });
});

describe('refresh confirm + classification branches', () => {
  it('expires when identity fails after a rotation', async () => {
    loginThenMe();
    await useAuthStore.getState().login({ tenantId: '11111111-1111-1111-1111-111111111111', externalSubject: 'o@e.c' });
    route((url) => {
      if (url.endsWith('/auth/refresh')) return Promise.resolve(jsonResponse({ ...LOGIN_BODY, accessToken: 'access-2' }));
      if (url.endsWith('/me')) return Promise.resolve(jsonResponse(errorBody('TOKEN_EXPIRED'), 401));
      return Promise.resolve(jsonResponse({}));
    });
    expect(await useAuthStore.getState().refreshNow()).toBe(false);
    expect(useAuthStore.getState().status).toBe('expired');
  });

  it('marks non-401 refresh failures as error state', async () => {
    loginThenMe();
    await useAuthStore.getState().login({ tenantId: '11111111-1111-1111-1111-111111111111', externalSubject: 'o@e.c' });
    route((url) => {
      if (url.endsWith('/auth/refresh')) return Promise.resolve(jsonResponse(errorBody('PROVIDER_DOWN'), 503));
      return Promise.resolve(jsonResponse({}));
    });
    expect(await useAuthStore.getState().refreshNow()).toBe(false);
    expect(useAuthStore.getState().status).toBe('error');
    expect(useAuthStore.getState().lastErrorCode).toBe('PROVIDER_DOWN');
  });

  it('expires when no refresh token exists on an authenticated session', async () => {
    loginThenMe();
    await useAuthStore.getState().login({ tenantId: '11111111-1111-1111-1111-111111111111', externalSubject: 'o@e.c' });
    useAuthStore.setState({ refreshToken: undefined });
    expect(await useAuthStore.getState().refreshNow()).toBe(false);
    expect(useAuthStore.getState().status).toBe('expired');
  });

  it('keeps anonymous sessions quiet on refresh without tokens', async () => {
    expect(await useAuthStore.getState().refreshNow()).toBe(false);
    expect(mockFetch).not.toHaveBeenCalled();
  });
});

describe('offline refresh branches', () => {
  it('expires on offline timeout without reconnect (recovery: retry)', async () => {
    loginThenMe();
    await useAuthStore.getState().login({ tenantId: '11111111-1111-1111-1111-111111111111', externalSubject: 'o@e.c' });
    Object.defineProperty(window.navigator, 'onLine', { value: false, configurable: true });
    try {
      route((url) => {
        if (url.endsWith('/auth/refresh')) return Promise.reject(new TypeError('fetch failed'));
        if (url.endsWith('/me')) return Promise.resolve(jsonResponse(ME_BODY));
        return Promise.resolve(jsonResponse({}));
      });
      const ok = await useAuthStore.getState().refreshNow({ offlineTimeoutMs: 50 });
      expect(ok).toBe(false);
      expect(useAuthStore.getState().status).toBe('expired');
      expect(useAuthStore.getState().lastErrorCode).toBe('NETWORK_ERROR');
    } finally {
      Object.defineProperty(window.navigator, 'onLine', { value: true, configurable: true });
    }
  });

  it('recovers when the reconnect retry succeeds', async () => {
    loginThenMe();
    await useAuthStore.getState().login({ tenantId: '11111111-1111-1111-1111-111111111111', externalSubject: 'o@e.c' });
    Object.defineProperty(window.navigator, 'onLine', { value: false, configurable: true });
    try {
      let refreshCalls = 0;
      route((url) => {
        if (url.endsWith('/auth/refresh')) {
          refreshCalls += 1;
          if (refreshCalls === 1) return Promise.reject(new TypeError('fetch failed'));
          return Promise.resolve(jsonResponse({ ...LOGIN_BODY, accessToken: 'access-9' }));
        }
        if (url.endsWith('/me')) return Promise.resolve(jsonResponse(ME_BODY));
        return Promise.resolve(jsonResponse({}));
      });
      const pending = useAuthStore.getState().refreshNow({ offlineTimeoutMs: 2000 });
      await new Promise((resolve) => setTimeout(resolve, 10));
      Object.defineProperty(window.navigator, 'onLine', { value: true, configurable: true });
      window.dispatchEvent(new Event('online'));
      expect(await pending).toBe(true);
      expect(useAuthStore.getState().accessToken).toBe('access-9');
    } finally {
      Object.defineProperty(window.navigator, 'onLine', { value: true, configurable: true });
    }
  });
});

describe('logout + unauthorized + delay guards', () => {
  it('broadcasts logout even when storage throws', async () => {
    loginThenMe();
    await useAuthStore.getState().login({ tenantId: '11111111-1111-1111-1111-111111111111', externalSubject: 'o@e.c' });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('denied');
    });
    await expect(useAuthStore.getState().logout()).resolves.toBeUndefined();
    expect(useAuthStore.getState().status).toBe('anonymous');
  });

  it('clears locally without a server round-trip', async () => {
    loginThenMe();
    await useAuthStore.getState().login({ tenantId: '11111111-1111-1111-1111-111111111111', externalSubject: 'o@e.c' });
    const calls = mockFetch.mock.calls.length;
    useAuthStore.getState().logoutLocal();
    expect(useAuthStore.getState().status).toBe('anonymous');
    expect(mockFetch.mock.calls.length).toBe(calls);
  });

  it('resolves true when the session re-validates', async () => {
    loginThenMe();
    await useAuthStore.getState().login({ tenantId: '11111111-1111-1111-1111-111111111111', externalSubject: 'o@e.c' });
    route((url) => {
      if (url.endsWith('/auth/refresh')) return Promise.resolve(jsonResponse({ ...LOGIN_BODY, accessToken: 'access-2' }));
      if (url.endsWith('/me')) return Promise.resolve(jsonResponse(ME_BODY));
      return Promise.resolve(jsonResponse({}));
    });
    expect(await useAuthStore.getState().handleUnauthorized()).toBe(true);
  });

  it('clamps non-finite lifetimes to zero delay', () => {
    expect(computeRefreshDelayMs(Number.NaN)).toBe(0);
    expect(computeRefreshDelayMs(Number.POSITIVE_INFINITY)).toBe(0);
    expect(computeRefreshDelayMs(0.5)).toBe(400);
  });
});
