import type { ReactNode } from 'react';
import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { useAppStore } from '../../stores/index.js';
import { SessionSkeleton } from './SessionSkeleton.js';

/**
 * Authentication guard (Task 018): waits for the pre-shell /me resolution
 * Task 019 owns (shell skeleton meanwhile), redirects anonymous users to
 * `/login` with the return path, and renders the outlet once authenticated.
 */
export function RequireAuth(): ReactNode {
  const status = useAppStore((s) => s.sessionStatus);
  const location = useLocation();
  if (status === 'loading') {
    return <SessionSkeleton />;
  }
  if (status === 'anonymous') {
    return <Navigate to="/login" replace state={{ from: `${location.pathname}` }} />;
  }
  return <Outlet />;
}
