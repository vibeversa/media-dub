import { createContext, createElement, useContext, useMemo } from 'react';
import type { ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { UseQueryResult } from '@tanstack/react-query';
import type { AppError } from '../api/errors/index.js';
import { queryKeys } from '../api/queryKeys/index.js';
import {
  EMPTY_ME_FEATURE_FLAG_SLICE,
  evaluateFeatureFlags,
  readMeFeatureFlagSlice,
} from '../config/featureFlags.js';
import type {
  FeatureFlagKey,
  FeatureFlagValue,
  MeFeatureFlagSlice,
} from '../config/featureFlags.js';
import { fetchMeDocument } from '../features/auth/api.js';
import { useIsAuthenticated } from '../features/auth/useSession.js';

/**
 * The one product-wide feature-flag hook (Task 048, R1).
 *
 * THE RULE THIS FILE EXISTS TO ENFORCE
 * ------------------------------------
 * `useFeatureFlag(key)` is the ONLY way any surface learns whether a flag is
 * on. Not a convention - `src/hooks/useFeatureFlag.gate.test.tsx` fails the
 * suite if another module names a `/me` flag wire key, reads a `VITE_ENABLE_*`
 * variable, or issues its own `/me` read. Before this task the enrichment gate
 * and the admin panels each had their own private read, which meant "is
 * enrichment on?" had two possible answers depending on which module you asked.
 *
 * THE SOURCES, AND WHY THERE ARE ONLY TWO (R4)
 * -------------------------------------------
 * `GET /me`'s `featureFlags` slice (Task 006), then build-time bootstrap config
 * (`config/env.ts`'s `VITE_ENABLE_*` family), then the fail-closed default. The
 * full precedence sentence and the three-state (stated-true / stated-false /
 * unstated) rule are in `config/featureFlags.ts`; nothing here re-decides any
 * of it.
 *
 * WHY A REACT-QUERY ENTRY AND NOT A SECOND FETCH
 * ----------------------------------------------
 * `queryKeys.me.featureFlags()` is one cache entry for the whole session, so N
 * mounted surfaces produce ONE `/me` read - react-query deduplicates by key -
 * and `staleTime` means a surface mounted later in the session does not cause
 * another round trip for what is rollout configuration that only moves on a
 * deploy.
 *
 * `authStore`'s session resolution seeds the same entry with what it already
 * read (Task 019 reads `/me` once for permissions and locale), so in the running
 * application this module's `queryFn` is a *fallback* rather than the way flags
 * arrive: the cache is already populated and fresh by the time a surface can
 * mount, because a surface only renders after authentication, and
 * authentication is established FROM that read. Without the seed the app would
 * pay a second `/me` round trip per session for data it had already thrown
 * away. The fallback still matters: a suite (or an e2e) can mark a session
 * authenticated without running the login path, and then this is the only way
 * the flags arrive.
 *
 * FAIL-CLOSED, WITHOUT AN EXCEPTION (R2)
 * --------------------------------------
 * Pending, 401, 403, 404, 500, a network failure, a timeout, an unreadable
 * document, an unknown flag key, a flag value that is not a boolean, and a
 * session that is not authenticated ALL resolve to `false`. There is no
 * optimistic-on path and no "assume the rollout is on" default.
 *
 * A WORKING FLAG SURVIVES A FAILED REFRESH
 * -----------------------------------------
 * The flag-off default is used for "not read yet" and for "could not be read";
 * it is not sticky. A transient failure after a successful read leaves the
 * resolved slice in place (react-query keeps `data` on a failed refetch), so a
 * blip never makes a working surface disappear mid-session. Clearing it is
 * `queryClient.clear()`, which the logout path already does before it flips
 * the session.
 *
 * A FLAG NEVER GRANTS ANYTHING (R3)
 * ---------------------------------
 * Note what this module does not import: no permission helper, no role, no
 * guard. `useFeatureFlag` cannot answer "may this user do this", and the
 * negative test in `__tests__/useFeatureFlag.test.tsx` pins that by running a
 * flag-ON session past the real admin guard with a non-elevated permission list
 * and asserting the denial still happens. Every gated action still passes
 * through `/me`'s permission strings (Task 006), `RequireAdmin` (Task 018) and
 * the server's own authorization.
 *
 * TELEMETRY
 * ---------
 * Nothing here emits an event. Flag evaluation is not a failure, a route change
 * or a version skew, and a rollout hint has no business in a telemetry stream:
 * the task's security section allows a flag's key and boolean and nothing else,
 * and emitting nothing is the only way to guarantee that no future refactor
 * widens it.
 */

export type FeatureFlagSnapshotSource = 'PROVIDER' | 'ME' | 'UNRESOLVED';

export interface FeatureFlagSnapshot {
  /** Every key resolved; `false` wherever nothing was stated. */
  readonly flags: FeatureFlagValue;
  /** True once a read has settled - successfully or not. A session with no
   *  authenticated read never settles, because there is nothing to wait for. */
  readonly resolved: boolean;
  /** True when the read could not be completed. */
  readonly failed: boolean;
  /** Where the values came from, so a caller can explain rather than guess. */
  readonly source: FeatureFlagSnapshotSource;
}

/**
 * How long a resolved flag slice stays fresh.
 *
 * Rollout configuration moves on a deploy, not on a scroll, so a short value
 * would spend a `/me` read per session for a value that cannot have changed.
 * It is also the value `authStore`'s seed relies on: the seed is written with
 * `setQueryData`, which marks the entry fresh from that moment.
 */
export const FEATURE_FLAG_STALE_TIME_MS = 5 * 60_000;

/**
 * The `/me`-backed flag query, parameterised by whether it should run.
 *
 * `retry: false` because a retry cannot change an authorization-shaped answer,
 * and `refetchOnWindowFocus`/`refetchOnReconnect` are off for the same reason
 * the enrichment query turned them off: a flag that flickers is worse than a
 * flag that is five minutes stale.
 */
function useFeatureFlagsQueryFor(enabled: boolean): UseQueryResult<MeFeatureFlagSlice, AppError> {
  return useQuery<MeFeatureFlagSlice, AppError>({
    queryKey: queryKeys.me.featureFlags(),
    queryFn: async ({ signal }): Promise<MeFeatureFlagSlice> =>
      readMeFeatureFlagSlice(await fetchMeDocument(signal)),
    enabled,
    staleTime: FEATURE_FLAG_STALE_TIME_MS,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    retry: false,
  });
}

/**
 * The session's flag read, for a caller that wants it rather than a value.
 *
 * Fires only for an authenticated session. A subtree under `FeatureFlagProvider`
 * does not use this; the provider's whole purpose is to be the answer, so
 * asking the network as well would make a hermetic test non-hermetic for no
 * benefit.
 */
export function useFeatureFlagsQuery(): UseQueryResult<MeFeatureFlagSlice, AppError> {
  return useFeatureFlagsQueryFor(useIsAuthenticated());
}

/**
 * The inputs every consumer of this module shares: an explicit override, the
 * trustworthy `/me` slice, and the query itself for its settled/failed state.
 *
 * `slice` is `undefined` whenever there is nothing trustworthy to resolve from:
 * before the first read, after a failure with no earlier answer, and whenever
 * the session is not authenticated. That last case is the one the task's edge
 * cases name - a `/me` load failure leaves a non-admin shell, and a shell with
 * no session has no flags.
 */
function useFeatureFlagInputs(): {
  readonly override: FeatureFlagValue | undefined;
  readonly slice: MeFeatureFlagSlice | undefined;
  readonly query: UseQueryResult<MeFeatureFlagSlice, AppError>;
} {
  const override = useContext(FeatureFlagContext);
  const authenticated = useIsAuthenticated();
  const query = useFeatureFlagsQueryFor(authenticated && override === undefined);
  const slice = authenticated ? query.data : undefined;
  return { override, slice, query };
}

/** The resolved flags plus whether the read has settled. */
export function useFeatureFlagSnapshot(): FeatureFlagSnapshot {
  const { override, slice, query } = useFeatureFlagInputs();

  if (override !== undefined) {
    return { flags: override, resolved: true, failed: false, source: 'PROVIDER' };
  }
  if (slice === undefined) {
    return {
      flags: evaluateFeatureFlags(EMPTY_ME_FEATURE_FLAG_SLICE),
      resolved: query.isError,
      failed: query.isError,
      source: 'UNRESOLVED',
    };
  }
  return { flags: evaluateFeatureFlags(slice), resolved: true, failed: false, source: 'ME' };
}

/** Every flag, resolved. The map every `useFeatureFlag` call reads from. */
export function useFeatureFlags(): FeatureFlagValue {
  const { override, slice } = useFeatureFlagInputs();
  return useMemo(() => override ?? evaluateFeatureFlags(slice), [override, slice]);
}

/**
 * Whether one flag is on. Fail-closed; never throws; never reads a permission.
 *
 * `true` only when `/me` stated `true` under the flag's exact wire key, or when
 * `/me` said nothing about it and bootstrap config said `true`. A flag key this
 * build has never heard of, a slice that is not an object, and a session that is
 * not authenticated are all `false`.
 */
export function useFeatureFlag(key: FeatureFlagKey): boolean {
  return useFeatureFlags()[key];
}

const FeatureFlagContext = createContext<FeatureFlagValue | undefined>(undefined);

export interface FeatureFlagProviderProps {
  /**
   * The resolved flags for this subtree. A test/e2e seam, not a runtime
   * override: it replaces the resolved map, so a value passed here is exactly
   * what `useFeatureFlag` reports, and the `/me` read is not consulted.
   *
   * Explicit provider rather than a module-level setter, so an override cannot
   * leak between tests the way a mutable singleton can, and so production code
   * has exactly one flag source.
   */
  readonly value: FeatureFlagValue;
  readonly children: ReactNode;
}

/**
 * Test/e2e seam for the flag map.
 *
 * Mounting this bypasses the session check the hook applies to its own read,
 * which is what a unit suite wants and what no runtime path does; the shipped
 * app never mounts it.
 */
export function FeatureFlagProvider({ value, children }: FeatureFlagProviderProps): ReactNode {
  return createElement(FeatureFlagContext.Provider, { value }, children);
}