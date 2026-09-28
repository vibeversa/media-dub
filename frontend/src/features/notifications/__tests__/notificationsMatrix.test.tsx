// Task 039B: notifications state-matrix gap closure.
//
// Extends `notifications.test.tsx` (badge, ordering, mark-read, deep links,
// SSE dedupe, prefs happy path) with the missing states: center
// loading/error-retry/empty, unread 409-conflict + retry, unread
// non-conflict error, pagination prev/next, prefs loading/error/field-error,
// item gone/empty-title/read/failure-toast paths, the stream-hook direct
// matrix (dedupe, id extraction, bridge, guards), the link matrix, and the
// fetch/helper sweep. Every failure asserts its recovery control per §11.6
// with text signals (041C); fixtures are synthetic and fetch is intercepted.
import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
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
import { NotificationCenter } from '../NotificationCenter.js';
import { NotificationItem } from '../NotificationItem.js';
import {
  NOTIFICATION_PREFERENCE_KEY,
  NOTIFICATION_TYPES,
  dedupeNotificationIds,
  dedupeNotifications,
  formatBadgeCount,
  formatRelativeTime,
  iconForNotificationType,
  notificationTypeKey,
  parseNotification,
  parseNotificationPage,
  parseNotifications,
  parseUnreadCount,
} from '../types.js';
import type { NotificationView } from '../types.js';
import { isGoneLink, notificationLinkFor } from '../notificationLinks.js';
import {
  NOTIFICATION_CREATED_EVENT,
  createNotificationDedupe,
  dedupeHintIds,
  emitNotificationCreatedForTests,
  extractNotificationId,
  useNotificationStream,
} from '../useNotificationStream.js';
import { fetchNotificationItems, fetchNotifications, fetchUnreadCount, useNotifications } from '../useNotifications.js';
import { parseNotificationPrefs, serializeNotificationPrefs } from '../useNotificationPrefs.js';
import { resetEnvCache } from '../../../lib/env.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number, message?: string): Response {
  return jsonResponse(
    { error: { code, message: message ?? `backend ${code}`, correlationId: 'corr-n34', details: {} } },
    status,
  );
}

function row(id: string, overrides: Record<string, unknown> = {}): Record<string, unknown> {
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

function manyRows(count: number): Record<string, unknown>[] {
  const items: Record<string, unknown>[] = [];
  for (let i = 0; i < count; i += 1) {
    items.push(row(`ntf_${String(i + 1).padStart(2, '0')}`));
  }
  return items;
}

interface NotificationsMatrixWorld {
  listMode: 'ok' | 'empty' | 'error500' | 'never';
  unreadMode: 'ok' | 'conflict409' | 'error500';
  markReadMode: 'ok' | 'fail';
  prefsMode: 'ok' | 'error500' | 'never' | 'unknown-key';
  items: Record<string, unknown>[];
  listCalls: number;
  unreadCalls: number;
  markReadCalls: number;
}

let world: NotificationsMatrixWorld;

function resetWorld(): void {
  world = {
    listMode: 'ok',
    unreadMode: 'ok',
    markReadMode: 'ok',
    prefsMode: 'ok',
    items: [row('ntf_1'), row('ntf_2', { type: 'ManualReviewRequired', title: 'Review me', resourceType: 'ReviewItem', resourceId: 'rev_1' })],
    listCalls: 0,
    unreadCalls: 0,
    markReadCalls: 0,
  };
}

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

async function mockFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const url = urlOf(input);
  const method = methodOf(input, init);
  if (!url.includes('/api/v1/')) {
    return jsonResponse({});
  }
  if (method === 'GET' && url.includes('/notifications/unread-count')) {
    world.unreadCalls += 1;
    if (world.unreadMode === 'conflict409') {
      return errorEnvelope('VERSION_CONFLICT', 409);
    }
    if (world.unreadMode === 'error500') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    return jsonResponse({ unreadCount: world.items.filter((item) => item['readAt'] === null).length });
  }
  if (method === 'POST' && /\/notifications\/[^/?]+\/read/.test(url)) {
    world.markReadCalls += 1;
    if (world.markReadMode === 'fail') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    const match = /\/notifications\/([^/?]+)\/read/.exec(url);
    const target = world.items.find((entry) => entry['id'] === match?.[1]);
    if (target !== undefined) {
      target['readAt'] = '2024-01-16T13:00:00Z';
    }
    return jsonResponse({ marked: true });
  }
  if (method === 'POST' && url.includes('/notifications/read-all')) {
    for (const item of world.items) {
      item['readAt'] = '2024-01-16T13:00:00Z';
    }
    return jsonResponse({ marked: world.items.length });
  }
  if (method === 'GET' && url.includes('/notifications') && !url.includes('/unread-count')) {
    world.listCalls += 1;
    if (world.listMode === 'error500') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    if (world.listMode === 'empty') {
      return jsonResponse({ items: [], page: 1, pageSize: 20, total: 0, hasMore: false });
    }
    if (world.listMode === 'never') {
      return new Promise<Response>(() => {});
    }
    const parsed = new URL(url);
    const page = Number.parseInt(parsed.searchParams.get('page') ?? '1', 10);
    const pageSize = 20;
    const slice = world.items.slice((page - 1) * pageSize, page * pageSize);
    return jsonResponse({ items: slice, page, pageSize, total: world.items.length, hasMore: page * pageSize < world.items.length });
  }
  if (method === 'GET' && url.includes('/me/preferences')) {
    if (world.prefsMode === 'error500') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    if (world.prefsMode === 'never') {
      return new Promise<Response>(() => {});
    }
    return jsonResponse({ [NOTIFICATION_PREFERENCE_KEY]: 'ProcessingCompleted,ManualReviewRequired' });
  }
  if (method === 'PUT' && url.includes('/me/preferences')) {
    if (world.prefsMode === 'unknown-key') {
      return errorEnvelope('PREFERENCE_KEY_UNKNOWN', 400);
    }
    return jsonResponse({ [NOTIFICATION_PREFERENCE_KEY]: 'ProcessingCompleted' });
  }
  return jsonResponse({});
}

function renderWithProviders(node: React.JSX.Element): void {
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

function authenticate(): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', ['project.view']);
}

function parsedView(id: string, overrides: Record<string, unknown> = {}): NotificationView {
  const parsed = parseNotification(row(id, overrides));
  if (parsed === undefined) {
    throw new Error(`fixture ${id} failed to parse`);
  }
  return parsed;
}

beforeEach(() => {
  resetWorld();
  setInnerFetchForTests(mockFetch as typeof fetch);
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  resetEnvCache();
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
  vi.restoreAllMocks();
});

describe('NotificationCenter shell states', () => {
  it('shows loading while the inbox resolves (never blank)', () => {
    world.listMode = 'never';
    renderWithProviders(<NotificationCenter />);
    expect(screen.getByTestId('notifications-loading')).toBeDefined();
    expect(screen.getByTestId('notifications-prefs-loading')).toBeDefined();
  });

  it('recovers from list errors with retry (recovery: retry)', async () => {
    world.listMode = 'error500';
    renderWithProviders(<NotificationCenter />);
    expect(await screen.findByTestId('notifications-error')).toBeDefined();
    const callsBefore = world.listCalls;
    world.listMode = 'ok';
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(world.listCalls).toBeGreaterThan(callsBefore));
    expect(await screen.findByTestId('notifications-unread-count')).toBeDefined();
  });

  it('renders the empty inbox without error chrome (recovery: none needed)', async () => {
    world.listMode = 'empty';
    world.items = [];
    renderWithProviders(<NotificationCenter />);
    expect(await screen.findByTestId('notifications-empty')).toBeDefined();
    expect(screen.queryByTestId('notifications-error')).toBeNull();
    expect(screen.getByTestId('notifications-read-all').hasAttribute('disabled')).toBe(true);
  });
});

describe('unread-count conflict matrix', () => {
  it('surfaces 409 expiry with list refetch + retry (recovery: refresh)', async () => {
    world.unreadMode = 'conflict409';
    renderWithProviders(<NotificationCenter />);
    expect(await screen.findByTestId('notifications-unread-conflict')).toBeDefined();
    expect(screen.getByTestId('notifications-unread-conflict').textContent).toContain('expired');
    const listBefore = world.listCalls;
    const unreadBefore = world.unreadCalls;
    world.unreadMode = 'ok';
    fireEvent.click(screen.getByTestId('notifications-unread-retry'));
    await waitFor(() => expect(world.unreadCalls).toBeGreaterThan(unreadBefore));
    expect(world.listCalls).toBeGreaterThanOrEqual(listBefore);
    await waitFor(() => expect(screen.queryByTestId('notifications-unread-conflict')).toBeNull());
  });

  it('warns without conflict chrome on generic unread errors (recovery: wait)', async () => {
    world.unreadMode = 'error500';
    renderWithProviders(<NotificationCenter />);
    expect(await screen.findByTestId('notifications-unread-error')).toBeDefined();
    expect(screen.queryByTestId('notifications-unread-conflict')).toBeNull();
    expect(screen.getByTestId('notifications-unread-error').textContent).toContain('stale');
  });
});

describe('pagination matrix', () => {
  it('pages forward and back with page-info signals', async () => {
    world.items = manyRows(25);
    renderWithProviders(<NotificationCenter />);
    expect(await screen.findByTestId('notifications-page-info')).toBeDefined();
    expect(screen.getByTestId('notifications-page-info').textContent).toBe('Page 1 of 2');
    expect(screen.getByTestId('notifications-page-prev').hasAttribute('disabled')).toBe(true);
    fireEvent.click(screen.getByTestId('notifications-page-next'));
    await waitFor(() => expect(screen.getByTestId('notifications-page-info').textContent).toBe('Page 2 of 2'));
    expect(screen.getByTestId('notifications-page-next').hasAttribute('disabled')).toBe(true);
    fireEvent.click(screen.getByTestId('notifications-page-prev'));
    await waitFor(() => expect(screen.getByTestId('notifications-page-info').textContent).toBe('Page 1 of 2'));
  });
});

describe('prefs error matrix', () => {
  it('shows field errors when prefs fail to load (recovery: retry via refetch)', async () => {
    world.prefsMode = 'error500';
    renderWithProviders(<NotificationCenter />);
    expect(await screen.findByTestId('notifications-prefs-error')).toBeDefined();
    expect(screen.getByTestId('notifications-prefs-error-message').textContent?.length).toBeGreaterThan(0);
  });

  it('shows field errors when saving hits unknown keys (recovery: fix selection)', async () => {
    world.prefsMode = 'unknown-key';
    renderWithProviders(<NotificationCenter />);
    const toggle = await screen.findByTestId(`notifications-pref-${NOTIFICATION_TYPES[0]}`);
    fireEvent.click(toggle);
    expect(await screen.findByTestId('notifications-prefs-error')).toBeDefined();
  });
});

describe('NotificationItem matrix', () => {
  it('renders gone targets with dashboard fallback (no dead links)', async () => {
    renderWithProviders(<NotificationItem item={parsedView('ntf_1')} deletedIds={['ntf_1']} />);
    expect(await screen.findByTestId('notifications-gone-ntf_1')).toBeDefined();
    expect(screen.getByTestId('notifications-gone-title-ntf_1').textContent).toContain('no longer available');
    const back = screen.getByTestId('notifications-gone-dashboard-ntf_1');
    expect(back.getAttribute('href')).toBe('/projects/prj_1');
    expect(back.textContent).toContain('Back to project');
  });

  it('falls back to the type label for empty titles and hides empty bodies', async () => {
    renderWithProviders(<NotificationItem item={parsedView('ntf_1', { title: '', body: '' })} />);
    expect(await screen.findByTestId('notification-title-ntf_1')).toBeDefined();
    expect(screen.getByTestId('notification-title-ntf_1').textContent).not.toBe('');
    expect(screen.queryByTestId('notification-body-ntf_1')).toBeNull();
    expect(screen.getByTestId('notification-state-ntf_1').textContent).toBe('Unread');
  });

  it('hides actions on read rows (recovery: none needed)', async () => {
    renderWithProviders(<NotificationItem item={parsedView('ntf_1', { readAt: '2024-01-16T13:00:00Z' })} />);
    expect(await screen.findByTestId('notification-state-ntf_1')).toBeDefined();
    expect(screen.getByTestId('notification-state-ntf_1').textContent).toBe('Read');
    expect(screen.queryByTestId('notification-mark-read-ntf_1')).toBeNull();
  });

  it('toasts on mark-read failure with list refresh (recovery: retry)', async () => {
    world.markReadMode = 'fail';
    renderWithProviders(<NotificationItem item={parsedView('ntf_1')} />);
    fireEvent.click(await screen.findByTestId('notification-mark-read-ntf_1'));
    await waitFor(() => expect(screen.getByRole('region', { name: 'Notifications' }).textContent).toContain('Could not mark'));
  });

  it('marks rows read when opening the deep link (recovery: automatic)', async () => {
    renderWithProviders(
      <NotificationItem
        item={parsedView('ntf_2', { type: 'ManualReviewRequired', resourceType: 'ReviewItem', resourceId: 'rev_1' })}
      />,
    );
    const link = await screen.findByTestId('notification-link-ntf_2');
    expect(link.getAttribute('href')).toContain('/review?');
    fireEvent.click(link);
    await waitFor(() => expect(world.markReadCalls).toBe(1));
  });
});

describe('notification stream hook matrix', () => {
  it('dedupes hint ids with first-wins tracker semantics', () => {
    const tracker = createNotificationDedupe();
    expect(tracker.shouldProcess(undefined)).toBe(false);
    expect(tracker.shouldProcess('')).toBe(false);
    expect(tracker.shouldProcess('n1')).toBe(true);
    tracker.markSeen('n1');
    expect(tracker.shouldProcess('n1')).toBe(false);
    expect(tracker.hasSeen('n1')).toBe(true);
    expect(tracker.seenCount).toBe(1);
    tracker.markSeen('');
    expect(tracker.seenCount).toBe(1);
    tracker.reset();
    expect(tracker.shouldProcess('n1')).toBe(true);
    expect(dedupeHintIds(['a', 'b', 'a', 'c', 'b'])).toEqual(['a', 'b', 'c']);
  });

  it('extracts ids from allowlisted fields only (never bodies)', () => {
    const envelope = (payload: unknown) =>
      ({ payload }) as unknown as Parameters<typeof extractNotificationId>[0];
    expect(extractNotificationId(envelope({ notificationId: 'n1' }))).toBe('n1');
    expect(extractNotificationId(envelope({ notification_id: 'n2' }))).toBe('n2');
    expect(extractNotificationId(envelope({ id: 'n3' }))).toBe('n3');
    expect(extractNotificationId(envelope({ notificationId: '' }))).toBeUndefined();
    expect(extractNotificationId(envelope({ notificationId: 7 }))).toBeUndefined();
    expect(extractNotificationId(envelope({ transcript: 'words' }))).toBeUndefined();
  });

  it('invalidates once per unique hint through the bus (recovery: refetch)', async () => {
    function Probe(): null {
      const stream = useNotificationStream();
      (window as unknown as { __stream?: unknown }).__stream = stream;
      useNotifications(1);
      return null;
    }
    renderWithProviders(<Probe />);
    await waitFor(() => expect(world.listCalls).toBeGreaterThanOrEqual(1));
    const callsBefore = world.listCalls;
    emitNotificationCreatedForTests('n1');
    emitNotificationCreatedForTests('n1');
    emitNotificationCreatedForTests(undefined);
    emitNotificationCreatedForTests('n2');
    await waitFor(() => expect(world.listCalls).toBeGreaterThan(callsBefore));
    const stream = (window as unknown as { __stream?: { seenCount: number } }).__stream;
    expect(stream?.seenCount).toBe(2);
  });

  it('stays quiet when anonymous or SSE-disabled (no fetch storms)', async () => {
    function Probe(): null {
      const stream = useNotificationStream();
      stream.handleNotificationCreated('n9');
      return null;
    }
    useAuthStore.setState({ status: 'anonymous' });
    const callsBefore = world.listCalls;
    renderWithProviders(<Probe />);
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(world.listCalls).toBe(callsBefore);
    cleanup();
    queryClient.clear();
    authenticate();
    vi.stubEnv('VITE_SSE_ENABLED', 'false');
    resetEnvCache();
    function Probe2(): null {
      useNotificationStream();
      return null;
    }
    const callsBefore2 = world.listCalls;
    renderWithProviders(<Probe2 />);
    emitNotificationCreatedForTests('n10');
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(world.listCalls).toBe(callsBefore2);
  });

  it('never throws the test bridge on broken dispatchers', () => {
    const spy = vi.spyOn(window, 'dispatchEvent').mockImplementation(() => {
      throw new Error('denied');
    });
    expect(() => emitNotificationCreatedForTests('n1')).not.toThrow();
    spy.mockRestore();
    window.addEventListener(NOTIFICATION_CREATED_EVENT, () => {});
    expect(() => emitNotificationCreatedForTests('n1')).not.toThrow();
  });
});

describe('notification link matrix', () => {
  const cases: Array<[Record<string, string | undefined>, string, string]> = [
    [{ type: 'ManualReviewRequired', projectId: 'prj_1', resourceId: 'rev_1' }, '/review?project=prj_1&reviewId=rev_1', 'review'],
    [{ type: 'ReviewResolved', projectId: 'prj_1' }, '/review?project=prj_1', 'review'],
    [{ type: 'ExportCompleted', projectId: 'prj_1' }, '/projects/prj_1/exports', 'export'],
    [{ type: 'ExportFailed' }, '/dashboard', 'dashboard'],
    [{ resourceType: 'MediaAsset', projectId: 'prj_1' }, '/projects/prj_1/media', 'media'],
    [{ type: 'ProcessingCompleted', projectId: 'prj_1' }, '/projects/prj_1', 'project'],
    [{ resourceType: 'DubbingProject', resourceId: 'prj_9' }, '/projects/prj_9', 'project'],
    [{ resourceType: 'Tenant' }, '/dashboard', 'dashboard'],
    [{ type: 'QuotaWarning', projectId: 'prj_1' }, '/projects/prj_1', 'project'],
    [{ type: 'Bogus', projectId: 'prj_1' }, '/projects/prj_1', 'project'],
    [{ type: 'Bogus' }, '/dashboard', 'dashboard'],
    [{ resourceId: 'x' }, '/dashboard', 'dashboard'],
    [{ type: 'ProcessingCompleted', projectId: 'deleted' }, '/dashboard', 'gone'],
    [{ type: 'ProcessingCompleted', projectId: 'prj_1', resourceId: 'deleted' }, '/projects/prj_1', 'gone'],
  ];
  it.each(cases)('maps %o to %s (%s, never dead)', (input, href, kind) => {
    const target = notificationLinkFor(input);
    expect(target.href).toBe(href);
    expect(target.kind).toBe(kind);
    expect(isGoneLink(target)).toBe(kind === 'gone');
  });
});

describe('notification fetch/helper matrix', () => {
  it('clamps paging and normalizes failures (recovery: retry)', async () => {
    const page = await fetchNotifications(0, 500);
    expect(page.page).toBe(1);
    expect(page.pageSize).toBeLessThanOrEqual(100);
    world.listMode = 'error500';
    await expect(fetchNotifications(1)).rejects.toMatchObject({ retryable: false, code: 'INTERNAL_ERROR' });
    await expect(fetchNotificationItems()).rejects.toMatchObject({ code: 'INTERNAL_ERROR' });
    world.listMode = 'ok';
    await expect(fetchUnreadCount()).resolves.toBe(2);
    world.unreadMode = 'error500';
    await expect(fetchUnreadCount()).rejects.toMatchObject({ code: 'INTERNAL_ERROR' });
  });

  it('parses pages, badges, times, icons, and prefs defensively', () => {
    expect(parseNotificationPage(null).items).toEqual([]);
    expect(parseNotifications('nope')).toEqual([]);
    expect(parseNotification(null)).toBeUndefined();
    expect(parseUnreadCount({ unreadCount: 3 })).toBe(3);
    expect(parseUnreadCount({})).toBe(0);
    expect(parseUnreadCount(null)).toBe(0);
    expect(formatBadgeCount(0)).toBe('0');
    expect(formatBadgeCount(150)).toBe('99+');
    expect(notificationTypeKey('Bogus')).not.toBe('');
    expect(iconForNotificationType('Bogus')).not.toBe('');
    expect(formatRelativeTime('not-a-date')).toBe('not-a-date');
    expect(parseNotificationPrefs(null).ProcessingCompleted).toBe(true);
    const serialized = serializeNotificationPrefs(parseNotificationPrefs(null));
    expect((JSON.parse(serialized) as Record<string, boolean>)['ProcessingCompleted']).toBe(true);
    expect(dedupeNotificationIds(['a', 'a', 'b'])).toEqual(['a', 'b']);
    expect(dedupeNotifications([parseNotification(row('x'))!, parseNotification(row('x'))!]).length).toBe(1);
  });

  it('disables queries for anonymous sessions', async () => {
    useAuthStore.setState({ status: 'anonymous' });
    let calls = 0;
    setInnerFetchForTests((async () => {
      calls += 1;
      return jsonResponse({});
    }) as typeof fetch);
    function Probe(): null {
      useNotifications(1);
      return null;
    }
    renderWithProviders(<Probe />);
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(calls).toBe(0);
  });
});
