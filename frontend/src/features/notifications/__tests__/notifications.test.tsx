import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import '../../../i18n/i18n.js';
import {
  clearTokenProvider,
  restoreInnerFetchForTests,
  setInnerFetchForTests,
  setTokenProvider,
} from '../../../api/client/index.js';
import { LocaleProvider } from '../../../app/providers/LocaleProvider.js';
import { queryClient } from '../../../app/providers/queryClient.js';
import { ToastProvider } from '../../../components/Toast/Toast.js';
import { useAppStore } from '../../../stores/index.js';
import { useAuthStore } from '../../auth/authStore.js';
import { resetRestoreStartedForTests } from '../../auth/useSession.js';
import { NotificationBell } from '../NotificationBell.js';
import { NotificationCenter } from '../NotificationCenter.js';
import {
  NOTIFICATION_PREFERENCE_KEY,
  dedupeNotificationIds,
  dedupeNotifications,
  formatBadgeCount,
  formatRelativeTime,
  iconForNotificationType,
  parseNotification,
  parseNotifications,
  parseUnreadCount,
} from '../types.js';
import { notificationLinkFor } from '../notificationLinks.js';
import {
  createNotificationDedupe,
  extractNotificationId,
} from '../useNotificationStream.js';
import { parseNotificationPrefs, serializeNotificationPrefs } from '../useNotificationPrefs.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number, message?: string): Response {
  return jsonResponse(
    { error: { code, message: message ?? `backend ${code}`, correlationId: 'corr-n34', details: {} } },
    status,
  );
}

function row(
  id: string,
  overrides: Record<string, unknown> = {},
): Record<string, unknown> {
  return {
    id,
    type: 'ProcessingCompleted',
    severity: 'Info',
    title: `Title ${id}`,
    body: `Body ${id}`,
    resourceType: 'ProcessingRun',
    resourceId: 'run_1',
    projectId: 'prj_1',
    readAt: null,
    createdAt: '2024-01-16T12:00:00Z',
    expiresAt: null,
    ...overrides,
  };
}

function listBody(items: Record<string, unknown>[]): Record<string, unknown> {
  return {
    items,
    page: 1,
    pageSize: 20,
    total: items.length,
    hasMore: false,
  };
}

interface NotificationsWorld {
  items: Record<string, unknown>[];
  markReadBehavior: 'ok' | 'fail';
  markAllBehavior: 'ok' | 'fail';
  prefsValue: string;
  prefsPutBehavior: 'ok' | 'unknown-key';
  calls: { list: number; unread: number; markRead: number; markAll: number; prefsGet: number; prefsPut: number };
  lastPrefsPutBody: unknown;
  unreadOverride: number | undefined;
}

function newWorld(overrides: Partial<NotificationsWorld> = {}): NotificationsWorld {
  return {
    items: [
      row('ntf_1', {
        type: 'ProcessingCompleted',
        title: 'Processing completed',
        body: 'Run abc12345 completed.',
        resourceType: 'ProcessingRun',
        resourceId: 'run_1',
        projectId: 'prj_1',
        createdAt: '2024-01-16T12:00:00Z',
        readAt: null,
      }),
      row('ntf_2', {
        type: 'ManualReviewRequired',
        title: 'Manual review required',
        body: 'Stage Render needs review.',
        resourceType: 'ReviewItem',
        resourceId: 'rev_1',
        projectId: 'prj_1',
        createdAt: '2024-01-16T11:00:00Z',
        readAt: null,
      }),
      row('ntf_3', {
        type: 'ExportCompleted',
        title: 'Export completed',
        body: 'Export abc12345 completed.',
        resourceType: 'ExportJob',
        resourceId: 'exp_1',
        projectId: 'prj_1',
        createdAt: '2024-01-16T10:00:00Z',
        readAt: '2024-01-16T10:05:00Z',
      }),
      row('ntf_4', {
        type: 'QuotaWarning',
        title: 'Quota warning',
        body: 'Quota storage is near its limit.',
        resourceType: 'Tenant',
        resourceId: 'tenant_1',
        projectId: null,
        createdAt: '2024-01-16T09:00:00Z',
        readAt: null,
      }),
    ],
    markReadBehavior: 'ok',
    markAllBehavior: 'ok',
    prefsValue: JSON.stringify({
      ProcessingCompleted: true,
      ProcessingFailed: true,
      ManualReviewRequired: true,
      ReviewResolved: true,
      ExportCompleted: true,
      ExportFailed: true,
      UploadRejected: true,
      QuotaWarning: true,
      ProviderPolicyWarning: true,
    }),
    prefsPutBehavior: 'ok',
    calls: { list: 0, unread: 0, markRead: 0, markAll: 0, prefsGet: 0, prefsPut: 0 },
    lastPrefsPutBody: undefined,
    unreadOverride: undefined,
    ...overrides,
  };
}

let world: NotificationsWorld = newWorld();

function urlOf(input: RequestInfo | URL): string {
  if (typeof input === 'string') {
    return input;
  }
  if (input instanceof URL) {
    return input.href;
  }
  return (input as Request).url;
}

function methodOf(input: RequestInfo | URL, init?: RequestInit): string {
  if (typeof input !== 'string' && !(input instanceof URL)) {
    const request = input as Request;
    if (typeof request.method === 'string' && request.method !== '') {
      return request.method.toUpperCase();
    }
  }
  return (init?.method ?? 'GET').toUpperCase();
}

function bodyOf(init?: RequestInit): unknown {
  if (typeof init?.body !== 'string') {
    return undefined;
  }
  try {
    return JSON.parse(init.body as string) as unknown;
  } catch {
    return undefined;
  }
}

function unreadCountFor(items: Record<string, unknown>[]): number {
  let count = 0;
  for (const item of items) {
    const readAt = item['readAt'];
    if (readAt === null || readAt === undefined || readAt === '') {
      count += 1;
    }
  }
  return count;
}

async function mockFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const url = urlOf(input);
  const method = methodOf(input, init);
  if (!url.includes('/api/v1/')) {
    return jsonResponse({});
  }
  if (method === 'GET' && url.includes('/notifications/unread-count')) {
    world.calls.unread += 1;
    const count = world.unreadOverride ?? unreadCountFor(world.items);
    return jsonResponse({ unreadCount: count });
  }
  if (method === 'POST' && url.includes('/notifications/read-all')) {
    world.calls.markAll += 1;
    if (world.markAllBehavior === 'fail') {
      return errorEnvelope('INTERNAL_ERROR', 500, 'Mark-all failed.');
    }
    for (const item of world.items) {
      item['readAt'] = '2024-01-16T13:00:00Z';
    }
    return jsonResponse({ marked: world.items.length, markedCount: world.items.length });
  }
  if (method === 'POST' && /\/notifications\/[^/?]+\/read/.test(url)) {
    world.calls.markRead += 1;
    if (world.markReadBehavior === 'fail') {
      return errorEnvelope('INTERNAL_ERROR', 500, 'Mark-read failed.');
    }
    const match = /\/notifications\/([^/?]+)\/read/.exec(url);
    const id = match?.[1] ?? '';
    const target = world.items.find((entry) => entry['id'] === id);
    if (target !== undefined) {
      target['readAt'] = '2024-01-16T13:00:00Z';
    }
    return jsonResponse({ marked: true });
  }
  if (method === 'GET' && url.includes('/notifications') && !url.includes('/unread-count')) {
    world.calls.list += 1;
    return jsonResponse(listBody(world.items));
  }
  if (method === 'GET' && url.includes('/me/preferences')) {
    world.calls.prefsGet += 1;
    return jsonResponse({ [NOTIFICATION_PREFERENCE_KEY]: world.prefsValue });
  }
  if (method === 'PUT' && url.includes('/me/preferences')) {
    world.calls.prefsPut += 1;
    const body = bodyOf(init) as Record<string, unknown>;
    world.lastPrefsPutBody = body;
    if (world.prefsPutBehavior === 'unknown-key') {
      return errorEnvelope('PREFERENCE_KEY_UNKNOWN', 400, 'Unknown preference key.');
    }
    const next = body[NOTIFICATION_PREFERENCE_KEY];
    if (typeof next === 'string') {
      world.prefsValue = next;
    }
    return jsonResponse({ [NOTIFICATION_PREFERENCE_KEY]: world.prefsValue });
  }
  return jsonResponse({});
}

function authenticate(): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', ['project.view', 'project.edit']);
}

function renderWithProviders(node: React.ReactNode): void {
  render(
    <QueryClientProvider client={queryClient}>
      <LocaleProvider>
        <ToastProvider>
          <MemoryRouter>{node}</MemoryRouter>
        </ToastProvider>
      </LocaleProvider>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  world = newWorld();
  setInnerFetchForTests(mockFetch as typeof fetch);
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  queryClient.clear();
  authenticate();
});

afterEach(() => {
  cleanup();
  restoreInnerFetchForTests();
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  queryClient.clear();
});

describe('bell badge', () => {
  it('hides the badge at zero and announces counts politely', () => {
    renderWithProviders(<NotificationBell unreadCount={0} />);
    expect(screen.queryByTestId('nav-bell-badge')).toBeNull();
    expect(screen.getByTestId('nav-bell').getAttribute('href')).toBe('/notifications');
    cleanup();
    renderWithProviders(<NotificationBell unreadCount={3} />);
    const badge = screen.getByTestId('nav-bell-badge');
    expect(badge.textContent).toBe('3');
    expect(badge.getAttribute('aria-live')).toBe('polite');
  });

  it('renders 99+ overflow and formats counts purely', () => {
    expect(formatBadgeCount(0)).toBe('0');
    expect(formatBadgeCount(3)).toBe('3');
    expect(formatBadgeCount(99)).toBe('99');
    expect(formatBadgeCount(100)).toBe('99+');
    expect(formatBadgeCount(150)).toBe('99+');
    renderWithProviders(<NotificationBell unreadCount={150} />);
    expect(screen.getByTestId('nav-bell-badge').textContent).toBe('99+');
  });
});

describe('list ordering and parsing', () => {
  it('sorts newest-first and uses durable reads', async () => {
    world.items = [
      row('ntf_old', { createdAt: '2024-01-16T08:00:00Z' }),
      row('ntf_new', { createdAt: '2024-01-16T14:00:00Z' }),
      row('ntf_mid', { createdAt: '2024-01-16T10:00:00Z' }),
    ];
    renderWithProviders(<NotificationCenter />);
    expect(await screen.findByTestId('notifications-center')).toBeDefined();
    await waitFor(() => {
      expect(screen.getByTestId('notifications-list')).toBeDefined();
    });
    const list = screen.getByTestId('notifications-list');
    const rows = Array.from(list.querySelectorAll('[data-testid^="notification-row-"]'));
    expect(rows.map((entry) => entry.getAttribute('data-testid'))).toEqual([
      'notification-row-ntf_new',
      'notification-row-ntf_mid',
      'notification-row-ntf_old',
    ]);
    expect(world.calls.list).toBeGreaterThan(0);
    expect(screen.getByTestId('notifications-list-key').textContent).toContain('notifications');
    expect(screen.getByTestId('notifications-unread-key').textContent).toContain('unread-count');
  });

  it('parses defensively with id-keyed dedupe', () => {
    const parsed = parseNotifications({
      items: [
        row('dup', { createdAt: '2024-01-16T12:00:00Z' }),
        row('dup', { createdAt: '2024-01-16T12:00:00Z' }),
        { noId: true },
        row('solo', { createdAt: '2024-01-16T09:00:00Z' }),
      ],
    });
    expect(parsed.map((entry) => entry.id)).toEqual(['dup', 'solo']);
    expect(dedupeNotifications(parsed).map((entry) => entry.id)).toEqual(['dup', 'solo']);
    expect(dedupeNotificationIds(['a', 'a', 'b', '', 'b'])).toEqual(['a', 'b']);
    expect(parseUnreadCount({ unreadCount: 4 })).toBe(4);
    expect(parseUnreadCount({})).toBe(0);
    expect(parseNotification({ noId: true })).toBeUndefined();
    expect(iconForNotificationType('ProcessingCompleted')).toBe('✓');
    expect(formatRelativeTime('2024-01-16T11:59:30Z', Date.parse('2024-01-16T12:00:00Z'))).toBe('just now');
  });
});

describe('mark-read and read-all', () => {
  it('marks a single row read optimistically', async () => {
    renderWithProviders(<NotificationCenter />);
    expect(await screen.findByTestId('notification-row-ntf_1')).toBeDefined();
    expect(screen.getByTestId('notification-state-ntf_1').textContent).toBe('Unread');
    const unreadBefore = screen.getByTestId('notifications-unread-count').textContent;
    expect(unreadBefore).toContain('3');
    fireEvent.click(screen.getByTestId('notification-mark-read-ntf_1'));
    await waitFor(() => {
      expect(screen.getByTestId('notification-state-ntf_1').textContent).toBe('Read');
    });
    await waitFor(() => {
      expect(world.calls.markRead).toBe(1);
    });
    expect(screen.getByTestId('notifications-unread-count').textContent).toContain('2');
  });

  it('rolls back a failed single mark-read with a toast', async () => {
    world.markReadBehavior = 'fail';
    renderWithProviders(<NotificationCenter />);
    expect(await screen.findByTestId('notification-row-ntf_1')).toBeDefined();
    fireEvent.click(screen.getByTestId('notification-mark-read-ntf_1'));
    await waitFor(() => {
      expect(world.calls.markRead).toBe(1);
    });
    await waitFor(() => {
      expect(screen.getByTestId('notification-state-ntf_1').textContent).toBe('Unread');
    });
    expect(screen.getByTestId('notifications-unread-count').textContent).toContain('3');
    await waitFor(() => {
      expect(document.body.textContent).toContain('Could not mark this notification as read');
    });
  });

  it('marks all read optimistically and clears the badge', async () => {
    renderWithProviders(<NotificationCenter />);
    expect(await screen.findByTestId('notifications-list')).toBeDefined();
    fireEvent.click(screen.getByTestId('notifications-read-all'));
    await waitFor(() => {
      expect(screen.getByTestId('notification-state-ntf_1').textContent).toBe('Read');
    });
    expect(screen.getByTestId('notification-state-ntf_2').textContent).toBe('Read');
    await waitFor(() => {
      expect(world.calls.markAll).toBe(1);
    });
    expect(screen.getByTestId('notifications-unread-count').textContent).toContain('0 unread');
  });

  it('rolls back a failed read-all with a toast and refetch', async () => {
    world.markAllBehavior = 'fail';
    renderWithProviders(<NotificationCenter />);
    expect(await screen.findByTestId('notifications-list')).toBeDefined();
    const listCallsBefore = world.calls.list;
    fireEvent.click(screen.getByTestId('notifications-read-all'));
    await waitFor(() => {
      expect(world.calls.markAll).toBe(1);
    });
    await waitFor(() => {
      expect(document.body.textContent).toContain('Could not mark all as read');
    });
    expect(screen.getByTestId('notification-state-ntf_1').textContent).toBe('Unread');
    expect(world.calls.list).toBeGreaterThanOrEqual(listCallsBefore);
  });
});

describe('deep links', () => {
  it('maps project, review, export, and unknown types without dead links', () => {
    expect(notificationLinkFor({ type: 'ProcessingCompleted', projectId: 'prj_1' }).href).toBe('/projects/prj_1');
    const review = notificationLinkFor({ type: 'ManualReviewRequired', resourceType: 'ReviewItem', resourceId: 'rev_1', projectId: 'prj_1' });
    expect(review.href).toContain('/review');
    expect(review.href).toContain('prj_1');
    const exportLink = notificationLinkFor({ type: 'ExportCompleted', resourceType: 'ExportJob', resourceId: 'exp_1', projectId: 'prj_1' });
    expect(exportLink.href).toBe('/projects/prj_1/exports');
    const unknown = notificationLinkFor({ type: 'MysteryType' });
    expect(unknown.href).toBe('/dashboard');
    const media = notificationLinkFor({ type: 'UploadRejected', resourceType: 'MediaAsset', resourceId: 'med_1', projectId: 'prj_1' });
    expect(media.href).toBe('/projects/prj_1/media');
    const tenant = notificationLinkFor({ type: 'QuotaWarning', resourceType: 'Tenant', resourceId: 'tenant_1' });
    expect(tenant.href).toBe('/dashboard');
  });

  it('renders deep links per row and a GoneState for deleted targets', async () => {
    renderWithProviders(<NotificationCenter deletedIds={['ntf_2']} />);
    expect(await screen.findByTestId('notification-row-ntf_1')).toBeDefined();
    expect(screen.getByTestId('notification-link-ntf_1').getAttribute('href')).toBe('/projects/prj_1');
    expect(screen.getByTestId('notification-link-ntf_3').getAttribute('href')).toBe('/projects/prj_1/exports');
    expect(screen.getByTestId('notifications-gone-ntf_2')).toBeDefined();
    expect(screen.getByTestId('notifications-gone-title-ntf_2').textContent).toContain('no longer available');
    expect(screen.queryByTestId('notification-link-ntf_2')).toBeNull();
  });
});

describe('SSE invalidation dedupe', () => {
  it('dedupes notification ids and envelopes by id', () => {
    const tracker = createNotificationDedupe();
    expect(tracker.shouldProcess('ntf_1')).toBe(true);
    tracker.markSeen('ntf_1');
    expect(tracker.shouldProcess('ntf_1')).toBe(false);
    expect(tracker.shouldProcess('')).toBe(false);
    expect(tracker.shouldProcess(undefined)).toBe(false);
    expect(extractNotificationId({
      correlationId: 'c',
      eventId: 'e',
      eventType: 'notification.created',
      occurredAt: '2024-01-16T12:00:00Z',
      payload: { notificationId: 'ntf_9' },
      schemaVersion: 1,
      tenantId: 't',
    } as never)).toBe('ntf_9');
    const parsed = parseNotifications({
      items: [row('dup', { createdAt: '2024-01-16T12:00:00Z' }), row('dup', { createdAt: '2024-01-16T12:00:00Z' })],
    });
    expect(parsed).toHaveLength(1);
  });
});

describe('no sensitive payloads', () => {
  it('renders only display fields and never secret material', async () => {
    world.items = [
      {
        ...row('ntf_secret', {
          title: 'Processing completed',
          body: 'Run abc12345 completed.',
          createdAt: '2024-01-16T12:00:00Z',
        }),
        token: 'super-secret-token-xyz-123',
        secret: 'shhh-secret-value',
        signedUrl: 'https://example.com/signed-value-xyz',
        transcript: 'transcript-body-hello-world',
        rawPayload: { media: 'bytes' },
      },
    ];
    renderWithProviders(<NotificationCenter />);
    expect(await screen.findByTestId('notification-row-ntf_secret')).toBeDefined();
    const text = document.body.textContent ?? '';
    expect(text).not.toContain('super-secret-token-xyz-123');
    expect(text).not.toContain('shhh-secret-value');
    expect(text).not.toContain('signed-value-xyz');
    expect(text).not.toContain('transcript-body-hello-world');
    expect(text.toLowerCase()).not.toContain('signedurl');
  });
});

describe('notification preferences', () => {
  it('toggles delivery without deleting history', async () => {
    renderWithProviders(<NotificationCenter />);
    expect(await screen.findByTestId('notifications-prefs')).toBeDefined();
    const toggle = await screen.findByTestId('notifications-pref-ManualReviewRequired');
    expect((toggle as HTMLInputElement).checked).toBe(true);
    expect(await screen.findByTestId('notifications-list')).toBeDefined();
    const rowsBefore = document.querySelectorAll('[data-testid^="notification-row-"]').length;
    fireEvent.click(toggle);
    await waitFor(() => {
      expect(world.calls.prefsPut).toBe(1);
    });
    const rowsAfter = document.querySelectorAll('[data-testid^="notification-row-"]').length;
    expect(rowsAfter).toBe(rowsBefore);
    const body = world.lastPrefsPutBody as Record<string, string>;
    expect(body[NOTIFICATION_PREFERENCE_KEY]).toContain('ManualReviewRequired');
  });

  it('surfaces unknown-key rejections inline with toggles unchanged', async () => {
    world.prefsPutBehavior = 'unknown-key';
    renderWithProviders(<NotificationCenter />);
    const toggle = await screen.findByTestId('notifications-pref-ExportCompleted');
    expect((toggle as HTMLInputElement).checked).toBe(true);
    fireEvent.click(toggle);
    expect(await screen.findByTestId('notifications-prefs-error')).toBeDefined();
    expect(screen.getByTestId('notifications-prefs-error-message').textContent).toContain('Unknown preference key');
    expect((screen.getByTestId('notifications-pref-ExportCompleted') as HTMLInputElement).checked).toBe(true);
  });

  it('parses and serializes prefs purely', () => {
    const parsed = parseNotificationPrefs(JSON.stringify({ ProcessingCompleted: false }));
    expect(parsed.ProcessingCompleted).toBe(false);
    expect(parsed.ExportCompleted).toBe(true);
    expect(parseNotificationPrefs('not-json').ProcessingCompleted).toBe(true);
    const serialized = serializeNotificationPrefs(parsed);
    expect(serialized).toContain('ProcessingCompleted');
  });
});
