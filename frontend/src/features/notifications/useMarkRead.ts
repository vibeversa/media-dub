import { useMutation, useQueryClient } from '@tanstack/react-query';
import type { QueryClient, UseMutationResult } from '@tanstack/react-query';
import { apiClient } from '../../api/client/index.js';
import { normalizeError } from '../../api/errors/index.js';
import type { AppError } from '../../api/errors/index.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { invalidateNotifications } from './useNotifications.js';
import type { NotificationPageView, NotificationView } from './types.js';

/**
 * Read mutations with optimistic badge updates + rollback (Task 034, R3).
 *
 * - `useMarkNotificationRead` marks one row read (`POST
 *   /notifications/{id}/read`, idempotent). On open it flips the row to read
 *   and decrements the cached unread count optimistically; on failure it
 *   restores both snapshots, toasts, and refetches the first page.
 * - `useMarkAllNotificationsRead` marks the whole inbox read (`POST
 *   /notifications/read-all`, idempotent). It clears the badge optimistically
 *   and rolls back on failure.
 *
 * Both mutations read/write only the `queryKeys.notifications` keys
 * (tenant-scoped via the Task 017 client). Signed URLs, tokens, and raw
 * payloads never enter the cache.
 */

function nowIso(): string {
  return new Date().toISOString();
}

function markPageItemRead(page: NotificationPageView, notificationId: string, readAt: string): NotificationPageView {
  return {
    ...page,
    items: page.items.map((item): NotificationView => (item.id === notificationId && !item.isRead
      ? { ...item, readAt, isRead: true }
      : item)),
  };
}

function markPageAllRead(page: NotificationPageView, readAt: string): NotificationPageView {
  return {
    ...page,
    items: page.items.map((item): NotificationView => (item.isRead ? item : { ...item, readAt, isRead: true })),
  };
}

interface MarkReadContext {
  readonly listSnapshots: ReadonlyArray<readonly [readonly unknown[], NotificationPageView | undefined]>;
  readonly unreadSnapshot: number | undefined;
}

function snapshotLists(queryClient: QueryClient): MarkReadContext['listSnapshots'] {
  return queryClient.getQueriesData<NotificationPageView>({ queryKey: queryKeys.notifications.lists() });
}

export function useMarkNotificationRead(): UseMutationResult<void, AppError, string, MarkReadContext> {
  const queryClient = useQueryClient();
  return useMutation<void, AppError, string, MarkReadContext>({
    mutationFn: async (notificationId: string): Promise<void> => {
      try {
        await apiClient.markNotificationRead({ path: { notificationId } });
      } catch (error) {
        throw normalizeError(error, { method: 'POST' });
      }
    },
    onMutate: async (notificationId: string): Promise<MarkReadContext> => {
      await queryClient.cancelQueries({ queryKey: queryKeys.notifications.lists() });
      await queryClient.cancelQueries({ queryKey: queryKeys.notifications.unreadCount() });
      const listSnapshots = snapshotLists(queryClient);
      const unreadSnapshot = queryClient.getQueryData<number>(queryKeys.notifications.unreadCount());
      const readAt = nowIso();
      for (const [key, data] of listSnapshots) {
        if (data === undefined) {
          continue;
        }
        const before = data.items.find((item) => item.id === notificationId);
        if (before !== undefined && !before.isRead) {
          queryClient.setQueryData(key, markPageItemRead(data, notificationId, readAt));
        }
      }
      if (unreadSnapshot !== undefined && unreadSnapshot > 0) {
        const decremented = unreadSnapshot - 1;
        const targetKnown = listSnapshots.some(([, data]) =>
          data?.items.some((item) => item.id === notificationId && !item.isRead) === true,
        );
        queryClient.setQueryData(
          queryKeys.notifications.unreadCount(),
          targetKnown ? Math.max(0, decremented) : unreadSnapshot,
        );
      }
      return { listSnapshots, unreadSnapshot };
    },
    onError: (_error, _variables, context) => {
      if (context !== undefined) {
        for (const [key, data] of context.listSnapshots) {
          queryClient.setQueryData(key, data);
        }
        queryClient.setQueryData(queryKeys.notifications.unreadCount(), context.unreadSnapshot);
      }
    },
    onSettled: () => {
      void invalidateNotifications(queryClient);
    },
    retry: false,
  });
}

export function useMarkAllNotificationsRead(): UseMutationResult<void, AppError, void, MarkReadContext> {
  const queryClient = useQueryClient();
  return useMutation<void, AppError, void, MarkReadContext>({
    mutationFn: async (): Promise<void> => {
      try {
        await apiClient.markAllNotificationsRead();
      } catch (error) {
        throw normalizeError(error, { method: 'POST' });
      }
    },
    onMutate: async (): Promise<MarkReadContext> => {
      await queryClient.cancelQueries({ queryKey: queryKeys.notifications.lists() });
      await queryClient.cancelQueries({ queryKey: queryKeys.notifications.unreadCount() });
      const listSnapshots = snapshotLists(queryClient);
      const unreadSnapshot = queryClient.getQueryData<number>(queryKeys.notifications.unreadCount());
      const readAt = nowIso();
      for (const [key, data] of listSnapshots) {
        if (data !== undefined) {
          queryClient.setQueryData(key, markPageAllRead(data, readAt));
        }
      }
      queryClient.setQueryData(queryKeys.notifications.unreadCount(), 0);
      return { listSnapshots, unreadSnapshot };
    },
    onError: (_error, _variables, context) => {
      if (context !== undefined) {
        for (const [key, data] of context.listSnapshots) {
          queryClient.setQueryData(key, data);
        }
        queryClient.setQueryData(queryKeys.notifications.unreadCount(), context.unreadSnapshot);
      }
    },
    onSettled: () => {
      void invalidateNotifications(queryClient);
    },
    retry: false,
  });
}
