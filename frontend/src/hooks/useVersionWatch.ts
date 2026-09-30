import { useCallback, useEffect, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { checkServedVersion, getDeployConfig } from '../config/env.js';
import type { VersionMatch } from '../config/env.js';
import { trackVersionMismatch } from '../telemetry/telemetry.js';

// Version skew detection (Task 043, edge case: "CDN serves stale HTML after
// deploy -> version-mismatch banner prompts reload").
//
// THE FAILURE THIS EXISTS FOR
// --------------------------
// The document (`index.html`) is served with `no-cache` and the hashed assets
// under `/assets/` with `immutable, max-age=31536000`. That pairing is correct
// and it has one consequence: a user holding an already-loaded tab keeps
// executing the bundle from release N while the assets it lazily imports by
// name now resolve to release N+1's bytes, because the names in the *new*
// document are the only ones on the origin. The symptom is a chunk-load failure
// on the first navigation, which `ChunkErrorBoundary` catches and explains as
// "the app was likely updated" - correct, but reactive, and it loses the user's
// place.
//
// Detecting it before it happens needs a comparison: the tag baked into the
// running bundle against the tag the CDN is currently serving. When they differ,
// the user is already on the old bundle, so the only correct action is a reload.
//
// WHY ON FOCUS, NOT ON A TIMER
// -----------------------------
// A polling interval would report a skew a user cannot act on - they are not
// looking - and would keep the check running on every tab in the fleet. The
// `focus` event is exactly the moment the answer becomes actionable: the user
// has come back to a tab, and a reload is a keystroke they are about to
// understand. The check also runs once on mount, which is the case of a user
// who has just loaded the page and been redirected by an in-app navigation.

export interface VersionWatchState {
  /** `MISMATCH` means the CDN is serving a different release than this bundle. */
  readonly match: VersionMatch;
  /** The tag the running bundle was built with, for the banner's wording. */
  readonly bakedTag: string;
  /** The tag the CDN reports, when it answered. */
  readonly servedTag: string | null;
  /** Reloads the page, bypassing the SPA router. */
  readonly reload: () => void;
}

export interface UseVersionWatchOptions {
  /**
   * Injected for tests. Defaults to the global `fetch`.
   */
  readonly fetchImpl?: typeof fetch;
  /**
   * `true` skips the check entirely. Used by visual/a11y suites, where a
   * reload mid-screenshot is a flake rather than a fix.
   */
  readonly enabled?: boolean;
  /**
   * Focus-poll interval. Exposed only so a test can set it to something other
   * than a minute; production always uses the default.
   */
  readonly focusIntervalMs?: number;
}

/** How often the check re-runs after a focus event. */
export const VERSION_CHECK_FOCUS_INTERVAL_MS = 60_000;

export function useVersionWatch(options: UseVersionWatchOptions = {}): VersionWatchState {
  const { fetchImpl, enabled = true, focusIntervalMs = VERSION_CHECK_FOCUS_INTERVAL_MS } = options;
  const queryClient = useQueryClient();
  const { versionTag } = getDeployConfig();

  const [state, setState] = useState<{ match: VersionMatch; servedTag: string | null }>({
    match: 'UNKNOWN',
    servedTag: null,
  });

  // The interval is reset on every focus, so a user who tabs away and comes back
  // is checked immediately rather than waiting out whatever remained of the
  // previous window. That is a ref rather than an effect dependency because
  // re-running the effect on a `Date.now()` value would restart the timer on
  // every render.
  const lastCheckedAt = useRef(0);
  const reportedRef = useRef(false);
  const cacheClearedRef = useRef(false);

  const check = useCallback(async () => {
    // Stamped on EVERY check, including the mount check, not only the
    // focus-driven one. Without this the mount check leaves the window wide open
    // and the first focus event always re-checks — so the interval silently
    // behaves as zero for exactly the tab-switch case it exists to rate-limit.
    lastCheckedAt.current = Date.now();
    try {
      const { match, served } = await checkServedVersion(fetchImpl ?? globalThis.fetch, versionTag);
      setState({ match, servedTag: served?.release ?? null });

      // Telemetry records the FIRST skew detection per page load and then goes
      // quiet. A skew that re-reports on every focus is one number with a
      // hundred samples attached, which makes the ratio unreadable and the
      // network call free.
      if (match === 'MISMATCH' && !reportedRef.current) {
        reportedRef.current = true;
        trackVersionMismatch({ baked: versionTag, served: served?.release ?? 'unknown' });
      }
    } catch {
      setState({ match: 'UNKNOWN', servedTag: null });
    }
  }, [fetchImpl, versionTag]);

  useEffect(() => {
    if (!enabled) {
      return;
    }

    void check();

    const onFocus = (): void => {
      if (Date.now() - lastCheckedAt.current < focusIntervalMs) {
        return;
      }
      void check();
    };

    window.addEventListener('focus', onFocus);
    return () => {
      window.removeEventListener('focus', onFocus);
    };
  }, [check, enabled, focusIntervalMs]);

  // A mismatched bundle may hold cached query data in a shape the newer server
  // no longer returns. Clearing on a DETECTED mismatch - once, not on every
  // focus - is what makes the reload safe rather than merely prompt: without
  // it, the reloaded client re-hydrates from a cache holding the old release's
  // responses and the user sees the same wrong data after a "successful"
  // reload.
  useEffect(() => {
    if (state.match === 'MISMATCH' && !cacheClearedRef.current) {
      cacheClearedRef.current = true;
      queryClient.clear();
    }
  }, [queryClient, state.match]);

  const reload = useCallback(() => {
    // `location.reload()` rather than a router navigation: a client-side
    // navigation would keep executing the same old bundle, which is the entire
    // problem. The reload is also the only way to drop the assets the old
    // document referenced, since they may already have been replaced.
    window.location.reload();
  }, []);

  return { match: state.match, bakedTag: versionTag, servedTag: state.servedTag, reload };
}
