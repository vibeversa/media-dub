import { anonymousClient, apiClient, apiFetch } from '../../api/client/index.js';
import type { AuthLoginRequest, AuthLoginResponse, MeResponse } from '../../api/client/index.js';

/**
 * Auth API surface (Task 019) over the Task 017 transport.
 *
 * - `login`/`refresh` use the anonymous client: they mint fresh secrets and
 *   never send (or take) an `Idempotency-Key` per the bundle contract.
 * - `fetchMeDocument` uses the same authenticated transport and Bearer pre-flight
 *   via the in-memory token provider Task 019 registers.
 * - `logout` is best-effort idempotent: unknown/absent refresh tokens still
 *   return 200 server-side, and callers clear local state regardless.
 * - Shapes come from the generated barrel only (`src/api/client`); no deep
 *   `api/generated` imports (enforced by `no-restricted-imports`).
 * - Never logs tokens, credentials, or responses — arguments pass straight
 *   through to the transport.
 */

export type LoginCredentials = AuthLoginRequest;

export type SessionTokens = AuthLoginResponse;

export type SessionIdentity = MeResponse;

/** Mints a 15-minute access JWT plus a 7-day refresh session. */
export async function login(credentials: LoginCredentials): Promise<SessionTokens> {
  return anonymousClient.authLogin({ path: {} }, credentials);
}

/** Rotates `{sessionId:N}.{secret}`; reuse revokes the family (401). */
export async function refreshSession(refreshToken: string): Promise<SessionTokens> {
  return anonymousClient.authRefresh({ path: {} }, { refreshToken });
}

/**
 * Revokes the refresh session. Never throws: revocation is best-effort and
 * local state is always cleared by the caller afterwards.
 */
export async function logout(refreshToken: string | undefined): Promise<void> {
  try {
    if (refreshToken === undefined || refreshToken === '') {
      await anonymousClient.authLogout({ path: {} }, undefined);
    } else {
      await anonymousClient.authLogout({ path: {} }, { refreshToken });
    }
  } catch {
    // Idempotent server-side; local clearing must proceed regardless.
  }
}

/**
 * Current identity plus UX-hint permission strings. Requires a token.
 *
 * The app has exactly ONE `/me` read, and it is {@link fetchMeDocument} +
 * {@link readMePermissions} (`authStore`'s `resolveIdentityHints`). This was the
 * typed `apiClient.getMe()` call until Task 045, but the locale preference and
 * the permission hints come from the *same* document: two reads of one resource
 * is a race with itself, and it costs a round trip on every session resolution
 * for no additional information.
 *
 * It stays because the typed client is the right way to call a bundle-covered
 * route; `fetchMeDocument` exists only because the bundle's `MeResponse` is
 * behind the server record and does not declare `locale`.
 */
export async function fetchMe(): Promise<SessionIdentity> {
  return apiClient.getMe();
}

/**
 * The raw `GET /me` document.
 *
 * Task 045 needs `MeResponse.Locale`, and the committed OpenAPI bundle's
 * `MeResponse` declares only `userId`/`tenantId`/`permissions`/`roles` — the
 * server record (`AuthMeDtos.MeResponse`) carries `locale`, `featureFlags` and
 * `session`, and the bundle is behind it. Reading the raw document and parsing
 * defensively is the same choice Task 044 makes for `/me`'s flag slice: against
 * an older backend the field is absent, the parse yields `undefined`, and the
 * locale chain continues to the browser default.
 *
 * `unknown`, not `MeResponse`, because the point is that the generated type is
 * not the whole truth here.
 */
export async function fetchMeDocument(signal?: AbortSignal): Promise<unknown> {
  return apiFetch<unknown>('/me', signal !== undefined ? { signal } : undefined);
}

/**
 * The `permissions` hint list out of a raw `/me` document. Pure.
 *
 * The field is `required` in the bundle, so a real backend always sends it — but
 * `fetchMeDocument` is typed `unknown` precisely because the generated type is
 * not the whole truth here, and a permission list read out of an unvalidated
 * `unknown` with a cast would be a cast that can be wrong. Anything that is not
 * an array of strings resolves to `[]`: the caller's fallback for an unreadable
 * document, and the safe direction, because these are UX hints that hide UI and
 * never authorize anything (every controller still enforces its own policy).
 */
export function readMePermissions(raw: unknown): readonly string[] {
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) {
    return [];
  }
  const permissions = (raw as Record<string, unknown>)['permissions'];
  if (!Array.isArray(permissions)) {
    return [];
  }
  return permissions.filter((entry): entry is string => typeof entry === 'string' && entry !== '');
}
