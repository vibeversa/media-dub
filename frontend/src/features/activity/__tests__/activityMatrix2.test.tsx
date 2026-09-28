// Delta 2: activity remaining-branch closure.
//
// Supplements activityMatrix (shells, stale, filters, guards, sweep) with
// pagination cursor flows (next/prev with URL state), background-partial
// refresh, advanced-details expanders (permitted/forbidden), empty
// timestamps, filter-form submit + date controls + disabled reset, the
// URL-filter hook delete legs, useActivity clamping/invalidation, and the
// types exhaustive sweep. Synthetic fixtures, fetch intercepted.
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
import { queryKeys } from '../../../api/queryKeys/index.js';
import { ACTIVITY_PAGE_SIZE, DEFAULT_ACTIVITY_FILTERS, activityActionKey, filterActivityEvents, isAdvancedActivityKey, isDefaultActivityFilters, isForbiddenActivityKey, looksLikeReservationId, parseActivityEvent, parseActivityEvents, parseActivityFiltersFromSearch, parseActivityPage, serializeActivityFilters } from '../types.js';
import type { ActivityFilters as FilterState } from '../types.js';
import { fetchActivityPage, invalidateActivity, useActivity } from '../useActivity.js';
import { useActivityFiltersFromUrl } from '../useActivityFilters.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number): Response {
  return jsonResponse(
    { error: { code, message: `backend ${code}`, correlationId: 'corr-a35b', details: {} } },
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

interface World {
  mode: 'ok' | 'error500' | 'never';
}

let world: World;

function resetWorld(): void {
  world = { mode: 'ok' };
}

function items25(): Record<string, unknown>[] {
  const items: Record<string, unknown>[] = [];
  for (let i = 0; i < 25; i += 1) {
    items.push(
      activityRow(`act_${String(i + 1).padStart(2, '0')}`, {
        summary: `Event ${i + 1}`,
        run: `run_${i + 1}`,
        stage: 'qc',
      }),
    );
  }
  return items;
}

function urlOf(input: RequestInfo | URL): string {
  if (typeof input === 'string') return input;
  if (input instanceof URL) return input.href;
  return (input as Request).url;
}

async function mockFetch(input: RequestInfo | URL): Promise<Response> {
  const url = urlOf(input);
  if (!url.includes('/api/v1/')) return jsonResponse({});
  if (url.includes('/activity')) {
    if (world.mode === 'error500') return errorEnvelope('INTERNAL_ERROR', 500);
    if (world.mode === 'never') return new Promise<Response>(() => {});
    const parsed = new URL(url);
    const page = Number.parseInt(parsed.searchParams.get('page') ?? '1', 10);
    const pageSize = 20;
    const all = items25();
    const slice = all.slice((page - 1) * pageSize, page * pageSize);
    return jsonResponse({ items: slice, page, pageSize, total: all.length, hasMore: page * pageSize < all.length });
  }
  return jsonResponse({});
}

function renderTimeline(initialEntries: string[] = ['/projects/prj_1/activity']): void {
  render(
    <QueryClientProvider client={queryClient}>
      <LocaleProvider>
        <ToastProvider>
          <MemoryRouter initialEntries={initialEntries}>
            <AuditTimeline projectId="prj_1" />
          </MemoryRouter>
        </ToastProvider>
      </LocaleProvider>
    </QueryClientProvider>,
  );
}

function authenticate(permissions: readonly string[] = ['project.view']): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', permissions);
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

describe('AuditTimeline pagination cursors', () => {
  it('pages forward and back while keeping filter state', async () => {
    renderTimeline();
    expect(await screen.findByTestId('activity-table')).toBeDefined();
    expect(screen.getByTestId('activity-page-info').textContent).toBe('Page 1 of 2');
    expect((screen.getByTestId('activity-page-prev') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByTestId('activity-page-next'));
    await waitFor(() => expect(screen.getByTestId('activity-page-info').textContent).toBe('Page 2 of 2'));
    expect((screen.getByTestId('activity-page-next') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByTestId('activity-page-prev'));
    await waitFor(() => expect(screen.getByTestId('activity-page-info').textContent).toBe('Page 1 of 2'));
  });

  it('parses out-of-range and non-numeric page cursors as page 1', async () => {
    renderTimeline(['/projects/prj_1/activity?page=0']);
    expect(await screen.findByTestId('activity-table')).toBeDefined();
    expect(screen.getByTestId('activity-page-info').textContent).toBe('Page 1 of 2');
    cleanup();
    queryClient.clear();
    renderTimeline(['/projects/prj_1/activity?page=bogus']);
    expect(await screen.findByTestId('activity-table')).toBeDefined();
    expect(screen.getByTestId('activity-page-info').textContent).toBe('Page 1 of 2');
  });

  it('refreshes background-partial data without losing rows (recovery: refresh)', async () => {
    renderTimeline();
    expect(await screen.findByTestId('activity-table')).toBeDefined();
    setInnerFetchForTests((async () => new Promise<Response>(() => {})) as typeof fetch);
    void queryClient.invalidateQueries();
    expect(await screen.findByTestId('activity-partial')).toBeDefined();
    setInnerFetchForTests(mockFetch as typeof fetch);
    fireEvent.click(screen.getByTestId('activity-partial-refresh'));
    await waitFor(() => expect(screen.queryByTestId('activity-partial')).toBeNull());
  });
});

describe('advanced details + timestamps', () => {
  it('expands and collapses allowlisted details for permitted readers', async () => {
    authenticate(['project.view', 'diagnostics.view']);
    renderTimeline();
    const toggle = await screen.findByTestId('activity-row-act_01-toggle');
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(toggle.textContent).toBe('Show details');
    fireEvent.click(toggle);
    expect(await screen.findByTestId('activity-advanced-act_01')).toBeDefined();
    expect(screen.getByTestId('activity-advanced-key-act_01-run').textContent).toBe('run');
    expect(screen.getByTestId('activity-advanced-value-act_01-run').textContent).toBe('run_1');
    fireEvent.click(screen.getByTestId('activity-row-act_01-toggle'));
    await waitFor(() => expect(screen.queryByTestId('activity-advanced-act_01')).toBeNull());
  });

  it('hides advanced details from unpermitted readers with placeholder text', async () => {
    renderTimeline();
    expect(await screen.findByTestId('activity-advanced-forbidden-act_01')).toBeDefined();
    expect(screen.getByTestId('activity-advanced-forbidden-act_01').textContent).toContain('hidden');
  });
});

describe('ActivityFilters form branches', () => {
  function renderFilters(filters: FilterState = DEFAULT_ACTIVITY_FILTERS): { changes: FilterState[]; resets: () => number } {
    const changes: FilterState[] = [];
    let resets = 0;
    render(
      <MemoryRouter>
        <ActivityFilters
          filters={filters}
          onChange={(next) => {
            changes.push(next);
          }}
          onReset={() => {
            resets += 1;
          }}
        />
      </MemoryRouter>,
    );
    return { changes, resets: () => resets };
  }

  it('prevents submit navigation and writes date controls', () => {
    const { changes } = renderFilters();
    fireEvent.change(screen.getByTestId('activity-filter-from'), { target: { value: '2024-01-01' } });
    fireEvent.change(screen.getByTestId('activity-filter-to'), { target: { value: '2024-02-01' } });
    expect(changes).toContainEqual(expect.objectContaining({ from: '2024-01-01' }));
    expect(changes).toContainEqual(expect.objectContaining({ to: '2024-02-01' }));
    fireEvent.submit(screen.getByTestId('activity-filters'));
    expect(changes.length).toBe(2);
  });

  it('disables reset on default filters and enables it otherwise', () => {
    renderFilters();
    expect((screen.getByTestId('activity-filters-reset') as HTMLButtonElement).disabled).toBe(true);
    cleanup();
    const { resets } = renderFilters({ ...DEFAULT_ACTIVITY_FILTERS, actor: 'owner' });
    expect((screen.getByTestId('activity-filters-reset') as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(screen.getByTestId('activity-filters-reset'));
    expect(resets()).toBe(1);
  });
});

describe('URL filter hook delete legs', () => {
  function renderProbe(initialEntries: string[]): void {
    function Probe(): null {
      const { filters, setFilters, resetFilters } = useActivityFiltersFromUrl();
      (window as unknown as { __f?: unknown }).__f = { filters, setFilters, resetFilters };
      return null;
    }
    render(
      <MemoryRouter initialEntries={initialEntries}>
        <Probe />
      </MemoryRouter>,
    );
  }

  it('sets and clears every key plus the page cursor', () => {
    renderProbe(['/projects/prj_1/activity?actor=owner&action=Run&from=2024-01-01&to=2024-02-01&page=3']);
    const api = () => (window as unknown as { __f: { filters: FilterState; setFilters: (f: FilterState) => void; resetFilters: () => void } }).__f;
    expect(api().filters.actor).toBe('owner');
    act(() => {
      api().setFilters({ actor: '', action: '', from: '', to: '' });
    });
    expect(api().filters.actor).toBe('');
    act(() => {
      api().setFilters({ actor: 'x', action: 'y', from: '2024-01-01', to: '2024-02-01' });
    });
    expect(api().filters.actor).toBe('x');
    act(() => {
      api().resetFilters();
    });
    expect(api().filters).toEqual(DEFAULT_ACTIVITY_FILTERS);
  });
});

describe('useActivity fetch branches', () => {
  it('clamps paging and normalizes failures', async () => {
    const page = await fetchActivityPage('prj_1', 0, 5000);
    expect(page.page).toBe(1);
    expect(page.pageSize).toBeLessThanOrEqual(100);
    world.mode = 'error500';
    await expect(fetchActivityPage('prj_1', 1, 20)).rejects.toMatchObject({ code: 'INTERNAL_ERROR' });
    await invalidateActivity(queryClient, 'prj_1');
  });

  it('never fires for empty ids and forwards abort signals', async () => {
    let calls = 0;
    setInnerFetchForTests((async () => {
      calls += 1;
      return jsonResponse({});
    }) as typeof fetch);
    function Probe(): null {
      useActivity('', 1);
      return null;
    }
    render(
      <QueryClientProvider client={queryClient}>
        <Probe />
      </QueryClientProvider>,
    );
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(calls).toBe(0);
    setInnerFetchForTests(mockFetch as typeof fetch);
    const page = await fetchActivityPage('prj_1', 1, 20, new AbortController().signal);
    expect(page.items.length).toBeGreaterThan(0);
  });

  it('falls back to the default page size for unusable cursors', async () => {
    // Paging inputs arrive from the URL, so an out-of-range or non-numeric
    // page size must degrade to the default rather than request the API with
    // a nonsense page (no blank list, no crash).
    for (const pageSize of [0, -5, Number.NaN, Number.POSITIVE_INFINITY]) {
      const page = await fetchActivityPage('prj_1', 1, pageSize);
      expect(page.pageSize).toBe(ACTIVITY_PAGE_SIZE);
    }
  });

  it('clamps unusable hook cursors onto the default page key', async () => {
    // The hook mirrors the fetch clamps so two bad cursors can never open a
    // second cache entry for the same list.
    function Probe(): null {
      useActivity('prj_1', 0, -1);
      return null;
    }
    render(
      <QueryClientProvider client={queryClient}>
        <Probe />
      </QueryClientProvider>,
    );
    await waitFor(() =>
      expect(
        queryClient.getQueryState(queryKeys.activity.list('prj_1', { page: 1, pageSize: ACTIVITY_PAGE_SIZE })),
      ).toBeDefined(),
    );
  });
});

describe('activity types sweep', () => {
  it('gates forbidden and advanced keys without leaks', () => {
    expect(isForbiddenActivityKey('reservationId')).toBe(true);
    expect(isForbiddenActivityKey('signed_url')).toBe(true);
    expect(isForbiddenActivityKey('downloadUrl')).toBe(true);
    expect(isForbiddenActivityKey('api_key')).toBe(true);
    expect(isForbiddenActivityKey('stage')).toBe(false);
    expect(isAdvancedActivityKey('run')).toBe(true);
    expect(isAdvancedActivityKey('correlation_id')).toBe(true);
    expect(isAdvancedActivityKey('secret')).toBe(false);
  });

  it('sanitizes actors and actions to display-safe text', () => {
    expect(parseActivityEvent({ id: 'a', summary: 'did things', actor: 'owner@corp.com' })?.actor).toBe('Team member');
    expect(parseActivityEvent({ id: 'a', summary: 'did things', actor: 'usr_abc123' })?.actor).toBe('Team member');
    expect(parseActivityEvent({ id: 'a', summary: 'system rotated keys' })?.actor).toBe('System');
    expect(parseActivityEvent({ id: 'a', summary: 'did things', actor: '' })?.actor).toBe('System');
    expect(parseActivityEvent({ id: 'a', summary: 'did things', actor: 'x'.repeat(200) })?.actor.length).toBe(80);
    expect(parseActivityEvent({ id: 'a', summary: 'did things', action: '' })?.action).toBe('Updated');
    expect(parseActivityEvent({ id: 'a', summary: 'did things', action: 'x'.repeat(200) })?.action.length).toBe(80);
    expect(parseActivityEvent({ id: '', summary: 'x' })).toBeUndefined();
    expect(parseActivityEvent({ id: 'a', summary: '' })).toBeUndefined();
    expect(parseActivityEvent({ id: 'a', summary: 'x', occurredAt: 7 })?.timestamp).toBe('');
  });

  it('keeps only allowlisted advanced fields without secret text', () => {
    const parsed = parseActivityEvent({
      id: 'a',
      summary: 'run finished',
      run: 'run_1',
      attempt: 3,
      cost: 12,
      bogus: 'dropped',
      token: 'dropped',
      runId: 'res_123',
      provider: 'https://internal',
    });
    expect(parsed?.hasAdvanced).toBe(true);
    expect(parsed?.advanced['run']).toBe('run_1');
    expect(parsed?.advanced['attempt']).toBe('3');
    expect(parsed?.advanced['bogus']).toBeUndefined();
    expect(parsed?.advanced['token']).toBeUndefined();
    expect(parsed?.advanced['runId']).toBeUndefined();
    expect(parsed?.advanced['provider']).toBeUndefined();
    expect(parseActivityEvent({ id: 'a', summary: 'plain' })?.hasAdvanced).toBe(false);
  });

  it('parses envelopes, pages, and shareable filters', () => {
    expect(parseActivityEvents({ recent: [activityRow('r1')] }).length).toBe(1);
    expect(parseActivityEvents([activityRow('r1')]).length).toBe(1);
    expect(parseActivityEvents({ items: 'nope' })).toEqual([]);
    expect(parseActivityEvents('nope')).toEqual([]);
    expect(parseActivityEvents({ items: [activityRow('d'), activityRow('d')] }).length).toBe(1);
    const page = parseActivityPage({ items: [activityRow('a')], page: 0, pageSize: 0, total: -1, hasMore: false });
    expect(page.page).toBe(1);
    expect(page.pageSize).toBe(20);
    expect(page.total).toBe(1);
    expect(parseActivityPage({ items: [activityRow('a')], page: 1, pageSize: 20, total: 100 }).hasMore).toBe(true);
    expect(isDefaultActivityFilters(DEFAULT_ACTIVITY_FILTERS)).toBe(true);
    expect(parseActivityFiltersFromSearch('?actor=' + 'x'.repeat(200)).actor.length).toBeLessThanOrEqual(80);
    expect(serializeActivityFilters(DEFAULT_ACTIVITY_FILTERS)).toBe('');
    expect(serializeActivityFilters({ ...DEFAULT_ACTIVITY_FILTERS, from: '2024-01-01', to: '2024-02-01' })).toContain('from=');
  });

  it('filters by actor/action/date windows with day-end tolerance', () => {
    const items = parseActivityEvents({ items: [activityRow('a', { occurredAt: '2024-01-16T12:00:00Z', actor: 'owner', action: 'Run' }), activityRow('b', { occurredAt: 'not-a-date', actor: 'owner', action: 'Run' })] });
    expect(filterActivityEvents(items, { ...DEFAULT_ACTIVITY_FILTERS, actor: 'OWN' }).length).toBe(2);
    expect(filterActivityEvents(items, { ...DEFAULT_ACTIVITY_FILTERS, action: 'run' }).length).toBe(2);
    expect(filterActivityEvents(items, { ...DEFAULT_ACTIVITY_FILTERS, from: '2024-02-01' }).length).toBe(0);
    expect(filterActivityEvents(items, { ...DEFAULT_ACTIVITY_FILTERS, to: '2024-01-16' }).length).toBe(1);
    expect(filterActivityEvents(items, { ...DEFAULT_ACTIVITY_FILTERS, to: '2024-01-16T12:00:00Z' }).length).toBe(1);
    expect(activityActionKey('Run Started!')).toBe('run-started');
    expect(activityActionKey('')).toBe('unknown');
    expect(looksLikeReservationId('Reservation abc')).toBe(true);
  });
});
