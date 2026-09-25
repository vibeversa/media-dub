import { useQuery } from '@tanstack/react-query';
import type { QueryClient, UseQueryResult } from '@tanstack/react-query';
import { apiClient } from '../../api/client/index.js';
import { normalizeError } from '../../api/errors/index.js';
import type { AppError } from '../../api/errors/index.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { useIsAuthenticated } from '../auth/useSession.js';
import {
  NOTIFICATIONS_PAGE_SIZE,
  parseNotificationPage,
  parseNotifications,
  parseUnreadCount,
} from './types.js';
import type { NotificationPageView, NotificationView } from './types.js';

export { NOTIFICATIONS_PAGE_SIZE };

/**
 * Notification inbox reads (Task 034).
 *
 * - `useNotifications(page, pageSize)` is the durable list query on
 *   `queryKeys.notifications.list({ page, pageSize })`
 *   (`GET /notifications`, newest-first page envelope). The API is the source
 *   of truth; SSE `notification.created` is an invalidation hint only
 *   (see `useNotificationStream`). No ad-hoc polling: live updates flow
 *   through invalidation, and generating states do not poll this inbox.
 * - `fetchNotifications`/`fetchUnreadCount` are the throw-normalized fetch
 *   cores shared by queries and invalidation fallbacks.
 * - `invalidateNotifications` refetches the first page plus the unread count
 *   (used after 409/expired cursors and after read mutations settle).
 */

export async function fetchNotifications(
  page = 1,
  pageSize: number = NOTIFICATIONS_PAGE_SIZE,
  signal?: AbortSignal,
): Promise<NotificationPageView> {
  const safePage = Number.isFinite(page) && page >= 1 ? Math.floor(page) : 1;
  const safeSize = Number.isFinite(pageSize) && pageSize >= 1 ? Math.floor(Math.min(pageSize, 100)) : NOTIFICATIONS_PAGE_SIZE;
  try {
    const response = await apiClient.listNotifications(
      { path: {}, query: { page: safePage, pageSize: safeSize } },
      signal !== undefined ? { signal } : undefined,
    );
    return parseNotificationPage(response as unknown);
  } catch (error) {
    throw normalizeError(error, { method: 'GET' });
  }
}

export async function fetchNotificationItems(signal?: AbortSignal): Promise<readonly NotificationView[]> {
  try {
    const response = await apiClient.listNotifications(
      { path: {}, query: { page: 1, pageSize: NOTIFICATIONS_PAGE_SIZE } },
      signal !== undefined ? { signal } : undefined,
    );
    return parseNotifications(response as unknown);
  } catch (error) {
    throw normalizeError(error, { method: 'GET' });
  }
}

export function useNotifications(
  page = 1,
  pageSize: number = NOTIFICATIONS_PAGE_SIZE,
): UseQueryResult<NotificationPageView, AppError> {
  const enabled = useIsAuthenticated();
  const safePage = Number.isFinite(page) && page >= 1 ? Math.floor(page) : 1;
  const safeSize = Number.isFinite(pageSize) && pageSize >= 1 ? Math.floor(Math.min(pageSize, 100)) : NOTIFICATIONS_PAGE_SIZE;
  return useQuery<NotificationPageView, AppError>({
    queryKey: queryKeys.notifications.list({ page: safePage, pageSize: safeSize }),
    queryFn: async ({ signal }): Promise<NotificationPageView> => fetchNotifications(safePage, safeSize, signal),
    enabled,
    staleTime: 30_000,
    refetchInterval: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    retry: false,
  });
}

export async function fetchUnreadCount(signal?: AbortSignal): Promise<number> {
  try {
    const response = await apiClient.getUnreadNotificationCount(
      undefined,
      signal !== undefined ? { signal } : undefined,
    );
    return parseUnreadCount(response as unknown);
  } catch (error) {
    throw normalizeError(error, { method: 'GET' });
  }
}

/** Invalidates the first page plus the unread count (409/expired fallback). */
export async function invalidateNotifications(queryClient: QueryClient): Promise<void> {
  await queryClient.invalidateQueries({ queryKey: queryKeys.notifications.list() });
  await queryClient.invalidateQueries({ queryKey: queryKeys.notifications.unreadCount() });
}
