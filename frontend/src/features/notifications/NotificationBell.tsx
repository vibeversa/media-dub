import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { formatBadgeCount } from './types.js';

export interface NotificationBellProps {
  readonly unreadCount: number;
  /** Destination for the bell (defaults to the Center). */
  readonly to?: string;
}

/**
 * Header notification bell (Task 034).
 *
 * Renders the Task 018 shell slot: a bell icon linking to the Center with
 * an unread-count badge. The badge hides at zero, renders `99+` overflow,
 * and announces via `aria-live="polite"` so screen readers hear count
 * changes within one SSE round-trip. Counts render as plain text only.
 */
export function NotificationBell({ unreadCount, to = '/notifications' }: NotificationBellProps): ReactNode {
  const { t } = useTranslation();
  const safe = Number.isFinite(unreadCount) && unreadCount > 0 ? Math.floor(unreadCount) : 0;
  const label = safe > 0 ? t('nav:bell.unread', { count: safe }) : t('nav:bell.label');
  return (
    <Link to={to} aria-label={label} data-testid="nav-bell" className="relative rounded p-2">
      <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
        <path d="M6 9a6 6 0 1 1 12 0c0 5 2 6 2 6H4s2-1 2-6" />
        <path d="M10 20a2 2 0 0 0 4 0" />
      </svg>
      {safe > 0 ? (
        <span
          data-testid="nav-bell-badge"
          aria-live="polite"
          aria-atomic="true"
          className="absolute inset-block-start-0 inset-inline-end-0 rounded-full px-1 text-xs"
        >
          {formatBadgeCount(safe)}
        </span>
      ) : null}
    </Link>
  );
}
