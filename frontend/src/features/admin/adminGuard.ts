import { useEffect, useRef } from 'react';
import { hasAdminPermission } from '../../app/session/permissions.js';
import { useAppStore } from '../../stores/index.js';
import { resolveTelemetryCorrelation } from '../../telemetry/correlation.js';
import { emitTelemetryEvent } from '../../telemetry/telemetry.js';
import { isAdminForbiddenError } from './types.js';

export { isAdminForbiddenError };

/**
 * Elevated admin gate (Task 036, R1).
 *
 * The route-level `RequireAdmin` (Task 018) redirects non-elevated users to
 * `/403`; this module is the in-section companion: `useAdminGuard` decides
 * whether the Admin section may render its panels (`ForbiddenState` when
 * denied — never a redirect loop) and logs denials to telemetry without user
 * ids (route + code + opaque correlation id only). The client never caches
 * the elevated role claim: callers re-read permissions from the store on
 * every render, and the server re-authorizes every query (per-query 403
 * handling lives in `AdminPage`, which locks the section + toasts while the
 * session stays intact).
 */

/** True when the permission set unlocks the Admin section. Pure. */
export function canAccessAdmin(permissions: readonly string[]): boolean {
  return hasAdminPermission(permissions);
}

/**
 * Logs one guard denial to telemetry (no user ids, no tenant ids, no tokens).
 * Safe to call repeatedly; only the first call per denial instance emits.
 * Never throws — telemetry must not break rendering.
 */
export function logAdminGuardDenial(correlationId?: string): void {
  try {
    emitTelemetryEvent({
      type: 'api_failure',
      route: '/admin',
      code: 'FORBIDDEN',
      correlationId: correlationId ?? resolveTelemetryCorrelation(),
      status: 403,
    });
  } catch {
    // Telemetry is best-effort; denials still render.
  }
}

export interface AdminGuardSnapshot {
  /** True when the section may render its panels. */
  readonly allowed: boolean;
  /** True while the pre-shell gate must show the skeleton. */
  readonly isPending: boolean;
}

/**
 * Section guard hook. Re-reads the store permission set on every render (no
 * cached elevated claim) and emits one telemetry denial when blocked.
 */
export function useAdminGuard(): AdminGuardSnapshot {
  const status = useAppStore((s) => s.sessionStatus);
  const permissions = useAppStore((s) => s.permissions);
  const loggedRef = useRef(false);

  const isPending = status === 'loading' || status === 'unknown';
  const allowed = status === 'authenticated' && canAccessAdmin(permissions);

  useEffect(() => {
    if (!isPending && !allowed && !loggedRef.current) {
      loggedRef.current = true;
      logAdminGuardDenial();
    }
    if (allowed) {
      loggedRef.current = false;
    }
  }, [isPending, allowed]);

  return { allowed, isPending };
}
