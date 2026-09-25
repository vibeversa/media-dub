import type { ReactNode } from 'react';
import { NotificationItem } from './NotificationItem.js';
import type { NotificationView } from './types.js';

export interface NotificationListProps {
  readonly items: readonly NotificationView[];
  readonly deletedIds?: readonly string[];
}

/**
 * Durable notification list (Task 034).
 *
 * Renders rows newest-first (the caller sorts via `parseNotifications`).
 * Dedupe is id-keyed upstream so SSE duplicates never duplicate rows. Rows
 * render display fields only; see `NotificationItem` for the
 * no-sensitive-payloads rule.
 */
export function NotificationList({ items, deletedIds = [] }: NotificationListProps): ReactNode {
  return (
    <div data-testid="notifications-list" data-total={String(items.length)} data-rendered={String(items.length)}>
      <ul style={{ listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: 'var(--space-3)' }}>
        {items.map((item) => (
          <li key={item.id}>
            <NotificationItem item={item} deletedIds={deletedIds} />
          </li>
        ))}
      </ul>
    </div>
  );
}
