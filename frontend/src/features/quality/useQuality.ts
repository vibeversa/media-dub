import { useQuery } from '@tanstack/react-query';
import type { QueryClient, UseQueryResult } from '@tanstack/react-query';
import { apiClient } from '../../api/client/index.js';
import { normalizeError } from '../../api/errors/index.js';
import type { AppError } from '../../api/errors/index.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { useIsAuthenticated } from '../auth/useSession.js';
import { fetchWorkspace, getWorkspacePollInterval } from '../processing/useWorkspace.js';
import {
  QUALITY_MAX_PAGES,
  QUALITY_PAGE_SIZE,
  buildQualityIssues,
  isQualityPendingRun,
  parseQualityProjection,
  parseQualitySegments,
  summarizeQuality,
} from './types.js';
import type { QualityIssueView, QualitySummaryView } from './types.js';

export interface QualityView {
  readonly issues: readonly QualityIssueView[];
  readonly summary: QualitySummaryView;
  readonly totalSegments: number;
  readonly runStatus: string | undefined;
  readonly runId: string | undefined;
  readonly isQcPending: boolean;
  readonly allowedActions: readonly string[];
  readonly rawBlocked: number;
  readonly rawFailed: number;
  readonly rawCodes: readonly string[];
}

async function fetchSegmentsPage(
  projectId: string,
  page: number,
  signal?: AbortSignal,
): Promise<unknown> {
  return apiClient.listSegments(
    { path: { projectId }, query: { page, pageSize: QUALITY_PAGE_SIZE } },
    signal !== undefined ? { signal } : undefined,
  );
}

async function fetchAllSegments(projectId: string, signal: AbortSignal | undefined): Promise<ReturnType<typeof parseQualitySegments>> {
  const merged: ReturnType<typeof parseQualitySegments> = [];
  for (let page = 1; page <= QUALITY_MAX_PAGES; page += 1) {
    const response = await fetchSegmentsPage(projectId, page, signal);
    merged.push(...parseQualitySegments(response));
    const record = response as unknown as { hasMore?: boolean };
    if (record.hasMore !== true) {
      break;
    }
    if (signal?.aborted === true) {
      break;
    }
  }
  const deduped = new Map<string, (typeof merged)[number]>();
  for (const segment of merged) {
    deduped.set(segment.id, segment);
  }
  return [...deduped.values()].sort((a, b) => a.startMs - b.startMs || a.sequence - b.sequence);
}

async function fetchProjection(projectId: string, signal: AbortSignal | undefined): Promise<{ blocked: number; failed: number; codes: readonly string[] }> {
  try {
    const response = await apiClient.getWorkspaceQuality(
      { path: { projectId } },
      signal !== undefined ? { signal } : undefined,
    );
    return parseQualityProjection(response as unknown);
  } catch {
    return { blocked: 0, failed: 0, codes: [] };
  }
}

/**
 * Quality read model (Task 032).
 *
 * Single query on `queryKeys.quality.detail(projectId)` that merges the
 * evidence-backed sources: segment rows (per-code issues), the counts-only
 * quality projection (cross-check, never payload text), and the workspace
 * aggregate (run status for the pending skeleton plus `allowedActions` for
 * retry gating). Issues are one per `(segment, code)`; the summary counts
 * derive from those issues plus clean-segment passed count, so R1 always
 * holds (`warning+retry+review+blocked === totalIssues`). Live updates flow
 * through the Task 026 registry (`run`/`stage`/`output` invalidate this
 * key); no panel fetches on its own. Signed URLs stay in cache only and
 * never enter shareable filter URLs.
 */
export async function fetchQuality(projectId: string, signal?: AbortSignal): Promise<QualityView> {
  const [segments, projection, workspace] = await Promise.all([
    fetchAllSegments(projectId, signal),
    fetchProjection(projectId, signal),
    fetchWorkspace(projectId),
  ]);
  const issues = buildQualityIssues(segments, workspace.allowedActions);
  const summary = summarizeQuality(issues, segments.length);
  const runActive = isQualityPendingRun(workspace.run.status);
  // Pending QC (run active, no rows yet) surfaces as a skeleton upstream,
  // never as zeros: flag it here so the workspace shows in-progress state.
  const isQcPending = runActive && issues.length === 0 && projection.codes.length === 0;
  return {
    issues,
    summary,
    totalSegments: segments.length,
    runStatus: workspace.run.status,
    runId: workspace.run.id,
    isQcPending,
    allowedActions: workspace.allowedActions,
    rawBlocked: projection.blocked,
    rawFailed: projection.failed,
    rawCodes: projection.codes,
  };
}

export function useQuality(projectId: string): UseQueryResult<QualityView, AppError> {
  const enabled = useIsAuthenticated() && projectId !== '';
  return useQuery<QualityView, AppError>({
    queryKey: queryKeys.quality.detail(projectId),
    queryFn: async ({ signal }): Promise<QualityView> => {
      try {
        return await fetchQuality(projectId, signal);
      } catch (error) {
        throw normalizeError(error, { method: 'GET' });
      }
    },
    enabled,
    refetchInterval: (query) => {
      const data = query.state.data as QualityView | undefined;
      if (data !== undefined && data.runStatus !== undefined && isQualityPendingRun(data.runStatus)) {
        return 15_000;
      }
      void getWorkspacePollInterval;
      return false;
    },
  });
}

/** Invalidates the quality aggregate after evidence refetch or retry. */
export async function invalidateQuality(queryClient: QueryClient, projectId: string): Promise<void> {
  await queryClient.invalidateQueries({ queryKey: queryKeys.quality.detail(projectId) });
}
