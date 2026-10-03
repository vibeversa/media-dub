import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { formatRelativeTime, iconForNotificationType, notificationTypeKey } from './types.js';
import type { NotificationView } from './types.js';
import { isGoneLink, notificationLinkFor } from './notificationLinks.js';
import { useMarkNotificationRead } from './useMarkRead.js';
import { useToast } from '../../components/Toast/useToast.js';
import { useTranslation } from 'react-i18next';

export interface NotificationItemProps {
  readonly item: NotificationView;
  /** Deleted-target ids that force a `GoneState` (tests + tombstones). */
  readonly deletedIds?: readonly string[];
}

/**
 * One durable notification row (Task 034).
 *
 * Renders only display fields (`title`/`body`/relative timestamp/link);
 * raw payloads, tokens, signed URLs, and transcript/media bodies never reach
 * this component (see `types.parseNotification`). Type shows as a text icon
 * (never color alone); read/unread is a text state plus `data-read` for
 * tests. Opening the deep link marks an unread row read (optimistic with
 * rollback); deleted targets render a `GoneState` with an explanation and
 * keep the row as read.
 */
export function NotificationItem({ item, deletedIds = [] }: NotificationItemProps): ReactNode {
    const { t } = useTranslation();
const { push } = useToast();
  const markRead = useMarkNotificationRead();
  const deleted = deletedIds.includes(item.id);
  const link = notificationLinkFor({
    type: item.type,
    resourceType: item.resourceType,
    resourceId: deleted ? 'deleted' : item.resourceId,
    projectId: item.projectId,
  });
  const gone = deleted || isGoneLink(link);
  const typeKey = notificationTypeKey(item.type);

  function handleOpen(): void {
    if (item.isRead) {
      return;
    }
    markRead.mutate(item.id, {
      onError: () => {
        push('error', 'Could not mark this notification as read. List refreshed.');
      },
    });
  }

  function handleMarkRead(): void {
    if (item.isRead) {
      return;
    }
    markRead.mutate(item.id, {
      onError: () => {
        push('error', 'Could not mark this notification as read. List refreshed.');
      },
    });
  }

  if (gone) {
    return (
      <article data-testid={`notification-row-${item.id}`} data-read={item.isRead ? 'true' : 'false'} data-type={typeKey}>
        <p data-testid={`notification-icon-${item.id}`} aria-hidden="true">
          {iconForNotificationType(item.type)}
        </p>
        <h4 data-testid={`notification-title-${item.id}`}>{item.title === '' ? item.type : item.title}</h4>
        {item.body !== '' ? <p data-testid={`notification-body-${item.id}`}>{item.body}</p> : null}
        <p data-testid={`notification-time-${item.id}`} className="dp-muted">
          {formatRelativeTime(item.createdAt)}
        </p>
        <p data-testid={`notification-state-${item.id}`} className="dp-muted">
          {item.isRead ? 'Read' : 'Unread'}
        </p>
        <div data-testid={`notifications-gone-${item.id}`} role="note" aria-label={t('notifications:notificationItem.deleted-notification-target')}>
          <p data-testid={`notifications-gone-title-${item.id}`}>{t('notifications:notificationItem.this-item-is-no-longer-available')}</p>
          <p className="dp-muted">{t('notifications:notificationItem.the-linked-project-review-or-export')}</p>
          <Link data-testid={`notifications-gone-dashboard-${item.id}`} to={link.href}>
            {t('notifications:notificationItem.back-to')} {link.href.startsWith('/projects/') ? 'project' : 'dashboard'}
          </Link>
        </div>
      </article>
    );
  }

  return (
    <article data-testid={`notification-row-${item.id}`} data-read={item.isRead ? 'true' : 'false'} data-type={typeKey}>
      <p data-testid={`notification-icon-${item.id}`} aria-hidden="true">
        {iconForNotificationType(item.type)}
      </p>
      <h4 data-testid={`notification-title-${item.id}`}>{item.title === '' ? item.type : item.title}</h4>
      {item.body !== '' ? <p data-testid={`notification-body-${item.id}`}>{item.body}</p> : null}
      <p data-testid={`notification-time-${item.id}`} className="dp-muted">
        {formatRelativeTime(item.createdAt)}
      </p>
      <p data-testid={`notification-state-${item.id}`} className="dp-muted">
        {item.isRead ? 'Read' : 'Unread'}
      </p>
      <Link data-testid={`notification-link-${item.id}`} to={link.href} onClick={handleOpen}>
        {t('notifications:notificationItem.open')} {link.kind}
      </Link>
      {!item.isRead ? (
        <button
          type="button"
          data-testid={`notification-mark-read-${item.id}`}
          className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
          disabled={markRead.isPending}
          onClick={handleMarkRead}
        >
          {t('notifications:notificationItem.mark-read')}
        </button>
      ) : null}
    </article>
  );
}
