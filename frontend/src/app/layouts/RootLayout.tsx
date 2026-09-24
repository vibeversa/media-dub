import { Suspense } from 'react';
import type { ReactNode } from 'react';
import { Link, Outlet } from 'react-router-dom';
import { ChunkErrorBoundary } from '../ChunkErrorBoundary.js';
import { RouteFallback } from '../RouteFallback.js';

/** Authenticated shell: nav, suspense boundary, chunk-reload boundary, outlet. */
export function RootLayout(): ReactNode {
  return (
    <div className="min-h-screen bg-white text-slate-900">
      <header className="border-b border-slate-200">
        <nav aria-label="Primary" className="mx-auto flex max-w-6xl items-center gap-4 px-4 py-3">
          <Link to="/" className="font-semibold">
            Dubbing Platform
          </Link>
          <Link to="/dashboard">Dashboard</Link>
          <Link to="/projects">Projects</Link>
          <Link to="/review">Review</Link>
          <Link to="/notifications">Notifications</Link>
          <Link to="/settings">Settings</Link>
          <Link to="/admin">Admin</Link>
        </nav>
      </header>
      <main className="mx-auto max-w-6xl px-4 py-6">
        <ChunkErrorBoundary>
          <Suspense fallback={<RouteFallback />}>
            <Outlet />
          </Suspense>
        </ChunkErrorBoundary>
      </main>
    </div>
  );
}
