import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  AUTH_EXPIRED_EVENT,
  ApiError,
  NOT_AUTHENTICATED,
  anonymousClient,
  apiClient,
  apiFetch,
  clearTokenProvider,
  getLastCorrelationId,
  newCorrelationId,
  newIdempotencyKey,
  requireAccessToken,
  restoreInnerFetchForTests,
  setInnerFetchForTests,
  setTokenProvider,
} from '../client/index.js';
import type { AuthExpiredDetail } from '../client/index.js';

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function errorBody(code: string, correlationId: string): unknown {
  return { error: { code, message: `backend ${code}`, correlationId, details: {} } };
}

function headerOf(call: readonly unknown[], name: string): string | null {
  const init = call[1] as RequestInit | undefined;
  const headers = new Headers(init?.headers);
  return headers.get(name);
}

const mockFetch = vi.fn<typeof fetch>();

function mockOk(body: unknown = { ok: true }): void {
  mockFetch.mockImplementation(() => Promise.resolve(jsonResponse(body)));
}

function expectNotAuthenticated(fn: () => unknown): void {
  try {
    fn();
  } catch (error) {
    expect((error as { code?: unknown }).code).toBe(NOT_AUTHENTICATED);
    return;
  }
  expect.unreachable('expected NotAuthenticatedError');
}

beforeEach(() => {
  mockFetch.mockReset();
  setInnerFetchForTests(mockFetch);
  clearTokenProvider();
});

afterEach(() => {
  restoreInnerFetchForTests();
  clearTokenProvider();
  vi.restoreAllMocks();
});

describe('httpClient auth (R3, in-memory token only)', () => {
  it('throws NOT_AUTHENTICATED pre-flight without a provider and never hits the network', async () => {
    await expect(apiClient.getMe()).rejects.toMatchObject({ code: NOT_AUTHENTICATED });
    expect(mockFetch).not.toHaveBeenCalled();
    expectNotAuthenticated(() => requireAccessToken());
  });

  it('attaches Bearer from the in-memory provider', async () => {
    setTokenProvider(() => 'tok-123');
    mockOk({ userId: 'u1' });
    await apiClient.getMe();
    expect(mockFetch).toHaveBeenCalledTimes(1);
    expect(headerOf(mockFetch.mock.calls[0] as readonly unknown[], 'Authorization')).toBe('Bearer tok-123');
  });

  it('treats empty tokens as logged out', () => {
    setTokenProvider(() => '');
    expectNotAuthenticated(() => requireAccessToken());
  });

  it('lets the anonymous client call login without a provider', async () => {
    mockOk({ accessToken: 'a', refreshToken: 'r' });
    await anonymousClient.authLogin({ path: {} }, { email: 'e', password: 'p', tenantSlug: 't' });
    expect(mockFetch).toHaveBeenCalledTimes(1);
    expect(headerOf(mockFetch.mock.calls[0] as readonly unknown[], 'Authorization')).toBeNull();
  });
});

describe('httpClient correlation (R2)', () => {
  it('sends a uuid X-Correlation-ID per request and remembers the last one', async () => {
    setTokenProvider(() => 'tok');
    mockOk({});
    await apiClient.getMe();
    await apiClient.getMe();
    const first = headerOf(mockFetch.mock.calls[0] as readonly unknown[], 'X-Correlation-ID');
    const second = headerOf(mockFetch.mock.calls[1] as readonly unknown[], 'X-Correlation-ID');
    expect(first).toMatch(UUID_RE);
    expect(second).toMatch(UUID_RE);
    expect(first).not.toBe(second);
    expect(getLastCorrelationId()).toBe(second);
  });

  it('echoes the request ID into errors when the envelope omits it', async () => {
    setTokenProvider(() => 'tok');
    mockFetch.mockResolvedValue(jsonResponse(errorBody('INTERNAL_ERROR', ''), 500));
    const failure = await apiFetch<{ ok: boolean }>('/me').then(
      () => null,
      (error: unknown) => error,
    );
    expect(failure).toBeInstanceOf(ApiError);
    const sent = headerOf(mockFetch.mock.calls[0] as readonly unknown[], 'X-Correlation-ID');
    expect((failure as ApiError).correlationId).toBe(sent);
  });

  it('generates uuid-shaped correlation and idempotency values', () => {
    expect(newCorrelationId()).toMatch(UUID_RE);
    expect(newIdempotencyKey()).toMatch(UUID_RE);
  });
});

describe('httpClient GET-only retry', () => {
  it('retries GET 503 up to success reusing the correlation ID', async () => {
    setTokenProvider(() => 'tok');
    mockFetch
      .mockResolvedValueOnce(jsonResponse(errorBody('PROVIDER_FAILED', 'c1'), 503))
      .mockResolvedValueOnce(jsonResponse({ userId: 'u1' }, 200));
    await apiClient.getMe();
    expect(mockFetch).toHaveBeenCalledTimes(2);
    expect(headerOf(mockFetch.mock.calls[0] as readonly unknown[], 'X-Correlation-ID')).toBe(
      headerOf(mockFetch.mock.calls[1] as readonly unknown[], 'X-Correlation-ID'),
    );
  });

  it('retries GET network failures', async () => {
    setTokenProvider(() => 'tok');
    mockFetch.mockRejectedValueOnce(new TypeError('fetch failed')).mockResolvedValueOnce(jsonResponse({ ok: true }, 200));
    const result = await apiFetch<{ ok: boolean }>('/me');
    expect(result).toEqual({ ok: true });
    expect(mockFetch).toHaveBeenCalledTimes(2);
  });

  it('never auto-retries POST, even on retryable statuses', async () => {
    setTokenProvider(() => 'tok');
    mockFetch.mockResolvedValue(jsonResponse(errorBody('PROVIDER_FAILED', 'c2'), 503));
    await expect(apiFetch('/projects', { method: 'POST', body: { sourceLanguage: 'en' } })).rejects.toBeInstanceOf(
      ApiError,
    );
    expect(mockFetch).toHaveBeenCalledTimes(1);
  });

  it('emits auth:expired on 401 without retrying', async () => {
    setTokenProvider(() => 'tok-stale');
    const seen: AuthExpiredDetail[] = [];
    const onExpired = (event: Event): void => {
      seen.push((event as CustomEvent<AuthExpiredDetail>).detail);
    };
    window.addEventListener(AUTH_EXPIRED_EVENT, onExpired);
    try {
      mockFetch.mockResolvedValue(jsonResponse(errorBody('TOKEN_EXPIRED', 'c3'), 401));
      await expect(apiFetch('/me')).rejects.toBeInstanceOf(ApiError);
      expect(mockFetch).toHaveBeenCalledTimes(1);
      expect(seen.length).toBe(1);
      expect(seen[0]?.status).toBe(401);
      expect(seen[0]?.correlationId).toBe(
        headerOf(mockFetch.mock.calls[0] as readonly unknown[], 'X-Correlation-ID'),
      );
    } finally {
      window.removeEventListener(AUTH_EXPIRED_EVENT, onExpired);
    }
  });
});

describe('httpClient idempotency keys (R5)', () => {
  it('auto-generates a key for POST and reuses caller keys across double-submit', async () => {
    setTokenProvider(() => 'tok');
    mockOk({ id: 'prj_1' });
    await apiFetch('/projects', { method: 'POST', body: {} });
    const auto = headerOf(mockFetch.mock.calls[0] as readonly unknown[], 'Idempotency-Key');
    expect(auto).toMatch(UUID_RE);

    await apiFetch('/projects', { method: 'POST', body: {}, idempotencyKey: 'key-abc' });
    await apiFetch('/projects', { method: 'POST', body: {}, idempotencyKey: 'key-abc' });
    expect(headerOf(mockFetch.mock.calls[1] as readonly unknown[], 'Idempotency-Key')).toBe('key-abc');
    expect(headerOf(mockFetch.mock.calls[2] as readonly unknown[], 'Idempotency-Key')).toBe('key-abc');
  });
});

describe('R3 no web-storage tokens', () => {
  it('reads tokens from memory only', () => {
    const hits: string[] = [];
    const walk = (dir: string): void => {
      for (const entry of readdirSync(dir)) {
        const full = join(dir, entry);
        if (statSync(full).isDirectory()) {
          walk(full);
          continue;
        }
        if (!/\.(ts|tsx)$/.test(entry) || full.includes(join('__tests__'))) {
          continue;
        }
        const text = readFileSync(full, 'utf8');
        if (/localStorage|sessionStorage/.test(text)) {
          hits.push(full);
        }
      }
    };
    walk(join(process.cwd(), 'src', 'api'));
    expect(hits).toEqual([]);
  });
});
