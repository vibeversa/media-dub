import type { ReactNode } from 'react';

/** Suspense fallback rendered while a lazy route chunk loads. */
export function RouteFallback(): ReactNode {
  return (
    <p role="status" aria-live="polite" data-testid="route-fallback" className="py-8 text-center">
      Loading…
    </p>
  );
}
