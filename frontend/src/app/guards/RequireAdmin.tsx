import type { ReactNode } from 'react';
import { Navigate, Outlet } from 'react-router-dom';
import { hasAdminPermission } from '../session/permissions.js';
import { useAppStore } from '../../stores/index.js';
import { SessionSkeleton } from './SessionSkeleton.js';

/**
 * Admin guard (Task 018, R1): permission-gated, redirects to `/403` without
 * the admin permission. Fail-closed on permissions load failure (empty set =
 * no Admin). UX defense-in-depth only — the server re-authorizes per
 * Tasks 013/036.
 */
export function RequireAdmin(): ReactNode {
  const status = useAppStore((s) => s.sessionStatus);
  const permissions = useAppStore((s) => s.permissions);
  if (status === 'loading') {
    return <SessionSkeleton />;
  }
  if (status !== 'authenticated' || !hasAdminPermission(permissions)) {
    return <Navigate to="/403" replace />;
  }
  return <Outlet />;
}
