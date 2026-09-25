/**
 * Notification center domain view (Task 034).
 *
 * Pure parsing + derivation over the Task 012 notifications contract
 * (`GET /notifications` page envelope of `Notification` rows +
 * `GET /notifications/unread-count`). All parsing is defensive (never throws,
 * skips rows without an id) and keeps only display fields (`title`, `body`,
 * link ids). Raw payloads, tokens, signed URLs, and transcript/media bodies
 * are never stored or rendered — see `notificationLinks.ts` for the
 * allowlisted deep-link mapping.
 */

export const NOTIFICATIONS_PAGE_SIZE = 20;

export const NOTIFICATION_PREFERENCE_KEY = 'notificationPreferences';

export type NotificationTypeName =
  | 'ProcessingCompleted'
  | 'ProcessingFailed'
  | 'ManualReviewRequired'
  | 'ReviewResolved'
  | 'ExportCompleted'
  | 'ExportFailed'
  | 'UploadRejected'
  | 'QuotaWarning'
  | 'ProviderPolicyWarning';

export const NOTIFICATION_TYPES: readonly NotificationTypeName[] = [
  'ProcessingCompleted',
  'ProcessingFailed',
  'ManualReviewRequired',
  'ReviewResolved',
  'ExportCompleted',
  'ExportFailed',
  'UploadRejected',
  'QuotaWarning',
  'ProviderPolicyWarning',
];

export type NotificationResourceType =
  | 'ProcessingRun'
  | 'ReviewItem'
  | 'ExportJob'
  | 'MediaAsset'
  | 'DubbingProject'
  | 'Tenant';

export interface NotificationView {
  readonly id: string;
  readonly type: string;
  readonly severity: string;
  readonly title: string;
  readonly body: string;
  readonly resourceType: string | undefined;
  readonly resourceId: string | undefined;
  readonly projectId: string | undefined;
  readonly readAt: string | undefined;
  readonly createdAt: string;
  readonly expiresAt: string | undefined;
  readonly isRead: boolean;
}

export interface NotificationPageView {
  readonly items: readonly NotificationView[];
  readonly page: number;
  readonly pageSize: number;
  readonly total: number;
  readonly hasMore: boolean;
}

function toRecord(value: unknown): Record<string, unknown> | undefined {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : undefined;
}

function pick(record: Record<string, unknown> | undefined, ...keys: readonly string[]): unknown {
  if (record === undefined) {
    return undefined;
  }
  for (const key of keys) {
    const value = record[key];
    if (value !== undefined && value !== null) {
      return value;
    }
  }
  return undefined;
}

function toNonEmptyString(value: unknown): string | undefined {
  return typeof value === 'string' && value !== '' ? value : undefined;
}

function toDisplayText(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

/**
 * Parses one notification row defensively. Keeps only display fields
 * (`title`/`body`/link ids); never retains raw payloads. Returns undefined
 * for rows without an id (caller skips them). Pure.
 */
export function parseNotification(raw: unknown): NotificationView | undefined {
  const record = toRecord(raw);
  if (record === undefined) {
    return undefined;
  }
  const id = toNonEmptyString(pick(record, 'id', 'Id', 'notificationId', 'NotificationId')) ?? '';
  if (id === '') {
    return undefined;
  }
  const readRaw = pick(record, 'readAt', 'ReadAt');
  const readAt = typeof readRaw === 'string' && readRaw !== '' ? readRaw : undefined;
  const createdAt = toNonEmptyString(pick(record, 'createdAt', 'CreatedAt')) ?? '';
  return {
    id,
    type: toNonEmptyString(pick(record, 'type', 'Type')) ?? 'unknown',
    severity: toNonEmptyString(pick(record, 'severity', 'Severity')) ?? 'info',
    title: toDisplayText(pick(record, 'title', 'Title')),
    body: toDisplayText(pick(record, 'body', 'Body')),
    resourceType: toNonEmptyString(pick(record, 'resourceType', 'ResourceType')),
    resourceId: toNonEmptyString(pick(record, 'resourceId', 'ResourceId')),
    projectId: toNonEmptyString(pick(record, 'projectId', 'ProjectId')),
    readAt,
    createdAt,
    expiresAt: toNonEmptyString(pick(record, 'expiresAt', 'ExpiresAt')),
    isRead: readAt !== undefined,
  };
}

/**
 * Id-keyed dedupe (first occurrence wins). Preserves input order; callers
 * sort newest-first afterwards. Pure.
 */
export function dedupeNotifications(items: readonly NotificationView[]): NotificationView[] {
  const seen = new Map<string, NotificationView>();
  for (const item of items) {
    if (!seen.has(item.id)) {
      seen.set(item.id, item);
    }
  }
  return [...seen.values()];
}

/** Newest-first sort (empty `createdAt` sinks last, id breaks ties). Pure. */
export function sortNotificationsNewestFirst(items: readonly NotificationView[]): NotificationView[] {
  return [...items].sort((a, b) => {
    if (a.createdAt === '' && b.createdAt === '') {
      return a.id.localeCompare(b.id);
    }
    if (a.createdAt === '') {
      return 1;
    }
    if (b.createdAt === '') {
      return -1;
    }
    const byTime = b.createdAt.localeCompare(a.createdAt);
    return byTime !== 0 ? byTime : a.id.localeCompare(b.id);
  });
}

/**
 * Parses a page envelope (`{ items, page, pageSize, total, hasMore }`).
 * Skips bad rows, dedupes by id, sorts newest-first. Never throws. Pure.
 */
export function parseNotifications(raw: unknown): NotificationView[] {
  const record = toRecord(raw);
  const items = record !== undefined ? pick(record, 'items', 'Items') : raw;
  if (!Array.isArray(items)) {
    return [];
  }
  const out: NotificationView[] = [];
  for (const entry of items as unknown[]) {
    const parsed = parseNotification(entry);
    if (parsed !== undefined) {
      out.push(parsed);
    }
  }
  return sortNotificationsNewestFirst(dedupeNotifications(out));
}

/** Parses the full page envelope with paging metadata. Never throws. Pure. */
export function parseNotificationPage(raw: unknown): NotificationPageView {
  const record = toRecord(raw) ?? {};
  const items = parseNotifications(raw);
  const toPageNumber = (value: unknown, fallback: number): number => {
    if (typeof value === 'number' && Number.isFinite(value) && value >= 1) {
      return Math.floor(value);
    }
    return fallback;
  };
  const page = toPageNumber(pick(record, 'page', 'Page'), 1);
  const pageSize = toPageNumber(pick(record, 'pageSize', 'PageSize'), NOTIFICATIONS_PAGE_SIZE);
  const totalRaw = pick(record, 'total', 'Total');
  const total =
    typeof totalRaw === 'number' && Number.isFinite(totalRaw) && totalRaw >= 0
      ? Math.floor(totalRaw)
      : items.length;
  const hasMoreRaw = pick(record, 'hasMore', 'HasMore');
  const hasMore = hasMoreRaw === true || page * pageSize < total;
  return { items, page, pageSize, total, hasMore };
}

/** Parses the unread-count response (`{ unreadCount }`). Defaults to 0. Pure. */
export function parseUnreadCount(raw: unknown): number {
  const record = toRecord(raw);
  const candidate = record !== undefined ? pick(record, 'unreadCount', 'UnreadCount', 'count', 'Count') : undefined;
  if (typeof candidate === 'number' && Number.isFinite(candidate) && candidate >= 0) {
    return Math.floor(candidate);
  }
  return 0;
}

/**
 * Badge text for the header bell (`99+` overflow). Pure.
 */
export function formatBadgeCount(count: number): string {
  const safe = Number.isFinite(count) && count > 0 ? Math.floor(count) : 0;
  return safe > 99 ? '99+' : String(safe);
}

/**
 * Relative timestamp (`just now`, `5m ago`, `2h ago`, `3d ago`, else the
 * ISO date part). `nowMs` is injectable for deterministic tests. Pure.
 */
export function formatRelativeTime(iso: string, nowMs?: number): string {
  if (iso === '') {
    return '';
  }
  const time = Date.parse(iso);
  if (Number.isNaN(time)) {
    return iso;
  }
  const now = nowMs ?? Date.now();
  const diffMs = now - time;
  if (!Number.isFinite(diffMs) || diffMs < 0) {
    return iso.slice(0, 10);
  }
  const minute = 60_000;
  const hour = 60 * minute;
  const day = 24 * hour;
  if (diffMs < minute) {
    return 'just now';
  }
  if (diffMs < hour) {
    const minutes = Math.floor(diffMs / minute);
    return `${String(minutes)}m ago`;
  }
  if (diffMs < day) {
    const hours = Math.floor(diffMs / hour);
    return `${String(hours)}h ago`;
  }
  if (diffMs < 30 * day) {
    const days = Math.floor(diffMs / day);
    return `${String(days)}d ago`;
  }
  return iso.slice(0, 10);
}

/** Non-color icon glyph per notification type (text, never color alone). Pure. */
export function iconForNotificationType(type: string): string {
  switch (type) {
    case 'ProcessingCompleted':
    case 'ReviewResolved':
    case 'ExportCompleted':
      return '✓';
    case 'ProcessingFailed':
    case 'ExportFailed':
      return '✕';
    case 'ManualReviewRequired':
    case 'UploadRejected':
    case 'QuotaWarning':
    case 'ProviderPolicyWarning':
      return '⚠';
    default:
      return '•';
  }
}

/** Lowercase testid key for a notification type. Pure. */
export function notificationTypeKey(type: string): string {
  const normalized = type.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  return normalized === '' ? 'unknown' : normalized;
}

/**
 * Dedupes raw notification ids (first occurrence wins, preserves order).
 * Used by the SSE invalidation path so duplicates never duplicate rows.
 * Pure.
 */
export function dedupeNotificationIds(ids: readonly string[]): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const id of ids) {
    if (id !== '' && !seen.has(id)) {
      seen.add(id);
      out.push(id);
    }
  }
  return out;
}

/** True when the value looks like an unread-count conflict (409). Pure. */
export function isUnreadCountConflict(error: { readonly code?: string; readonly status?: number } | undefined | null): boolean {
  if (error === undefined || error === null) {
    return false;
  }
  return error.status === 409;
}

/** True for deleted-target sentinels (test + tombstone ids). Pure. */
export function isDeletedTargetId(value: string | undefined): boolean {
  if (value === undefined || value === '') {
    return false;
  }
  const lowered = value.toLowerCase();
  return lowered === 'deleted' || lowered === 'gone' || lowered === 'missing';
}
