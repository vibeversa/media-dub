import { useEffect } from 'react';
import { useAuthStore } from './authStore.js';
import type { AuthStatus } from './authStore.js';

let restoreStarted = false;

/** Idempotent boot hook: the first caller starts the pre-shell restore. */
export function ensureRestoreStarted(): void {
  if (restoreStarted) {
    return;
  }
  restoreStarted = true;
  void useAuthStore.getState().restore();
}

/** Test-only reset for the boot guard. */
export function resetRestoreStartedForTests(): void {
  restoreStarted = false;
}

/** True only when feature queries may fire (`enabled: isAuthenticated`). */
export function isAuthenticatedStatus(status: AuthStatus): boolean {
  return status === 'authenticated';
}

export interface SessionSnapshot {
  readonly status: AuthStatus;
  readonly userId: string | undefined;
  readonly tenantId: string | undefined;
  readonly permissions: readonly string[];
  readonly isAuthenticated: boolean;
  /** True while the pre-shell gate must show the skeleton. */
  readonly isPending: boolean;
  readonly isExpired: boolean;
  readonly loginPending: boolean;
  readonly lastErrorCode: string | undefined;
}

/**
 * Session hook (Task 019). Ensures the pre-shell `/me` resolution starts on
 * first use and exposes the gate flags. No feature query may fire before
 * `isAuthenticated` is true — pass it as the query `enabled` flag.
 */
export function useSession(): SessionSnapshot {
  const status = useAuthStore((s) => s.status);
  const userId = useAuthStore((s) => s.userId);
  const tenantId = useAuthStore((s) => s.tenantId);
  const permissions = useAuthStore((s) => s.permissions);
  const loginPending = useAuthStore((s) => s.loginPending);
  const lastErrorCode = useAuthStore((s) => s.lastErrorCode);

  useEffect(() => {
    ensureRestoreStarted();
  }, []);

  return {
    status,
    userId,
    tenantId,
    permissions,
    isAuthenticated: isAuthenticatedStatus(status),
    isPending: status === 'unknown',
    isExpired: status === 'expired',
    loginPending,
    lastErrorCode,
  };
}

/** Query-gate helper: `useQuery({ ..., enabled: useIsAuthenticated() })`. */
export function useIsAuthenticated(): boolean {
  return useAuthStore((s) => isAuthenticatedStatus(s.status));
}
