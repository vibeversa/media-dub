import { createContext, createElement, useContext } from 'react';
import type { ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { UseQueryResult } from '@tanstack/react-query';
import { apiFetch } from '../../api/client/index.js';
import type { AppError } from '../../api/errors/index.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { useIsAuthenticated } from '../auth/useSession.js';

/**
 * Optional-enrichment flag resolution (Task 044, Plan B §19.1–§19.3).
 *
 * WHY THIS FILE IS SEPARATE FROM THE PANELS
 * -----------------------------------------
 * This module and `EnrichmentGate.tsx` are the ONLY enrichment files a core
 * bundle may contain, and the import gate in
 * `__tests__/enrichment.test.tsx` proves it by resolving the app's real chunk
 * graph (the route modules `src/app/pages/lazy.ts` names, and each one's static
 * closure) and failing if an enrichment payload module appears in one.
 *
 * Everything else — the panels, the query hooks, the payload parsers, the
 * enrichment state components — is reachable only through a dynamic
 * `import()`, so a build with the flags off never ships it and a browser with
 * the flags off never requests it.
 *
 * The line is deliberate: **the flag check ships, the payload does not.** A
 * gate that had to be fetched before it could decide anything would defeat the
 * purpose, and a gate that shipped the panels would defeat the bundle
 * exclusion. This file is the smallest thing that satisfies both.
 *
 * WHERE THE FLAGS COME FROM
 * -------------------------
 * `GET /me` is the only server→client flag channel an *ordinary* user can
 * read. The admin flag list (`GET /admin/feature-flags`, surfaced by the Task
 * 036 `FlagsPanel`) is tenant-admin gated and 403s for everyone else, so it
 * cannot gate a workspace panel. `/me` carries `MeFeatureFlags`
 * (`videoIntelligenceEnabled` / `lipSyncEnabled` / `localInferenceEnabled`),
 * which is exactly the three capabilities in §19.1–§19.3.
 *
 * The committed OpenAPI bundle's `MeResponse` does not declare `featureFlags`,
 * so this module reads the raw `/me` document through `apiFetch` and parses it
 * defensively rather than through the generated type. That is also the honest
 * behaviour against an older backend: the field is absent, the parse yields
 * all-off, and the UI shows nothing.
 *
 * FAIL-CLOSED, WITHOUT EXCEPTIONS
 * -------------------------------
 * Pending, 401, 403, 404, 500, a network failure, a timeout, or a shape this
 * parser does not recognise all resolve to **all three flags off**. There is
 * no "optimistic on" path and no "assume enabled" default. These flags gate
 * presentation of media-derived artifacts and an operator device summary; the
 * failure mode of guessing wrong in one direction is a user seeing a panel that
 * cannot work, and in the other is shipping enrichment code and device detail
 * to users who must not have them.
 *
 * NOTE ON TASK 048
 * ----------------
 * `useFeatureFlag` (`src/hooks/useFeatureFlag.ts`) generalises this into a
 * product-wide hook. It is deliberately NOT used here: this file must stay the
 * single enrichment gate regardless of which mechanism reads the flag, so that
 * `EnrichmentGate` has exactly one place to change when 048 lands.
 */

/** The three enrichment capabilities, in the task's own vocabulary. */
export const ENRICHMENT_FLAG_NAMES = ['videoIntel', 'lipSync', 'localGpu'] as const;

export type EnrichmentFlagName = (typeof ENRICHMENT_FLAG_NAMES)[number];

export interface EnrichmentFlags {
  readonly videoIntel: boolean;
  readonly lipSync: boolean;
  readonly localGpu: boolean;
}

/**
 * Every flag off. The single value used for pending, error, unknown shape and
 * "not read yet" alike, so there is no path by which a partially-resolved
 * snapshot can enable something.
 */
export const ENRICHMENT_FLAGS_OFF: EnrichmentFlags = Object.freeze({
  videoIntel: false,
  lipSync: false,
  localGpu: false,
});

/**
 * Wire keys on the `/me` `featureFlags` slice, in the same order as
 * `ENRICHMENT_FLAG_NAMES`.
 *
 * These are the EXACT `MeFeatureFlags` record fields
 * (`src/DubbingPlatform.Api/Models/AuthMeDtos.cs`) plus nothing else. An
 * earlier draft also accepted the client-side aliases (`videoIntel`,
 * `localGpu`), and that was a mistake: `featureFlags: { videoIntel: true }`
 * then read as "on" for a key no backend has ever emitted, so a payload shaped
 * by something other than the platform could switch a surface on. Accepting a
 * name the server does not send is how a fail-closed parser stops being
 * fail-closed.
 */
const WIRE_FLAG_KEYS: Readonly<Record<EnrichmentFlagName, readonly string[]>> = Object.freeze({
  videoIntel: ['videoIntelligenceEnabled'],
  lipSync: ['lipSyncEnabled'],
  localGpu: ['localInferenceEnabled'],
});

function toRecord(value: unknown): Record<string, unknown> | undefined {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : undefined;
}

/**
 * True only for the literal `true`. A string `"true"`, a number `1` and a
 * truthy object all read as OFF: the backend emits real booleans, so anything
 * else is a shape this parser does not understand, and the rule for that is
 * the same as for every other unknown — fail closed. Pure.
 */
function readEnabled(value: unknown): boolean {
  return value === true;
}

/**
 * Resolves the enrichment flags from a raw `GET /me` document. Pure.
 *
 * Accepts either the top-level document (`{ featureFlags: {...} }`) or the
 * already-unwrapped slice, because `apiFetch` hands back the whole body and a
 * caller holding a slice should not have to re-wrap it to test this. Any
 * missing, malformed or unrecognised input resolves every flag to false.
 */
export function parseEnrichmentFlags(raw: unknown): EnrichmentFlags {
  const document = toRecord(raw);
  if (document === undefined) {
    return ENRICHMENT_FLAGS_OFF;
  }
  // A caller may pass the slice itself; only descend when the document still
  // carries a container, so an unwrapped slice is read in place.
  const slice = toRecord(document['featureFlags']) ?? document;

  let resolved: EnrichmentFlags = ENRICHMENT_FLAGS_OFF;
  for (const name of ENRICHMENT_FLAG_NAMES) {
    for (const wireKey of WIRE_FLAG_KEYS[name]) {
      if (wireKey in slice) {
        const enabled = readEnabled(slice[wireKey]);
        resolved = { ...resolved, [name]: enabled };
        // First recognised spelling wins; later aliases never override a value
        // that was actually present.
        break;
      }
    }
  }
  return resolved;
}

/** Whether one enrichment capability is on. Pure. */
export function isEnrichmentFlagEnabled(flags: EnrichmentFlags, name: EnrichmentFlagName): boolean {
  return flags[name] === true;
}

/**
 * R1's decision, as a pure function: `true` renders the child, `false` renders
 * `null`.
 *
 * Exported separately from `EnrichmentGate` because `react-refresh` requires a
 * component file to export only components, and because the rule is worth
 * asserting directly instead of inferring it from rendered output.
 */
export function enrichmentGateAllows(flags: EnrichmentFlags, name: EnrichmentFlagName): boolean {
  return isEnrichmentFlagEnabled(flags, name);
}

export interface EnrichmentFlagSnapshot {
  readonly flags: EnrichmentFlags;
  /** True once a `/me` read has settled — successfully or not. */
  readonly resolved: boolean;
  /** True when `/me` could not be read; the flags are the fail-closed default. */
  readonly failed: boolean;
}

/** The snapshot used before `/me` settles and after it fails. */
const UNRESOLVED_SNAPSHOT: EnrichmentFlagSnapshot = Object.freeze({
  flags: ENRICHMENT_FLAGS_OFF,
  resolved: false,
  failed: false,
});

const FAILED_SNAPSHOT: EnrichmentFlagSnapshot = Object.freeze({
  flags: ENRICHMENT_FLAGS_OFF,
  resolved: true,
  failed: true,
});

/**
 * Reads the flag slice from the session. One query per session (see
 * `queryKeys.enrichment.flags`), `retry: false` because a retry cannot change
 * an authorization-shaped answer, and a long `staleTime` because the values
 * are rollout configuration that only moves on a deploy.
 *
 * Never throws and never throws *away* a resolution: an earlier successful
 * snapshot survives a later refetch failure, because a transient blip must not
 * make a working panel disappear.
 */
export function useEnrichmentFlagsQuery(): UseQueryResult<EnrichmentFlags, AppError> {
  const enabled = useIsAuthenticated();
  return useQuery<EnrichmentFlags, AppError>({
    queryKey: queryKeys.enrichment.flags(),
    queryFn: async ({ signal }): Promise<EnrichmentFlags> => {
      const raw = await apiFetch<unknown>('/me', signal !== undefined ? { signal } : undefined);
      return parseEnrichmentFlags(raw);
    },
    enabled,
    staleTime: 5 * 60_000,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    retry: false,
  });
}

/**
 * The resolved flags plus whether the read has settled. This is the shape the
 * gate and every test consume, so "off" is never ambiguous between "not yet
 * known" and "known to be off" at the call site.
 */
export function useEnrichmentFlagSnapshot(): EnrichmentFlagSnapshot {
  const query = useEnrichmentFlagsQuery();
  if (query.data === undefined) {
    return query.isError ? FAILED_SNAPSHOT : UNRESOLVED_SNAPSHOT;
  }
  return { flags: query.data, resolved: true, failed: false };
}

const EnrichmentFlagsContext = createContext<EnrichmentFlags | undefined>(undefined);

export interface EnrichmentFlagsProviderProps {
  readonly value: EnrichmentFlags;
  readonly children: ReactNode;
}

/**
 * Test/e2e seam for the flag snapshot.
 *
 * The e2e specs need to drive flags from a `page.route` mock, and the unit
 * suites need both halves of the matrix, without every one of them having to
 * reproduce a `/me` document. This is an explicit provider — never a module
 * level setter — so an override cannot leak between tests the way a mutable
 * singleton can, and so production code has exactly one flag source.
 */
export function EnrichmentFlagsProvider({ value, children }: EnrichmentFlagsProviderProps): ReactNode {
  return createElement(EnrichmentFlagsContext.Provider, { value }, children);
}

/**
 * The flags for this subtree: the provider's when one is mounted, otherwise
 * the `/me`-backed query. Pure enough to reason about; never throws.
 */
export function useEnrichmentFlags(): EnrichmentFlags {
  const override = useContext(EnrichmentFlagsContext);
  const snapshot = useEnrichmentFlagSnapshot();
  return override ?? snapshot.flags;
}