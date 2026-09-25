import { useQuery } from '@tanstack/react-query';
import type { UseQueryResult } from '@tanstack/react-query';
import type { AppError } from '../../api/errors/index.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { useIsAuthenticated } from '../auth/useSession.js';
import { fetchUnreadCount } from './useNotifications.js';

/**
 * Unread-count query (Task 034).
 *
 * Reads `queryKeys.notifications.unreadCount()`
 * (`GET /notifications/unread-count`, same scope as the list). The server
 * count is authoritative; the badge never increments locally (SSE duplicates
 * would inflate it). No polling: stale-while-revalidate on SSE invalidation
 * only (`useNotificationStream` invalidates this key on
 * `notification.created`). A 409/expired cursor falls back to a full refetch
 * of the first list page (see `NotificationCenter`).
 */
export function useUnreadCount(): UseQueryResult<number, AppError> {
  const enabled = useIsAuthenticated();
  return useQuery<number, AppError>({
    queryKey: queryKeys.notifications.unreadCount(),
    queryFn: async ({ signal }): Promise<number> => fetchUnreadCount(signal),
    enabled,
    staleTime: 30_000,
    refetchInterval: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    retry: false,
  });
}
