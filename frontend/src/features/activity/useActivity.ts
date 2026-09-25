import { useQuery } from '@tanstack/react-query';
import type { QueryClient, UseQueryResult } from '@tanstack/react-query';
import { apiClient } from '../../api/client/index.js';
import { normalizeError } from '../../api/errors/index.js';
import type { AppError } from '../../api/errors/index.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { useIsAuthenticated } from '../auth/useSession.js';
import { ACTIVITY_PAGE_SIZE, parseActivityPage } from './types.js';
import type { ActivityPageView } from './types.js';

/**
 * Activity feed read model (Task 035) over the Task 008 contract
 * (`GET /projects/{id}/activity`, page-based, newest first).
 *
 * - Single fetcher plus a single paged hook on the activity factory scope;
 *   no other aggregate calls live here (the workspace `activity.recent`
 *   slice stays a 10-row preview in Task 025).
 * - Shapes are parsed defensively: only display fields survive, reservation
 *   ids and provider-internal keys never reach the view.
 * - Invalidation flows through the shared registry: progress/completion SSE
 *   frames for the open project invalidate the activity prefix (see
 *   `queryKeys.activity.all`), so no polling lives here.
 */

/** Single activity page fetch. Never logs payload contents. */
export async function fetchActivityPage(
  projectId: string,
  page: number,
  pageSize: number,
  signal?: AbortSignal,
): Promise<ActivityPageView> {
  const safePage = Number.isFinite(page) && page >= 1 ? Math.floor(page) : 1;
  const safeSize =
    Number.isFinite(pageSize) && pageSize >= 1 ? Math.min(100, Math.max(1, Math.floor(pageSize))) : ACTIVITY_PAGE_SIZE;
  try {
    const raw = await apiClient.listWorkspaceActivity(
      { path: { projectId }, query: { page: safePage, pageSize: safeSize } },
      signal !== undefined ? { signal } : undefined,
    );
    return parseActivityPage(raw as unknown);
  } catch (error) {
    throw normalizeError(error, { method: 'GET' });
  }
}

/**
 * Paged activity query. Gated on the session plus a non-empty project id.
 * Pagination never loses filter state because filters live in the URL
 * (see `ActivityFilters`); page changes only swap the factory params.
 */
export function useActivity(projectId: string, page: number, pageSize: number = ACTIVITY_PAGE_SIZE): UseQueryResult<ActivityPageView, AppError> {
  const enabled = useIsAuthenticated() && projectId !== '';
  const safePage = Number.isFinite(page) && page >= 1 ? Math.floor(page) : 1;
  const safeSize =
    Number.isFinite(pageSize) && pageSize >= 1 ? Math.min(100, Math.max(1, Math.floor(pageSize))) : ACTIVITY_PAGE_SIZE;
  return useQuery<ActivityPageView, AppError>({
    queryKey: queryKeys.activity.list(projectId, { page: safePage, pageSize: safeSize }),
    queryFn: ({ signal }) => fetchActivityPage(projectId, safePage, safeSize, signal),
    enabled,
    staleTime: 15_000,
    refetchInterval: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    retry: false,
  });
}

/** Invalidates every activity page for one project (prefix scope). */
export async function invalidateActivity(queryClient: QueryClient, projectId: string): Promise<void> {
  await queryClient.invalidateQueries({ queryKey: queryKeys.activity.all(projectId) });
}
