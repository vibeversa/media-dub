import { useEffect, useRef } from 'react';
import type { ReactNode } from 'react';
import { useLocation } from 'react-router-dom';
import { trackPageView, trackRouteChange } from './telemetry.js';

/**
 * Mounted inside the authenticated shell: records a page view on first
 * render and a route change on every subsequent navigation. Path-only
 * payloads (query stripped); no-op while telemetry is disabled.
 */
export function RouteTracker(): ReactNode {
  const location = useLocation();
  const previous = useRef<string | null>(null);

  useEffect(() => {
    const to = `${location.pathname}`;
    if (previous.current === null) {
      trackPageView(to);
    } else if (previous.current !== to) {
      trackRouteChange(previous.current, to);
      trackPageView(to);
    }
    previous.current = to;
  }, [location.pathname]);

  return null;
}
