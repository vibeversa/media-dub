/**
 * Normalized error kinds (Task 017).
 *
 * Every backend failure collapses into exactly one kind so UI code can branch
 * without knowing the 65-code catalog. `Unknown` is the fallback: it always
 * carries a correlation ID and a "report this ID" hint for support triage.
 */
export type ErrorKind =
  | 'Validation'
  | 'Auth'
  | 'Conflict'
  | 'Quota'
  | 'Media'
  | 'Review'
  | 'Unknown';

/**
 * UI-safe error shape. `message` and `recoveryHint` are plain text only and
 * must be rendered as text, never HTML. No media bytes, transcript content,
 * tokens, or URLs are ever attached here.
 */
export interface AppError {
  readonly kind: ErrorKind;
  /** Backend `ErrorCode`, `NOT_AUTHENTICATED`, or `NETWORK_ERROR`. */
  readonly code: string;
  readonly message: string;
  /** Opaque ID for support triage; shown in error UI as "report this ID". */
  readonly correlationId: string;
  /**
   * True only for idempotent GETs against transient statuses (502/503/504)
   * and for network failures. Mutations are never auto-retried; the user
   * retries explicitly with the same idempotency key.
   */
  readonly retryable: boolean;
  /** Plain-text next step, e.g. re-login, refresh-and-retry, manage usage. */
  readonly recoveryHint: string;
  readonly status?: number;
  readonly details?: Record<string, unknown>;
}

/** Frontend-only code for pre-flight unauthenticated calls (no backend twin). */
export const NOT_AUTHENTICATED = 'NOT_AUTHENTICATED';
/** Frontend-only code for fetch-level network failures (no backend twin). */
export const NETWORK_ERROR = 'NETWORK_ERROR';

/** Type guard for values already normalized by `normalizeError`. */
export function isAppError(value: unknown): value is AppError {  if (typeof value !== 'object' || value === null) {
    return false;
  }
  const record = value as Record<string, unknown>;
  return (
    typeof record['kind'] === 'string' &&
    typeof record['code'] === 'string' &&
    typeof record['message'] === 'string' &&
    typeof record['correlationId'] === 'string' &&
    typeof record['retryable'] === 'boolean' &&
    typeof record['recoveryHint'] === 'string'
  );
}
