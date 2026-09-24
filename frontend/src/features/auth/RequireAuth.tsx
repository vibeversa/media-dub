import type { ReactNode } from 'react';
import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { SessionSkeleton } from '../../app/guards/SessionSkeleton.js';
import { useAppStore } from '../../stores/index.js';
import { buildLoginPath } from './destination.js';

/**
 * Canonical authentication guard (Task 019).
 *
 * Blocks the shell until the pre-shell `/me` resolution settles
 * (`loading`/`unknown` → skeleton, R5: shell never renders for
 * unauthenticated users). Anonymous, expired, and error sessions redirect to
 * `/login?next=<destination>` so post-login navigation restores the original
 * destination (R2), including expired deep links.
 */
export function RequireAuth(): ReactNode {
  const status = useAppStore((s) => s.sessionStatus);
  const location = useLocation();
  if (status === 'loading' || status === 'unknown') {
    return <SessionSkeleton />;
  }
  if (status !== 'authenticated') {
    const destination = `${location.pathname}${location.search}`;
    return <Navigate to={buildLoginPath(destination)} replace />;
  }
  return <Outlet />;
}
