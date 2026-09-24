import { getLastCorrelationId, newCorrelationId } from '../api/client/index.js';

/**
 * Request → error → telemetry correlation (Task 018). Correlation IDs are
 * opaque uuids (no tenant/user info); telemetry joins on them, never on
 * tokens, URLs, or content.
 */

/** Strips query/hash (and origin) so telemetry stores path-only routes. */
export function sanitizeRoute(input: string): string {
  const trimmed = input.trim();
  if (trimmed === '') {
    return '/';
  }
  try {
    // Absolute URLs: keep the pathname only (origin + query never logged).
    const url = new URL(trimmed);
    return url.pathname === '' ? '/' : url.pathname;
  } catch {
    // Relative routes: cut at the first query/hash boundary.
    const cut = trimmed.search(/[?#]/);
    const path = (cut >= 0 ? trimmed.slice(0, cut) : trimmed).trim();
    return path === '' ? '/' : path;
  }
}

interface CorrelationCarrier {
  readonly correlationId?: unknown;
}

function carrierId(value: unknown): string | undefined {
  if (typeof value !== 'object' || value === null) {
    return undefined;
  }
  const id = (value as CorrelationCarrier).correlationId;
  return typeof id === 'string' && id !== '' ? id : undefined;
}

/**
 * Resolves the telemetry correlation ID for any thrown value: normalized
 * `AppError`/generated `ApiError` IDs first, then the transport's last
 * request ID, then a fresh opaque ID (never empty, never a secret).
 */
export function resolveTelemetryCorrelation(error?: unknown): string {
  return carrierId(error) ?? getLastCorrelationId() ?? newCorrelationId();
}
