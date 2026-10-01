import { useQuery } from '@tanstack/react-query';
import type { UseQueryResult } from '@tanstack/react-query';
import { apiFetch } from '../../api/client/index.js';
import { normalizeError } from '../../api/errors/index.js';
import type { AppError } from '../../api/errors/index.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { fetchSignedDownloadUrl } from '../../api/signedDownload/index.js';
import type { SignedDownloadUrl } from '../../api/signedDownload/index.js';
import { useIsAuthenticated } from '../auth/useSession.js';
import { parseLipSync, parseVideoIntel } from './types.js';
import type { LipSyncView, VideoIntelView } from './types.js';

/**
 * Optional-enrichment reads (Task 044).
 *
 * These endpoints are FRONTEND-ANTICIPATED. The enrichment workers
 * (`VideoIntelligenceWorker`, `LipSyncWorker`) exist and are dual-gated, but
 * no read endpoint is provisioned yet, so every call here degrades rather than
 * fails — the same convention the Task 036 admin panels use for
 * `/admin/tenants` and `/admin/feature-flags`. Three outcomes are
 * distinguished, because they mean different things to the user:
 *
 * | Backend answer | Meaning | Panel state |
 * | --- | --- | --- |
 * | 404 `NOT_FOUND` | route not provisioned (also the `ADMIN_ROUTE_UNKNOWN` marker) | `NotAvailableState` — "operator feature in setup" |
 * | 501 `NOT_IMPLEMENTED` | provisioned but the capability is off in this deployment | `NotAvailableState` — same copy, same reason to a user |
 * | 5xx / network / timeout | the capability broke | dismissible `UnavailableState`; core untouched |
 * | 403 | the caller lacks the grant | `ForbiddenState`; no detail rendered |
 *
 * A missing route is a *deployment fact*, not an error, and rendering it as an
 * error would put a red box on every screen of an installation that simply has
 * not turned the feature on yet.
 *
 * All three hooks pass `enabled` from the caller so a query can never fire
 * before the flag has been resolved ON. That is not just an optimisation: R1
 * requires that with the flags off nothing about enrichment is requested at
 * all, so the flag check has to be upstream of the query, not a filter on its
 * results.
 */

/** True for "the route or capability is not provisioned here". */
export function isNotProvisionedError(error: unknown): boolean {
  const status = (error as { status?: unknown } | undefined)?.status;
  return status === 404 || status === 501;
}

/** True for an elevated-gating denial. */
export function isEnrichmentForbiddenError(error: unknown): boolean {
  const shape = error as { status?: unknown; code?: unknown } | undefined;
  return shape?.status === 403 || shape?.code === 'FORBIDDEN';
}

function normalizeGet(error: unknown): AppError {
  return normalizeError(error, { method: 'GET' });
}

/**
 * Video-intel artifacts for one project.
 *
 * `retry: false` — a 5xx here is an enrichment failure and the requirement is
 * that it not become a request storm on top of a broken dependency. The panel
 * offers an explicit retry instead.
 */
export function useVideoIntel(projectId: string, enabled: boolean): UseQueryResult<VideoIntelView, AppError> {
  const authenticated = useIsAuthenticated();
  return useQuery<VideoIntelView, AppError>({
    queryKey: queryKeys.enrichment.videoIntel(projectId),
    queryFn: async ({ signal }): Promise<VideoIntelView> => {
      const raw = await apiFetch<unknown>(
        `/projects/${encodeURIComponent(projectId)}/enrichment/video-intel`,
        signal !== undefined ? { signal } : undefined,
      );
      return parseVideoIntel(raw);
    },
    enabled: authenticated && enabled && projectId !== '',
    staleTime: 60_000,
    refetchOnWindowFocus: false,
    retry: false,
  });
}

/** Per-segment lip-sync scores plus the separate transformed-asset descriptor. */
export function useLipSync(projectId: string, enabled: boolean): UseQueryResult<LipSyncView, AppError> {
  const authenticated = useIsAuthenticated();
  return useQuery<LipSyncView, AppError>({
    queryKey: queryKeys.enrichment.lipSync(projectId),
    queryFn: async ({ signal }): Promise<LipSyncView> => {
      const raw = await apiFetch<unknown>(
        `/projects/${encodeURIComponent(projectId)}/enrichment/lip-sync`,
        signal !== undefined ? { signal } : undefined,
      );
      return parseLipSync(raw);
    },
    enabled: authenticated && enabled && projectId !== '',
    staleTime: 60_000,
    refetchOnWindowFocus: false,
    retry: false,
  });
}

/**
 * Click-time signed URL for the lip-sync transformed asset (R3).
 *
 * NOT a query. A signed URL is a bearer-shaped value with a 15-minute life
 * that the API binds to the tenant; caching one in the query cache would put
 * it in memory long past its life and in any cache dump taken for support.
 * This is the same mechanism the Task 033 export download uses — one call per
 * click, at most one refetch on `410`, and the URL lives in the caller's
 * handler for the length of one click.
 *
 * It is a DISTINCT artifact from the core outputs of Task 033: different
 * route, different file, different lifecycle, and it never appears in the
 * outputs or exports lists.
 */
export function fetchLipSyncAssetUrl(projectId: string): Promise<SignedDownloadUrl> {
  return fetchSignedDownloadUrl(
    async () => {
      // A plain GET with `redirect: 'manual'` so the 302 is observable instead
      // of being followed by the transport with the bearer token attached.
      const { apiClient } = await import('../../api/client/index.js');
      return apiClient.requestRaw(
        'GET',
        `/projects/${encodeURIComponent(projectId)}/enrichment/lip-sync/asset`,
      );
    },
    {
      missing: 'The lip-sync asset no longer exists.',
      unavailable: 'The lip-sync asset could not be prepared. No data was changed.',
    },
  );
}

/** Normalizes an error thrown by the click-time download path. */
export function normalizeDownloadError(error: unknown): AppError {
  return normalizeGet(error);
}