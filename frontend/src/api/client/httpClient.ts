import { ApiClient, ApiError } from '../generated/index.js';
import { NOT_AUTHENTICATED } from '../errors/kinds.js';
import { getEnv } from '../../lib/env.js';

/**
 * Authenticated HTTP transport over the generated client (Task 017).
 *
 * - Base URL comes from `lib/env` (`VITE_API_BASE_URL`); tests fall back to
 *   `http://localhost:5000` because `getEnv()` throws outside the app guard.
 * - The bearer token lives in memory only. `setTokenProvider` is called by
 *   Task 019 after login; until then (or after logout) the authenticated
 *   client throws `NOT_AUTHENTICATED` before touching the network. The
 *   anonymous client exists for login/refresh, which mint fresh secrets and
 *   never take an `Idempotency-Key` (bundle contract).
 * - Every `/api/v1` request carries `X-Correlation-ID` (opaque uuid, no
 *   tenant/user info). The ID is echoed into thrown `ApiError`s when the
 *   server envelope omits it, and `getLastCorrelationId()` exposes the most
 *   recent one as a fallback for telemetry (Task 018 reads per-request IDs
 *   from normalized errors instead).
 * - GET-only retry: network failures and 502/503/504 on GET are retried up
 *   to 2x with exponential backoff. Mutations are never auto-retried; the
 *   user retries explicitly with the same idempotency key. 401 is never
 *   retried and emits `auth:expired` for Task 019.
 * - Tokens are never logged and never attached to telemetry payloads.
 */

export { NOT_AUTHENTICATED };
export const AUTH_EXPIRED_EVENT = 'auth:expired';
export const CORRELATION_HEADER = 'X-Correlation-ID';
export const IDEMPOTENCY_HEADER = 'Idempotency-Key';

/** Thrown pre-flight when no in-memory token is available. */
export class NotAuthenticatedError extends Error {
  public readonly code = NOT_AUTHENTICATED;
  public readonly correlationId: string;
  public constructor(correlationId?: string) {
    super('Not authenticated.');
    this.name = 'NotAuthenticatedError';
    this.correlationId = correlationId ?? newCorrelationId();
  }
}

export interface AuthExpiredDetail {
  readonly correlationId: string;
  readonly status: number;
}

type TokenProvider = () => string | undefined;

let tokenProvider: TokenProvider | undefined;
let lastCorrelationId: string | undefined;

/** Registers the in-memory token reader (Task 019 owns the session). */
export function setTokenProvider(provider: TokenProvider): void {
  tokenProvider = provider;
}

/** Clears the in-memory token reader (logout). */
export function clearTokenProvider(): void {
  tokenProvider = undefined;
}

/** Best-effort token read; returns undefined when logged out. */
export function getTokenOrUndefined(): string | undefined {
  return tokenProvider?.();
}

/** Returns the token or throws `NotAuthenticatedError` (default behavior). */
export function requireAccessToken(): string {
  const token = tokenProvider?.();
  if (token === undefined || token === '') {
    throw new NotAuthenticatedError();
  }
  return token;
}

/** Opaque uuid for correlation; falls back to Math.random outside WebCrypto. */
export function newCorrelationId(): string {
  return newUuid();
}

/** Opaque uuid for `Idempotency-Key` on POST mutations. */
export function newIdempotencyKey(): string {
  return newUuid();
}

function newUuid(): string {
  const cryptoRef = globalThis.crypto as undefined | { randomUUID?: () => string; getRandomValues?: (a: Uint8Array) => Uint8Array };
  if (cryptoRef !== undefined && typeof cryptoRef.randomUUID === 'function') {
    return cryptoRef.randomUUID();
  }
  const bytes = new Uint8Array(16);
  if (cryptoRef !== undefined && typeof cryptoRef.getRandomValues === 'function') {
    cryptoRef.getRandomValues(bytes);
  } else {
    for (let i = 0; i < 16; i += 1) {
      bytes[i] = Math.floor(Math.random() * 256);
    }
  }
  const hex: string[] = Array.from(bytes, (b) => b.toString(16).padStart(2, '0'));
  const h = (i: number): string => hex[i] ?? '00';
  return `${h(0)}${h(1)}${h(2)}${h(3)}-${h(4)}${h(5)}-4${h(6).slice(1)}-${((parseInt(h(8), 16) & 0x3f) | 0x80).toString(16)}${h(9)}-${h(10)}${h(11)}${h(12)}${h(13)}${h(14)}${h(15)}`;
}

/** Most recent request correlation ID (fallback only; prefer per-error IDs). */
export function getLastCorrelationId(): string | undefined {
  return lastCorrelationId;
}

/** Emits `auth:expired` for Task 019. Never throws. */
export function emitAuthExpired(detail: AuthExpiredDetail): void {
  try {
    const target = typeof window !== 'undefined' ? window : globalThis;
    const dispatch = (target as { dispatchEvent?: (e: Event) => boolean }).dispatchEvent;
    if (typeof dispatch === 'function') {
      dispatch.call(target, new CustomEvent<AuthExpiredDetail>(AUTH_EXPIRED_EVENT, { detail }));
    }
  } catch {
    // Telemetry paths must never break the request flow.
  }
}

/** True for requests routed to the versioned API (the only ones we touch). */
export function isApiRequest(url: string): boolean {
  return url.includes('/api/v1');
}

/** Base URL from env with a hermetic fallback for unit tests. */
export function resolveBaseUrl(): string {
  try {
    return getEnv().apiBaseUrl.replace(/\/$/, '');
  } catch {
    return 'http://localhost:5000';
  }
}

const MAX_GET_RETRIES = 2;
const RETRYABLE_STATUS = new Set([502, 503, 504]);

function backoffMs(retryIndex: number): number {
  return 100 * 2 ** (retryIndex - 1);
}

function sleep(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    if (signal?.aborted === true) {
      reject(new DOMException('Aborted', 'AbortError'));
      return;
    }
    const timer = setTimeout(() => {
      signal?.removeEventListener('abort', onAbort);
      resolve();
    }, ms);
    const onAbort = (): void => {
      clearTimeout(timer);
      reject(new DOMException('Aborted', 'AbortError'));
    };
    signal?.addEventListener('abort', onAbort, { once: true });
  });
}

/**
 * Shared fetch core: injects `X-Correlation-ID` on API requests, retries
 * GETs on network failure + 502/503/504 (up to 2x), emits `auth:expired` on
 * 401 without retrying. Non-API requests pass through untouched.
 */
async function coreFetch(url: string, init: RequestInit, inner: typeof fetch): Promise<Response> {
  if (!isApiRequest(url)) {
    return inner(url, init);
  }
  const headers = new Headers(init.headers);
  let correlationId = headers.get(CORRELATION_HEADER);
  if (correlationId === null || correlationId === '') {
    correlationId = newCorrelationId();
    headers.set(CORRELATION_HEADER, correlationId);
  }
  lastCorrelationId = correlationId;
  const method = (init.method ?? 'GET').toUpperCase();
  const withHeaders: RequestInit = { ...init, headers };

  for (let attempt = 0; ; attempt += 1) {
    let response: Response;
    try {
      response = await inner(url, withHeaders);
    } catch (error) {
      const aborted = error instanceof DOMException && error.name === 'AbortError';
      if (!aborted && method === 'GET' && attempt < MAX_GET_RETRIES) {
        await sleep(backoffMs(attempt + 1), init.signal ?? undefined);
        continue;
      }
      throw error;
    }
    if (response.status === 401) {
      emitAuthExpired({ correlationId, status: 401 });
      return response;
    }
    if (method === 'GET' && RETRYABLE_STATUS.has(response.status) && attempt < MAX_GET_RETRIES) {
      await sleep(backoffMs(attempt + 1), init.signal ?? undefined);
      continue;
    }
    return response;
  }
}

// The generated client calls `globalThis.fetch` directly, so the interceptor
// is installed globally (scoped to `/api/v1`). `innerFetch` is swappable so
// unit tests can observe headers and retry counts without network access.
let fetchInstalled = false;
let savedFetch: typeof fetch | undefined;
let innerFetch: typeof fetch | undefined;

function currentFetch(): typeof fetch {
  if (innerFetch !== undefined) {
    return innerFetch;
  }
  if (typeof globalThis.fetch !== 'function') {
    throw new Error('globalThis.fetch is unavailable.');
  }
  return globalThis.fetch.bind(globalThis);
}

/** Installs the correlation/retry interceptor (idempotent). */
export function ensureFetchInterceptor(): void {
  if (fetchInstalled) {
    return;
  }
  savedFetch = globalThis.fetch;
  innerFetch = globalThis.fetch;
  const patched = (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
    const merged: RequestInit = { ...init };
    if (typeof input !== 'string' && !(input instanceof URL)) {
      merged.method = merged.method ?? input.method;
      if (merged.headers === undefined) {
        merged.headers = input.headers;
      }
    }
    return coreFetch(url, merged, currentFetch());
  };
  globalThis.fetch = patched as typeof fetch;
  fetchInstalled = true;
}

/** Test-only: observes requests through the interceptor without network. */
export function setInnerFetchForTests(impl: typeof fetch): void {
  innerFetch = impl;
}

/** Test-only: restores the real fetch implementation. */
export function restoreInnerFetchForTests(): void {
  innerFetch = savedFetch;
}

/** Test-only: removes the global patch (restores `globalThis.fetch`). */
export function uninstallFetchInterceptorForTests(): void {
  if (fetchInstalled && savedFetch !== undefined) {
    globalThis.fetch = savedFetch;
  }
  fetchInstalled = false;
  savedFetch = undefined;
  innerFetch = undefined;
  lastCorrelationId = undefined;
}

ensureFetchInterceptor();

function buildQuery(query?: Record<string, string | number | boolean | undefined>): string {
  if (!query) {
    return '';
  }
  const params = new URLSearchParams();
  for (const key of Object.keys(query).sort()) {
    const value = query[key];
    if (value !== undefined) {
      params.append(key, String(value));
    }
  }
  const text = params.toString();
  return text === '' ? '' : `?${text}`;
}

async function throwForResponse(response: Response, correlationId: string): Promise<never> {
  let code = 'INTERNAL_ERROR';
  let message = 'An unexpected error occurred.';
  let serverCorrelationId = '';
  let details: Record<string, unknown> = {};
  try {
    const body = (await response.json()) as {
      error?: { code?: string; message?: string; correlationId?: string; details?: Record<string, unknown> };
    };
    if (body && typeof body === 'object' && body.error) {
      if (typeof body.error.code === 'string') {
        code = body.error.code;
      }
      if (typeof body.error.message === 'string') {
        message = body.error.message;
      }
      if (typeof body.error.correlationId === 'string') {
        serverCorrelationId = body.error.correlationId;
      }
      if (body.error.details && typeof body.error.details === 'object') {
        details = body.error.details;
      }
    }
  } catch {
    // Non-JSON failure: keep the generic envelope.
  }
  throw new ApiError(response.status, code, message, serverCorrelationId !== '' ? serverCorrelationId : correlationId, details);
}

export type ApiMethod = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';

export interface ApiFetchOptions {
  readonly method?: ApiMethod;
  readonly query?: Record<string, string | number | boolean | undefined>;
  readonly body?: unknown;
  /** Caller-supplied key; POST auto-generates one when absent. Retries reuse it. */
  readonly idempotencyKey?: string;
  readonly ifMatch?: string;
  readonly signal?: AbortSignal;
  /** False for login/refresh (mint fresh secrets, never send a token). */
  readonly auth?: boolean;
}

/**
 * Explicit request helper with full header control. Prefer the typed
 * `apiClient` methods for bundle-covered routes; use this for raw needs
 * (downloads) or when the caller must own the idempotency key lifecycle.
 */
export async function apiFetch<T>(path: string, options?: ApiFetchOptions): Promise<T> {
  if (!path.startsWith('/')) {
    throw new Error('apiFetch path must start with "/".');
  }
  const method = options?.method ?? 'GET';
  const correlationId = newCorrelationId();
  lastCorrelationId = correlationId;
  const headers: Record<string, string> = { [CORRELATION_HEADER]: correlationId };
  if (options?.body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }
  if (options?.auth !== false) {
    headers['Authorization'] = `Bearer ${requireAccessToken()}`;
  }
  if (method === 'POST') {
    headers[IDEMPOTENCY_HEADER] = options?.idempotencyKey ?? newIdempotencyKey();
  } else if (options?.idempotencyKey !== undefined) {
    headers[IDEMPOTENCY_HEADER] = options.idempotencyKey;
  }
  if (options?.ifMatch !== undefined) {
    headers['If-Match'] = options.ifMatch;
  }
  const url = `${resolveBaseUrl()}/api/v1${path}${buildQuery(options?.query)}`;
  const response = await coreFetch(
    url,
    {
      method,
      headers,
      body: options?.body !== undefined ? JSON.stringify(options.body) : undefined,
      signal: options?.signal,
    },
    currentFetch(),
  );
  if (!response.ok) {
    await throwForResponse(response, correlationId);
  }
  if (response.status === 204) {
    return undefined as T;
  }
  return (await response.json()) as T;
}

/**
 * Authenticated generated client. `getToken` throws `NOT_AUTHENTICATED`
 * pre-flight when Task 019 has not registered (or has cleared) the provider.
 */
export const apiClient: ApiClient = new ApiClient({
  baseUrl: resolveBaseUrl(),
  getToken: requireAccessToken,
});

/** Anonymous generated client for login/refresh (never sends a token). */
export const anonymousClient: ApiClient = new ApiClient({ baseUrl: resolveBaseUrl() });
