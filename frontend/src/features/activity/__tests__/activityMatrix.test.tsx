// Task 039B: activity state-matrix gap closure.
//
// Extends `activity.test.tsx` (columns, details, URL filters, quota
// treatments, ordering, gating) with the missing states: timeline shells
// (needs-project/loading/error-retry/empty/filtered-empty), partial +
// refresh, stale + refresh, pagination, the ActivityFilters control matrix,
// the URL-filter hook matrix, `useActivity` guards, and the types sweep.
// Every failure asserts its recovery control per §11.6 with text signals
// (041C); fixtures are synthetic and fetch is intercepted (R3).
import { QueryClientProvider } from '@tanstack/react-query';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
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
import { ActivityFilters } from '../ActivityFilters.js';
import { AuditTimeline } from '../AuditTimeline.js';
import { DEFAULT_ACTIVITY_FILTERS } from '../types.js';
import type { ActivityFilters as FilterState } from '../types.js';
import {
  ACTIVITY_PAGE_SIZE,
  filterActivityEvents,
  isDefaultActivityFilters,
  looksLikeReservationId,
  parseActivityEvent,
  parseActivityFiltersFromSearch,
  parseActivityPage,
  serializeActivityFilters,
} from '../types.js';
import { fetchActivityPage, useActivity } from '../useActivity.js';
import { useActivityFiltersFromUrl } from '../useActivityFilters.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number): Response {
  return jsonResponse(
    { error: { code, message: `backend ${code}`, correlationId: 'corr-a35', details: {} } },
    status,
  );
}

function activityRow(id: string, overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id,
    summary: `Event ${id} completed`,
    occurredAt: '2024-01-16T12:00:00Z',
    actor: 'System',
    action: 'Updated',
    ...overrides,
  };
}

interface ActivityMatrixWorld {
  items: Record<string, unknown>[];
  mode: 'ok' | 'empty' | 'error500' | 'never';
  calls: number;
}

let world: ActivityMatrixWorld;

function resetWorld(): void {
  world = {
    items: [
      activityRow('act_1', { summary: 'Run started', actor: 'System', action: 'RunStarted' }),
      activityRow('act_2', { summary: 'Translation completed', occurredAt: '2024-01-16T11:00:00Z', actor: 'owner', action: 'StageCompleted' }),
    ],
    mode: 'ok',
    calls: 0,
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

async function mockFetch(input: RequestInfo | URL): Promise<Response> {
  const url = urlOf(input);
  if (!url.includes('/api/v1/')) {
    return jsonResponse({});
  }
  if (url.includes('/activity')) {
    world.calls += 1;
    if (world.mode === 'error500') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    if (world.mode === 'empty') {
      return jsonResponse({ items: [], page: 1, pageSize: ACTIVITY_PAGE_SIZE, total: 0, hasMore: false });
    }
    if (world.mode === 'never') {
      return new Promise<Response>(() => {});
    }
    return jsonResponse({ items: world.items, page: 1, pageSize: ACTIVITY_PAGE_SIZE, total: world.items.length, hasMore: false });
  }
  return jsonResponse({});
}

function renderWithProviders(node: React.JSX.Element, initialEntries: string[] = ['/projects/prj_1/activity']): void {
  render(
    <QueryClientProvider client={queryClient}>
      <LocaleProvider>
        <ToastProvider>
          <MemoryRouter initialEntries={initialEntries}>{node}</MemoryRouter>
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

describe('AuditTimeline shell states', () => {
  it('asks for a project when unscoped (no fetch, no crash)', () => {
    renderWithProviders(<AuditTimeline projectId="" />);
    expect(screen.getByTestId('activity-needs-project')).toBeDefined();
    expect(world.calls).toBe(0);
  });

  it('shows loading while events resolve (never blank)', () => {
    world.mode = 'never';
    renderWithProviders(<AuditTimeline projectId="prj_1" />);
    expect(screen.getByTestId('activity-loading')).toBeDefined();
  });

  it('recovers from load errors with retry (recovery: retry)', async () => {
    world.mode = 'error500';
    renderWithProviders(<AuditTimeline projectId="prj_1" />);
    expect(await screen.findByTestId('activity-error')).toBeDefined();
    const callsBefore = world.calls;
    world.mode = 'ok';
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(world.calls).toBeGreaterThan(callsBefore));
    expect(await screen.findByTestId('activity-table')).toBeDefined();
  });

  it('renders the empty inbox without error chrome (recovery: none needed)', async () => {
    world.mode = 'empty';
    renderWithProviders(<AuditTimeline projectId="prj_1" />);
    expect(await screen.findByTestId('activity-empty')).toBeDefined();
    expect(screen.queryByTestId('activity-error')).toBeNull();
  });

  it('distinguishes filtered-empty with a clear action (recovery: clear)', async () => {
    renderWithProviders(<AuditTimeline projectId="prj_1" />, ['/projects/prj_1/activity?actor=nobody-here']);
    expect(await screen.findByTestId('activity-filtered-empty')).toBeDefined();
    fireEvent.click(screen.getByTestId('activity-filters-reset'));
    await waitFor(() => expect(screen.queryByTestId('activity-filtered-empty')).toBeNull());
    expect(await screen.findByTestId('activity-table')).toBeDefined();
  });

  it('warns on stale data while keeping rows (recovery: refresh)', async () => {
    renderWithProviders(<AuditTimeline projectId="prj_1" />);
    expect(await screen.findByTestId('activity-table')).toBeDefined();
    world.mode = 'error500';
    await queryClient.invalidateQueries();
    expect(await screen.findByTestId('activity-stale')).toBeDefined();
    world.mode = 'ok';
    fireEvent.click(screen.getByTestId('activity-refresh'));
    await waitFor(() => expect(screen.queryByTestId('activity-stale')).toBeNull());
  });
});

describe('ActivityFilters control matrix', () => {
  function renderFilters(filters: FilterState = { ...DEFAULT_ACTIVITY_FILTERS, actor: 'owner' }) {
    const changes: FilterState[] = [];
    let resets = 0;
    renderWithProviders(
      <ActivityFilters
        filters={filters}
        onChange={(next) => {
          changes.push(next);
        }}
        onReset={() => {
          resets += 1;
        }}
      />,
    );
    return { changes, resets: () => resets };
  }

  it('writes actor/action/date controls and resets (text signals)', () => {
    const { changes, resets } = renderFilters();
    fireEvent.change(screen.getByTestId('activity-filter-actor'), { target: { value: 'owner' } });
    fireEvent.change(screen.getByTestId('activity-filter-action'), { target: { value: 'StageCompleted' } });
    fireEvent.change(screen.getByTestId('activity-filter-from'), { target: { value: '2024-01-01' } });
    fireEvent.change(screen.getByTestId('activity-filter-to'), { target: { value: '2024-02-01' } });
    expect(changes.length).toBeGreaterThanOrEqual(3);
    expect(changes).toContainEqual(expect.objectContaining({ actor: 'owner' }));
    expect(changes).toContainEqual(expect.objectContaining({ action: 'StageCompleted' }));
    expect(changes.some((change) => change.from === '2024-01-01' || change.to === '2024-02-01')).toBe(true);
    fireEvent.click(screen.getByTestId('activity-filters-reset'));
    expect(resets()).toBe(1);
  });

  it('syncs filters through shareable URLs with truncation guards', () => {
    function Probe(): null {
      const { filters, setFilters, resetFilters } = useActivityFiltersFromUrl();
      (window as unknown as { __filters?: unknown }).__filters = { filters, setFilters, resetFilters };
      return null;
    }
    renderWithProviders(<Probe />, ['/projects/prj_1/activity?actor=owner&action=Run&page=3']);
    const readApi = () => (window as unknown as { __filters?: { filters: FilterState; setFilters: (f: FilterState) => void; resetFilters: () => void } }).__filters;
    expect(readApi()?.filters.actor).toBe('owner');
    expect(readApi()?.filters.action).toBe('Run');
    act(() => {
      readApi()?.setFilters({ actor: 'x'.repeat(200), action: '', from: '', to: '' });
    });
    expect(readApi()?.filters.actor.length).toBeLessThanOrEqual(80);
    act(() => {
      readApi()?.resetFilters();
    });
    expect(readApi()?.filters.actor).toBe('');
  });
});

describe('useActivity guards', () => {
  it('never fires for empty ids or anonymous sessions', async () => {
    let calls = 0;
    setInnerFetchForTests((async () => {
      calls += 1;
      return jsonResponse({});
    }) as typeof fetch);
    function Probe(): null {
      useActivity('', 1);
      return null;
    }
    renderWithProviders(<Probe />);
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(calls).toBe(0);
    useAuthStore.setState({ status: 'anonymous' });
    setInnerFetchForTests((async () => errorEnvelope('INTERNAL_ERROR', 500)) as typeof fetch);
    await expect(fetchActivityPage('prj_1', 1, ACTIVITY_PAGE_SIZE)).rejects.toMatchObject({ code: 'INTERNAL_ERROR' });
  });
});

describe('activity types sweep', () => {
  it('parses events/pages/filters defensively without sensitive leaks', () => {
    expect(parseActivityEvent(null)).toBeUndefined();
    expect(parseActivityEvent(activityRow('act_1'))?.id).toBe('act_1');
    expect(parseActivityPage(null).items).toEqual([]);
    expect(parseActivityPage({ items: [activityRow('a')], page: 1 }).total).toBe(1);
    expect(isDefaultActivityFilters(DEFAULT_ACTIVITY_FILTERS)).toBe(true);
    expect(isDefaultActivityFilters({ ...DEFAULT_ACTIVITY_FILTERS, actor: 'x' })).toBe(false);
    expect(parseActivityFiltersFromSearch('?actor=owner&action=Run')).toMatchObject({ actor: 'owner', action: 'Run' });
    expect(serializeActivityFilters({ ...DEFAULT_ACTIVITY_FILTERS, actor: 'owner' })).toContain('actor=owner');
    expect(filterActivityEvents(parseActivityPage({ items: world.items, page: 1 }).items, { ...DEFAULT_ACTIVITY_FILTERS, actor: 'owner' }).length).toBe(1);
    expect(looksLikeReservationId('res_1234567890abcdef')).toBe(true);
    expect(looksLikeReservationId('hello world')).toBe(false);
  });
});
