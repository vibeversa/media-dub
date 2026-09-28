// Delta 2: notifications remaining-branch closure.
//
// Supplements notificationsMatrix with NotificationItem gone-link variants
// (resource-deleted without tombstones, dashboard fallback copy),
// already-read link no-ops, deep-link mark-read failure toasts, and the
// useNotifications fetch/invalidation branches (item-list success,
// unread success, signal forwarding, page clamping, anonymous guards).
// Synthetic fixtures, fetch intercepted, text signals only.
//
// Intentional-exclusion candidate: `handleMarkRead`'s already-read guard
// is unreachable via UI (the button only renders for unread rows); the
// symmetric `handleOpen` guard is covered below through the always-rendered
// deep link.
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
import { NotificationItem } from '../NotificationItem.js';
import { parseNotification } from '../types.js';
import type { NotificationView } from '../types.js';
import {
  fetchNotificationItems,
  fetchNotifications,
  fetchUnreadCount,
  invalidateNotifications,
  useNotifications,
} from '../useNotifications.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number): Response {
  return jsonResponse(
    { error: { code, message: `backend ${code}`, correlationId: 'corr-n34b', details: {} } },
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

interface World {
  markReadMode: 'ok' | 'fail';
  listMode: 'ok' | 'error500';
  markReadCalls: number;
}

let world: World;

function resetWorld(): void {
  world = { markReadMode: 'ok', listMode: 'ok', markReadCalls: 0 };
}

function urlOf(input: RequestInfo | URL): string {
  if (typeof input === 'string') return input;
  if (input instanceof URL) return input.href;
  return (input as Request).url;
}

function methodOf(input: RequestInfo | URL, init?: RequestInit): string {
  if (typeof input !== 'string' && !(input instanceof URL)) {
    const request = input as Request;
    if (typeof request.method === 'string' && request.method !== '') return request.method.toUpperCase();
  }
  return (init?.method ?? 'GET').toUpperCase();
}

async function mockFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const url = urlOf(input);
  const method = methodOf(input, init);
  if (!url.includes('/api/v1/')) return jsonResponse({});
  if (method === 'GET' && url.includes('/notifications/unread-count')) {
    return jsonResponse({ unreadCount: 2 });
  }
  if (method === 'POST' && /\/notifications\/[^/?]+\/read/.test(url)) {
    world.markReadCalls += 1;
    if (world.markReadMode === 'fail') return errorEnvelope('INTERNAL_ERROR', 500);
    return jsonResponse({ marked: true });
  }
  if (method === 'GET' && url.includes('/notifications')) {
    if (world.listMode === 'error500') return errorEnvelope('INTERNAL_ERROR', 500);
    return jsonResponse({ items: [row('ntf_1'), row('ntf_2')], page: 1, pageSize: 20, total: 2, hasMore: false });
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
  if (parsed === undefined) throw new Error(`fixture ${id} failed to parse`);
  return parsed;
}

beforeEach(() => {
  resetWorld();
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
  vi.restoreAllMocks();
});

describe('NotificationItem gone-link variants', () => {
  it('treats deleted resource ids as gone without tombstones', async () => {
    renderWithProviders(<NotificationItem item={parsedView('ntf_1', { resourceId: 'deleted' })} />);
    expect(await screen.findByTestId('notifications-gone-ntf_1')).toBeDefined();
    expect(screen.queryByTestId('notification-link-ntf_1')).toBeNull();
    expect(screen.queryByTestId('notification-mark-read-ntf_1')).toBeNull();
  });

  it('falls back to dashboard copy for project-less gone targets', async () => {
    renderWithProviders(
      <NotificationItem item={parsedView('ntf_9', { type: 'ExportFailed', projectId: undefined })} deletedIds={['ntf_9']} />,
    );
    expect(await screen.findByTestId('notifications-gone-ntf_9')).toBeDefined();
    const back = screen.getByTestId('notifications-gone-dashboard-ntf_9');
    expect(back.getAttribute('href')).toBe('/dashboard');
    expect(back.textContent).toContain('Back to dashboard');
  });

  it('keeps project copy for project-scoped gone targets', async () => {
    renderWithProviders(<NotificationItem item={parsedView('ntf_1')} deletedIds={['ntf_1']} />);
    const back = await screen.findByTestId('notifications-gone-dashboard-ntf_1');
    expect(back.textContent).toContain('Back to project');
  });
});

describe('NotificationItem read-state guards', () => {
  it('leaves already-read rows untouched when opening links (no storm)', async () => {
    renderWithProviders(<NotificationItem item={parsedView('ntf_1', { readAt: '2024-01-16T13:00:00Z' })} />);
    const link = await screen.findByTestId('notification-link-ntf_1');
    fireEvent.click(link);
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(world.markReadCalls).toBe(0);
  });

  it('toasts when opening the deep link fails to mark read (recovery: retry)', async () => {
    world.markReadMode = 'fail';
    renderWithProviders(<NotificationItem item={parsedView('ntf_1')} />);
    const link = await screen.findByTestId('notification-link-ntf_1');
    fireEvent.click(link);
    await waitFor(() => expect(screen.getByRole('region', { name: 'Notifications' }).textContent).toContain('Could not mark'));
  });

  it('disables mark-read while the mutation is in flight', async () => {
    setInnerFetchForTests((async () => new Promise<Response>(() => {})) as typeof fetch);
    renderWithProviders(<NotificationItem item={parsedView('ntf_1')} />);
    const button = await screen.findByTestId('notification-mark-read-ntf_1');
    fireEvent.click(button);
    await waitFor(() => expect((screen.getByTestId('notification-mark-read-ntf_1') as HTMLButtonElement).disabled).toBe(true));
  });
});

describe('notification fetch branches', () => {
  it('lists items and counts on the success path', async () => {
    const items = await fetchNotificationItems();
    expect(items.length).toBe(2);
    expect(await fetchUnreadCount()).toBe(2);
    const page = await fetchNotifications(1, 20, new AbortController().signal);
    expect(page.items.length).toBe(2);
    expect(page.hasMore).toBe(false);
  });

  it('clamps paging windows and normalizes item failures', async () => {
    const page = await fetchNotifications(0, 5000);
    expect(page.page).toBe(1);
    expect(page.pageSize).toBeLessThanOrEqual(100);
    world.listMode = 'error500';
    await expect(fetchNotificationItems()).rejects.toMatchObject({ code: 'INTERNAL_ERROR' });
    await expect(fetchNotifications(Number.NaN, Number.NaN)).rejects.toMatchObject({ code: 'INTERNAL_ERROR' });
  });

  it('invalidates list plus unread scopes together', async () => {
    await invalidateNotifications(queryClient);
  });

  it('stays quiet for anonymous sessions', async () => {
    useAuthStore.setState({ status: 'anonymous' });
    let calls = 0;
    setInnerFetchForTests((async () => {
      calls += 1;
      return jsonResponse({});
    }) as typeof fetch);
    function Probe(): null {
      useNotifications(1, 5);
      return null;
    }
    renderWithProviders(<Probe />);
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(calls).toBe(0);
  });
});
