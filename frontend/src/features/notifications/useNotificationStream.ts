import { useEffect, useRef } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import type { SseEnvelope } from '../../api/client/index.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { useIsAuthenticated } from '../auth/useSession.js';
import { readSseEnabled } from '../../hooks/useProgressStream.js';
import { dedupeNotificationIds } from './types.js';

/**
 * SSE invalidation for the notification inbox (Task 034, R5).
 *
 * `notification.created` frames from the Task 026 stream are invalidation
 * hints only: they invalidate `queryKeys.notifications.list()` +
 * `queryKeys.notifications.unreadCount()` so the durable API refetches.
 * Duplicates and out-of-order frames never duplicate rows or inflate the
 * badge: notification ids are deduped id-keyed (first occurrence wins) and
 * the server count stays authoritative (the badge never increments locally).
 *
 * The Task 026 `useProgressStream` already routes `notification.created`
 * through `queryKeyRegistry`; this hook is the notification-scoped entry
 * point used by the Center and the shell badge. It subscribes to the shared
 * `notification.created` hint bus (a `CustomEvent` on `window`) so hermetic
 * tests and future global streams can drive invalidation without polling.
 */

export const NOTIFICATION_CREATED_EVENT = 'notifications:created';

export interface NotificationCreatedDetail {
  readonly notificationId?: string;
}

/**
 * Opaque id-keyed dedupe tracker for `notification.created` hints. Backend
 * ids are random GUIDs with no lexical order, so staleness is
 * equality-based (already-seen) and never timestamp-based. Pure container.
 */
export class NotificationDedupeTracker {
  private readonly seen = new Set<string>();

  public shouldProcess(notificationId: string | undefined): boolean {
    if (notificationId === undefined || notificationId === '') {
      return false;
    }
    return !this.seen.has(notificationId);
  }

  public markSeen(notificationId: string): void {
    if (notificationId !== '') {
      this.seen.add(notificationId);
    }
  }

  public hasSeen(notificationId: string): boolean {
    return this.seen.has(notificationId);
  }

  public get seenCount(): number {
    return this.seen.size;
  }

  public reset(): void {
    this.seen.clear();
  }
}

/** Creates an isolated dedupe tracker (one per hook instance). */
export function createNotificationDedupe(): NotificationDedupeTracker {
  return new NotificationDedupeTracker();
}

/**
 * Extracts the notification id from a validated `notification.created`
 * envelope. Reads only the allowlisted id fields (`notificationId`/`id`);
 * never reads transcript/media bodies. Pure.
 */
export function extractNotificationId(envelope: SseEnvelope): string | undefined {
  const payload = envelope.payload as Record<string, unknown>;
  const candidates: readonly unknown[] = [
    payload['notificationId'],
    payload['notification_id'],
    payload['id'],
  ];
  for (const candidate of candidates) {
    if (typeof candidate === 'string' && candidate !== '') {
      return candidate;
    }
  }
  return undefined;
}

/** Dedupes a batch of hint ids (first occurrence wins). Pure. */
export function dedupeHintIds(ids: readonly string[]): string[] {
  return dedupeNotificationIds(ids);
}

export interface UseNotificationStreamResult {
  readonly seenCount: number;
  /** Hint entry point: dedupes by id, then invalidates list + unread count. */
  readonly handleNotificationCreated: (notificationId: string | undefined) => void;
}

/**
 * Subscribes to `notification.created` hints. Each unique notification id
 * invalidates the list + unread keys once; duplicates are ignored. No
 * polling, no local row mirroring — the API refetch is the source of truth.
 */
export function useNotificationStream(): UseNotificationStreamResult {
  const queryClient = useQueryClient();
  const isAuthenticated = useIsAuthenticated();
  const trackerRef = useRef<NotificationDedupeTracker | null>(null);
  if (trackerRef.current === null) {
    trackerRef.current = createNotificationDedupe();
  }

  useEffect(() => {
    if (!isAuthenticated || !readSseEnabled()) {
      return;
    }
    const tracker = trackerRef.current;
    if (tracker === null) {
      return;
    }
    const onHint = (event: Event): void => {
      const detail = (event as CustomEvent<NotificationCreatedDetail>).detail;
      const notificationId = detail?.notificationId;
      if (!tracker.shouldProcess(notificationId)) {
        return;
      }
      if (notificationId !== undefined) {
        tracker.markSeen(notificationId);
      }
      void queryClient.invalidateQueries({ queryKey: queryKeys.notifications.list() });
      void queryClient.invalidateQueries({ queryKey: queryKeys.notifications.unreadCount() });
    };
    const target = typeof window !== 'undefined' ? window : undefined;
    target?.addEventListener(NOTIFICATION_CREATED_EVENT, onHint as EventListener);
    return () => {
      target?.removeEventListener(NOTIFICATION_CREATED_EVENT, onHint as EventListener);
    };
  }, [isAuthenticated, queryClient]);

  return {
    get seenCount(): number {
      return trackerRef.current?.seenCount ?? 0;
    },
    handleNotificationCreated: (notificationId: string | undefined): void => {
      const tracker = trackerRef.current;
      if (tracker === null || !isAuthenticated) {
        return;
      }
      if (!tracker.shouldProcess(notificationId)) {
        return;
      }
      if (notificationId !== undefined) {
        tracker.markSeen(notificationId);
      }
      void queryClient.invalidateQueries({ queryKey: queryKeys.notifications.list() });
      void queryClient.invalidateQueries({ queryKey: queryKeys.notifications.unreadCount() });
    },
  };
}

/**
 * Test/production bridge: emits a `notification.created` hint on the shared
 * bus. Never throws. Production Task 026 streams call the hook's handler
 * directly; tests dispatch through here to simulate duplicate/out-of-order
 * delivery.
 */
export function emitNotificationCreatedForTests(notificationId: string | undefined): void {
  try {
    const target = typeof window !== 'undefined' ? window : globalThis;
    const dispatch = (target as { dispatchEvent?: (e: Event) => boolean }).dispatchEvent;
    if (typeof dispatch === 'function') {
      dispatch.call(
        target,
        new CustomEvent<NotificationCreatedDetail>(NOTIFICATION_CREATED_EVENT, {
          detail: { notificationId },
        }),
      );
    }
  } catch {
    // Hint delivery must never break callers.
  }
}
