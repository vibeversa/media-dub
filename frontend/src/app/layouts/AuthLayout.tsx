import { Suspense } from 'react';
import type { ReactNode } from 'react';
import { Outlet } from 'react-router-dom';
import { RouteFallback } from '../RouteFallback.js';

/**
 * Minimal unauthenticated shell for the login route.
 *
 * The card is a `<main>` landmark, not a `<div>`. Task 041C's axe scan found
 * `landmark-one-main` and `region` here: a document with no `main` landmark is
 * one a screen-reader user cannot jump to, and the sign-in heading and both
 * fields sat outside every landmark, so "skip to content" had nowhere to go.
 * The authenticated shell has had a `<main>` since Task 018; the unauthenticated
 * one is the only document in the app that lacked it.
 */
export function AuthLayout(): ReactNode {
  return (
    // Tokens, not a hardcoded Tailwind palette. `bg-slate-50`/`bg-white` were
    // fixed light-mode colours, so in dark mode the card stayed white while
    // `--color-text` became near-white - and every label, the `h1` and the
    // inputs all dropped below 4.5:1. axe's `color-contrast` rule caught it on
    // the dark scan only, which is exactly why the audit runs both themes.
    <div className="dp-surface flex min-h-screen items-center justify-center">
      <main className="w-full max-w-md rounded-lg border border-[var(--color-border)] bg-[var(--color-surface-overlay)] p-6 shadow-sm">
        <Suspense fallback={<RouteFallback />}>
          <Outlet />
        </Suspense>
      </main>
    </div>
  );
}
