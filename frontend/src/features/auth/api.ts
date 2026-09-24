import { anonymousClient, apiClient } from '../../api/client/index.js';
import type { AuthLoginRequest, AuthLoginResponse, MeResponse } from '../../api/client/index.js';

/**
 * Auth API surface (Task 019) over the Task 017 transport.
 *
 * - `login`/`refresh` use the anonymous client: they mint fresh secrets and
 *   never send (or take) an `Idempotency-Key` per the bundle contract.
 * - `fetchMe` uses the authenticated client (Bearer pre-flight via the
 *   in-memory token provider Task 019 registers).
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

/** Current identity plus UX-hint permission strings. Requires a token. */
export async function fetchMe(): Promise<SessionIdentity> {
  return apiClient.getMe();
}
