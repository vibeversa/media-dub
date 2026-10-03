import { useCallback, useSyncExternalStore } from 'react';

/**
 * Viewport-width hook (GAP-023).
 *
 * `matchMedia` is the single mechanism: one listener per query instead of a
 * resize handler plus layout reads, and it stays SSR/jsdom safe. Environments
 * without `matchMedia` (older jsdom) report the wide layout so nothing is
 * hidden by a failed measurement — the CSS branches (`md:` utilities) still
 * apply.
 */
export function useMediaQuery(query: string): boolean {
  const subscribe = useCallback(
    (onChange: () => void) => {
      if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
        return () => undefined;
      }

      const list = window.matchMedia(query);
      // Safari < 14 only has addListener/removeListener.
      if (typeof list.addEventListener === 'function') {
        list.addEventListener('change', onChange);
        return () => {
          list.removeEventListener('change', onChange);
        };
      }

      list.addListener(onChange);
      return () => {
        list.removeListener(onChange);
      };
    },
    [query],
  );

  const getSnapshot = useCallback(() => {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
      return false;
    }

    return window.matchMedia(query).matches;
  }, [query]);

  // Server snapshot: the wide layout, matching getSnapshot's fallback.
  return useSyncExternalStore(subscribe, getSnapshot, () => false);
}