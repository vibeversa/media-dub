import { useState } from 'react';
import type { ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Alert } from '../../components/Alert/Alert.js';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { useToast } from '../../components/Toast/useToast.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { NOTIFICATION_TYPES } from './types.js';
import type { NotificationTypeName } from './types.js';
import { NotificationList } from './NotificationList.js';
import { invalidateNotifications, useNotifications } from './useNotifications.js';
import { useMarkAllNotificationsRead } from './useMarkRead.js';
import { useNotificationPrefs } from './useNotificationPrefs.js';
import { useNotificationStream } from './useNotificationStream.js';
import { useUnreadCount } from './useUnreadCount.js';

export interface NotificationCenterProps {
  /** Deleted-target ids that render `GoneState` rows (tests + tombstones). */
  readonly deletedIds?: readonly string[];
}

/**
 * Notification Center (Task 034).
 *
 * Durable inbox over `GET /notifications` (newest-first, paginated) with an
 * accurate `GET /notifications/unread-count` badge, optimistic single/read-all
 * mutations with rollback, per-type deep links (project/review/export with
 * dashboard/`GoneState` fallbacks), SSE `notification.created` invalidation
 * (hint only, id-keyed dedupe), and per-type delivery toggles bound to the
 * `notificationPreferences` identity key. Renders display fields only — raw
 * payloads, tokens, signed URLs, and transcript/media bodies never render.
 */
export function NotificationCenter({ deletedIds = [] }: NotificationCenterProps): ReactNode {
  const { push } = useToast();
  const queryClient = useQueryClient();
  const [page, setPage] = useState(1);
  const listQuery = useNotifications(page);
  const unreadQuery = useUnreadCount();
  const markAll = useMarkAllNotificationsRead();
  const prefs = useNotificationPrefs();
  useNotificationStream();

  const pageData = listQuery.data;
  const items = pageData?.items ?? [];
  const unreadRaw = unreadQuery.data as number | { unreadCount?: number } | undefined;
  const unreadCount = typeof unreadRaw === 'number' ? unreadRaw : (unreadRaw?.unreadCount ?? 0);

  async function handleReadAll(): Promise<void> {
    try {
      await markAll.mutateAsync();
      push('success', 'All notifications marked as read.');
    } catch {
      push('error', 'Could not mark all as read. List refreshed.');
      await invalidateNotifications(queryClient);
    }
  }

  function handleUnreadConflictRetry(): void {
    void queryClient.invalidateQueries({ queryKey: queryKeys.notifications.list({ page: 1, pageSize: 20 }) });
    void unreadQuery.refetch();
  }

  let listBody: ReactNode;
  if (listQuery.isPending && pageData === undefined) {
    listBody = (
      <div data-testid="notifications-loading">
        <Skeleton lines={5} />
      </div>
    );
  } else if (listQuery.isError && pageData === undefined) {
    listBody = (
      <div data-testid="notifications-error">
        <ErrorState
          title="Notifications unavailable"
          message={listQuery.error?.message ?? 'Notifications could not be loaded. No data was changed.'}
          correlationId={listQuery.error?.correlationId}
          onRetry={() => {
            void listQuery.refetch();
          }}
        />
      </div>
    );
  } else if (items.length === 0) {
    listBody = (
      <div data-testid="notifications-empty">
        <EmptyState title="No notifications" description="New activity will appear here." />
      </div>
    );
  } else {
    listBody = <NotificationList items={items} deletedIds={deletedIds} />;
  }

  const unreadConflict = unreadQuery.isError && unreadQuery.error?.status === 409;

  return (
    <section data-testid="notifications-center" aria-label="Notification center">
      <div style={{ display: 'flex', gap: 'var(--space-3)', alignItems: 'center', flexWrap: 'wrap' }}>
        <h2>Notifications</h2>
        <p data-testid="notifications-unread-count" className="dp-muted">
          {`${String(unreadCount)} unread`}
        </p>
        <button
          type="button"
          data-testid="notifications-read-all"
          className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
          disabled={markAll.isPending || items.length === 0}
          onClick={() => {
            void handleReadAll();
          }}
        >
          Mark all read
        </button>
      </div>
      {unreadConflict ? (
        <div data-testid="notifications-unread-conflict">
          <Alert tone="warning" title="Unread count expired" details={unreadQuery.error?.correlationId}>
            <p>The unread count expired. The first page was refetched.</p>
            <button
              type="button"
              data-testid="notifications-unread-retry"
              className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
              onClick={handleUnreadConflictRetry}
            >
              Refresh notifications
            </button>
          </Alert>
        </div>
      ) : null}
      {unreadQuery.isError && !unreadConflict ? (
        <div data-testid="notifications-unread-error">
          <Alert tone="warning" title="Unread count unavailable" details={unreadQuery.error?.correlationId}>
            <p>Showing the last loaded list. The badge may be stale.</p>
          </Alert>
        </div>
      ) : null}
      {listBody}
      {pageData !== undefined && (pageData.hasMore || page > 1) ? (
        <div style={{ display: 'flex', gap: 'var(--space-2)', alignItems: 'center' }}>
          <button
            type="button"
            data-testid="notifications-page-prev"
            className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
            disabled={page <= 1 || listQuery.isFetching}
            onClick={() => {
              setPage((current) => Math.max(1, current - 1));
            }}
          >
            Previous
          </button>
          <p data-testid="notifications-page-info" className="dp-muted">
            {`Page ${String(pageData.page)} of ${String(Math.max(1, Math.ceil(pageData.total / pageData.pageSize)))}`}
          </p>
          <button
            type="button"
            data-testid="notifications-page-next"
            className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
            disabled={!pageData.hasMore || listQuery.isFetching}
            onClick={() => {
              setPage((current) => current + 1);
            }}
          >
            Next
          </button>
        </div>
      ) : null}
      <section data-testid="notifications-prefs" aria-label="Notification preferences">
        <h3>Delivery preferences</h3>
        <p className="dp-muted">Toggles disable future delivery only. History is never deleted.</p>
        {prefs.isPending ? (
          <div data-testid="notifications-prefs-loading">
            <Skeleton lines={3} />
          </div>
        ) : (
          <ul style={{ listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: 'var(--space-2)' }}>
            {NOTIFICATION_TYPES.map((type) => (
              <li key={type}>
                <label>
                  <input
                    type="checkbox"
                    data-testid={`notifications-pref-${type}`}
                    checked={prefs.prefs[type as NotificationTypeName] === true}
                    disabled={prefs.savePending}
                    onChange={(event) => {
                      prefs.setTypeEnabled(type as NotificationTypeName, event.target.checked);
                    }}
                  />
                  {type}
                </label>
              </li>
            ))}
          </ul>
        )}
        {prefs.fieldError !== undefined ? (
          <div data-testid="notifications-prefs-error">
            <Alert tone="error" title="Preferences could not be saved">
              <p data-testid="notifications-prefs-error-message">{prefs.fieldError}</p>
            </Alert>
          </div>
        ) : null}
      </section>
      <div hidden>
        <span data-testid="notifications-list-key">{JSON.stringify(queryKeys.notifications.list())}</span>
        <span data-testid="notifications-unread-key">{JSON.stringify(queryKeys.notifications.unreadCount())}</span>
      </div>
    </section>
  );
}
