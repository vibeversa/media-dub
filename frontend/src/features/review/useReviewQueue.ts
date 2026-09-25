import { useQuery } from '@tanstack/react-query';
import type { QueryClient, UseQueryResult } from '@tanstack/react-query';
import { apiClient } from '../../api/client/index.js';
import { normalizeError } from '../../api/errors/index.js';
import type { AppError } from '../../api/errors/index.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import type { ReviewListParams } from '../../api/queryKeys/index.js';
import { useIsAuthenticated } from '../auth/useSession.js';
import {
  REVIEW_MAX_PAGES,
  REVIEW_PAGE_SIZE,
  parseReviewContext,
  parseReviewQueueItems,
  toReviewListQuery,
} from './types.js';
import type { ReviewContextView, ReviewFilters, ReviewQueueItemView } from './types.js';

/**
 * Review queue + single-screen context reads (Task 031).
 *
 * - `useReviewQueue(projectId, filters)` is the single queue query on
 *   `queryKeys.review.list(projectId, params)`. Every active filter becomes a
 *   server-side query param via `toReviewListQuery` (R1); the list pages
 *   `listProjectReviews` (default 50, capped at 10 pages) until `hasMore` is
 *   false, dedupes by id, and sorts by creation time.
 * - `useReviewContext(reviewId)` is the single context query on
 *   `queryKeys.reviewContext.detail(reviewId)` (`GET /reviews/{id}/context`,
 *   eleven sections). History comes from the same aggregate (newest-first in
 *   the card); there is no separate history endpoint.
 */

async function fetchQueuePage(
  projectId: string,
  filters: ReviewFilters,
  page: number,
  signal?: AbortSignal,
): Promise<unknown> {
  const filterQuery = toReviewListQuery(filters);
  const query: Record<string, string | number | undefined> = {
    ...filterQuery,
    page,
    pageSize: REVIEW_PAGE_SIZE,
  };
  return apiClient.listProjectReviews(
    { path: { projectId }, query: query as { page?: number; pageSize?: number } },
    signal !== undefined ? { signal } : undefined,
  );
}

async function fetchAllQueue(projectId: string, filters: ReviewFilters, signal: AbortSignal | undefined): Promise<ReviewQueueItemView[]> {
  const merged: ReviewQueueItemView[] = [];
  for (let page = 1; page <= REVIEW_MAX_PAGES; page += 1) {
    const response = await fetchQueuePage(projectId, filters, page, signal);
    merged.push(...parseReviewQueueItems(response));
    const record = response as unknown as { hasMore?: boolean };
    if (record.hasMore !== true) {
      break;
    }
    if (signal?.aborted === true) {
      break;
    }
  }
  const deduped = new Map<string, ReviewQueueItemView>();
  for (const item of merged) {
    deduped.set(item.id, item);
  }
  return [...deduped.values()].sort((a, b) => a.createdAt.localeCompare(b.createdAt) || a.id.localeCompare(b.id));
}

/** Cache params for the queue key (filters partition the key). */
export function reviewListParamsFor(filters: ReviewFilters): ReviewListParams {
  const query = toReviewListQuery(filters);
  return {
    ...(query['severity'] !== undefined ? { severity: String(query['severity']) } : {}),
    ...(query['status'] !== undefined ? { status: String(query['status']) } : {}),
    ...(query['type'] !== undefined ? { type: String(query['type']) } : {}),
    ...(query['speakerId'] !== undefined ? { speakerId: String(query['speakerId']) } : {}),
    ...(query['language'] !== undefined ? { language: String(query['language']) } : {}),
    ...(query['age'] !== undefined ? { age: String(query['age']) } : {}),
  };
}

export function useReviewQueue(
  projectId: string,
  filters: ReviewFilters,
): UseQueryResult<readonly ReviewQueueItemView[], AppError> {
  const enabled = useIsAuthenticated() && projectId !== '';
  const params = reviewListParamsFor(filters);
  return useQuery<readonly ReviewQueueItemView[], AppError>({
    queryKey: queryKeys.review.list(projectId, params),
    queryFn: async ({ signal }): Promise<readonly ReviewQueueItemView[]> => {
      try {
        return await fetchAllQueue(projectId, filters, signal);
      } catch (error) {
        throw normalizeError(error, { method: 'GET' });
      }
    },
    enabled,
  });
}

async function fetchReviewContext(reviewId: string): Promise<ReviewContextView> {
  try {
    const response = await apiClient.getReviewContext({ path: { reviewId } });
    const parsed = parseReviewContext(response as unknown);
    if (parsed === undefined) {
      throw new Error('Review context returned no review.');
    }
    return parsed;
  } catch (error) {
    throw normalizeError(error, { method: 'GET' });
  }
}

export function useReviewContext(reviewId: string | undefined): UseQueryResult<ReviewContextView | undefined, AppError> {
  const enabled = useIsAuthenticated() && reviewId !== undefined && reviewId !== '';
  return useQuery<ReviewContextView | undefined, AppError>({
    queryKey: queryKeys.reviewContext.detail(reviewId ?? ''),
    queryFn: async (): Promise<ReviewContextView | undefined> => fetchReviewContext(reviewId ?? ''),
    enabled,
  });
}

/** Invalidates the queue + one context entry after a disposition. */
export async function invalidateReviewQueue(
  queryClient: QueryClient,
  projectId: string,
  reviewId?: string,
): Promise<void> {
  await queryClient.invalidateQueries({ queryKey: queryKeys.review.list(projectId) });
  if (reviewId !== undefined && reviewId !== '') {
    await queryClient.invalidateQueries({ queryKey: queryKeys.reviewContext.detail(reviewId) });
    await queryClient.invalidateQueries({ queryKey: queryKeys.review.detail(reviewId) });
  }
}
