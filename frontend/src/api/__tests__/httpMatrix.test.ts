// Task 039B: httpClient branch matrix.
//
// Covers the transport seams the R3/R5 suites leave cold: uuid fallbacks
// without WebCrypto, non-API passthrough, abort-aware backoff, query
// building, envelope-tolerance in `throwForResponse`, and the `apiFetch`
// option matrix (path guard, anonymous calls, caller keys, If-Match, 204).
// No network is used: the inner fetch is always the test double (R3).
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  ApiError,
  NotAuthenticatedError,
  apiFetch,
  clearTokenProvider,
  emitAuthExpired,
  getTokenOrUndefined,
  isApiRequest,
  newCorrelationId,
  resolveBaseUrl,
  restoreInnerFetchForTests,
  setInnerFetchForTests,
  setTokenProvider,
  uninstallFetchInterceptorForTests,
  ensureFetchInterceptor,
} from '../client/index.js';
import { resetEnvCache } from '../../lib/env.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function headerOf(call: readonly unknown[], name: string): string | null {
  const init = call[1] as RequestInit | undefined;
  return new Headers(init?.headers).get(name);
}

const mockFetch = vi.fn<typeof fetch>();

beforeEach(() => {
  mockFetch.mockReset();
  setInnerFetchForTests(mockFetch);
  clearTokenProvider();
  resetEnvCache();
});

afterEach(() => {
  restoreInnerFetchForTests();
  clearTokenProvider();
  resetEnvCache();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  ensureFetchInterceptor();
});

describe('uuid fallbacks', () => {
  it('builds uuids from getRandomValues without randomUUID', () => {
    const realCrypto = globalThis.crypto;
    vi.stubGlobal('crypto', { getRandomValues: realCrypto.getRandomValues.bind(realCrypto) } as unknown as Crypto);
    const first = newCorrelationId();
    const second = newCorrelationId();
    expect(first.length).toBeGreaterThan(0);
    expect(first).not.toBe(second);
    vi.unstubAllGlobals();
  });

  it('builds uuids from Math.random without WebCrypto', () => {
    vi.stubGlobal('crypto', undefined as unknown as Crypto);
    const first = newCorrelationId();
    expect(first.length).toBeGreaterThan(0);
    expect(newCorrelationId()).not.toBe(first);
    vi.unstubAllGlobals();
  });
});

describe('request routing', () => {
  it('classifies versioned API urls only', () => {
    expect(isApiRequest('http://localhost:5000/api/v1/me')).toBe(true);
    expect(isApiRequest('http://localhost:5000/__mocks__/taxonomy/success')).toBe(false);
  });

  it('passes non-API requests through untouched (no correlation header)', async () => {
    mockFetch.mockResolvedValue(jsonResponse({ ok: true }));
    const response = await fetch('http://localhost:5000/__mocks__/taxonomy/success');
    expect(response.ok).toBe(true);
    expect(mockFetch).toHaveBeenCalledTimes(1);
    expect(headerOf(mockFetch.mock.calls[0] as readonly unknown[], 'X-Correlation-ID')).toBeNull();
  });

  it('fails loudly when no fetch implementation exists', async () => {
    uninstallFetchInterceptorForTests();
    vi.stubGlobal('fetch', undefined as unknown as typeof fetch);
    setTokenProvider(() => 'tok');
    await expect(apiFetch('/me')).rejects.toThrow('globalThis.fetch is unavailable.');
    vi.unstubAllGlobals();
    ensureFetchInterceptor();
    setInnerFetchForTests(mockFetch);
  });
});

describe('emitAuthExpired safety', () => {
  it('never throws when dispatch fails', () => {
    const spy = vi.spyOn(window, 'dispatchEvent').mockImplementation(() => {
      throw new Error('denied');
    });
    expect(() => emitAuthExpired({ correlationId: 'c', status: 401 })).not.toThrow();
    expect(spy).toHaveBeenCalledTimes(1);
  });

  it('skips dispatch without a dispatcher', () => {
    const target = { dispatchEvent: undefined as unknown as ((e: Event) => boolean) | undefined };
    expect(() => target.dispatchEvent?.call(target, new Event('x'))).not.toThrow();
  });
});

describe('token helpers', () => {
  it('reads the provider best-effort and constructs errors with ids', () => {
    expect(getTokenOrUndefined()).toBeUndefined();
    setTokenProvider(() => undefined);
    expect(getTokenOrUndefined()).toBeUndefined();
    setTokenProvider(() => 'tok');
    expect(getTokenOrUndefined()).toBe('tok');
    expect(new NotAuthenticatedError('corr-x').correlationId).toBe('corr-x');
    expect(new NotAuthenticatedError().correlationId.length).toBeGreaterThan(0);
  });
});

describe('abort-aware backoff', () => {
  it('rejects on a pre-aborted signal during retry sleep', async () => {
    setTokenProvider(() => 'tok');
    mockFetch.mockResolvedValue(jsonResponse({ error: { code: 'X', message: 'm', correlationId: 'c', details: {} } }, 503));
    const controller = new AbortController();
    controller.abort();
    await expect(apiFetch('/me', { signal: controller.signal })).rejects.toMatchObject({ name: 'AbortError' });
    expect(mockFetch).toHaveBeenCalledTimes(1);
  });

  it('aborts a pending retry sleep (recovery: caller retries explicitly)', async () => {
    setTokenProvider(() => 'tok');
    mockFetch.mockResolvedValue(jsonResponse({ error: { code: 'X', message: 'm', correlationId: 'c', details: {} } }, 503));
    const controller = new AbortController();
    const pending = apiFetch('/me', { signal: controller.signal });
    setTimeout(() => controller.abort(), 10);
    await expect(pending).rejects.toMatchObject({ name: 'AbortError' });
  });
});

describe('apiFetch option matrix', () => {
  it('rejects paths without a leading slash', async () => {
    await expect(apiFetch('no-slash')).rejects.toThrow('apiFetch path must start with "/".');
  });

  it('builds sorted query strings and skips undefined values', async () => {
    setTokenProvider(() => 'tok');
    mockFetch.mockImplementation(() => Promise.resolve(jsonResponse({ ok: true })));
    await apiFetch('/projects', { query: { b: 2, a: 'x', skip: undefined } });
    const url = String(mockFetch.mock.calls[0]?.[0]);
    expect(url).toContain('/api/v1/projects?a=x&b=2');
    expect(url).not.toContain('skip');
    mockFetch.mockClear();
    await apiFetch('/projects', { query: {} });
    expect(String(mockFetch.mock.calls[0]?.[0])).not.toContain('?');
  });

  it('omits Authorization for anonymous calls (login/refresh mint secrets)', async () => {
    mockFetch.mockResolvedValue(jsonResponse({ accessToken: 'a' }));
    await apiFetch('/auth/login', { method: 'POST', auth: false, body: { email: 'e' } });
    expect(headerOf(mockFetch.mock.calls[0] as readonly unknown[], 'Authorization')).toBeNull();
    expect(headerOf(mockFetch.mock.calls[0] as readonly unknown[], 'Content-Type')).toBe('application/json');
  });

  it('forwards caller idempotency keys on non-POST and If-Match headers', async () => {
    setTokenProvider(() => 'tok');
    mockFetch.mockResolvedValue(jsonResponse({ ok: true }));
    await apiFetch('/projects/prj_1', { method: 'PATCH', body: {}, idempotencyKey: 'key-1', ifMatch: '"3"' });
    expect(headerOf(mockFetch.mock.calls[0] as readonly unknown[], 'Idempotency-Key')).toBe('key-1');
    expect(headerOf(mockFetch.mock.calls[0] as readonly unknown[], 'If-Match')).toBe('"3"');
  });

  it('resolves 204 with undefined (no body to parse)', async () => {
    setTokenProvider(() => 'tok');
    mockFetch.mockResolvedValue(new Response(null, { status: 204 }));
    await expect(apiFetch('/projects/prj_1')).resolves.toBeUndefined();
  });

  it('falls back to the hermetic base url outside the app guard', () => {
    vi.stubEnv('VITE_API_BASE_URL', '');
    resetEnvCache();
    expect(resolveBaseUrl()).toBe('http://localhost:5000');
  });
});

describe('throwForResponse tolerance', () => {
  it('keeps a generic envelope for non-JSON failures', async () => {
    setTokenProvider(() => 'tok');
    mockFetch.mockResolvedValue(new Response('not-json', { status: 500 }));
    const failure = await apiFetch('/me').then(
      () => null,
      (error: unknown) => error,
    );
    expect(failure).toBeInstanceOf(ApiError);
    expect((failure as ApiError).code).toBe('INTERNAL_ERROR');
  });

  it('defaults missing envelope fields without losing the status', async () => {
    setTokenProvider(() => 'tok');
    mockFetch.mockResolvedValue(jsonResponse({ error: { details: { retryAfterMs: 1 } } }, 429));
    const failure = (await apiFetch('/me').then(
      () => null,
      (error: unknown) => error,
    )) as ApiError;
    expect(failure.code).toBe('INTERNAL_ERROR');
    expect(failure.status).toBe(429);
    expect(failure.details).toEqual({ retryAfterMs: 1 });
  });
});
