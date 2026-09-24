import { Suspense } from 'react';
import type { ReactNode } from 'react';
import { Outlet } from 'react-router-dom';
import { RouteFallback } from '../RouteFallback.js';

/** Minimal unauthenticated shell for the login route. */
export function AuthLayout(): ReactNode {
  return (
    <div className="flex min-h-screen items-center justify-center bg-slate-50">
      <div className="w-full max-w-md rounded-lg border border-slate-200 bg-white p-6 shadow-sm">
        <Suspense fallback={<RouteFallback />}>
          <Outlet />
        </Suspense>
      </div>
    </div>
  );
}
