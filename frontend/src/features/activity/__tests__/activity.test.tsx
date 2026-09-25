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
import { AuditTimeline } from '../AuditTimeline.js';
import { CostSummary } from '../../settings/CostSummary.js';
import { QuotaBanner } from '../../settings/QuotaBanner.js';
import { resetQuotaBannerDismissalForTests } from '../../settings/quotaDismiss.js';
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
import { deriveQuotaState, isQuotaBlocking } from '../../settings/types.js';
import { queryKeys } from '../../../api/queryKeys/index.js';

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

interface ActivityWorld {
  items: Record<string, unknown>[];
  activityStatus: number;
  summaryBody: Record<string, unknown>;
  workspaceBody: Record<string, unknown>;
  calls: { activity: number; summary: number; workspace: number };
}

function newWorld(overrides: Partial<ActivityWorld> = {}): ActivityWorld {
  return {
    items: [
      activityRow('act_1', {
        summary: 'Run started',
        occurredAt: '2024-01-16T12:00:00Z',
        actor: 'System',
        action: 'RunStarted',
        run: 'run_1',
        stage: 'Transcription',
      }),
      activityRow('act_2', {
        summary: 'Translation completed',
        occurredAt: '2024-01-16T11:00:00Z',
        actor: 'owner',
        action: 'StageCompleted',
        stage: 'Translation',
      }),
      activityRow('act_3', {
        summary: 'Review requested',
        occurredAt: '2024-01-16T10:00:00Z',
        actor: 'owner@example.com',
        action: 'ReviewRequested',
      }),
    ],
    activityStatus: 200,
    summaryBody: {
      cost: { monthToDate: 12.5, currency: 'USD' },
      quota: { remaining: 9, resetsAt: '2026-09-25T00:00:00Z' },
      storage: { usedBytes: 1000, quotaBytes: 100000 },
    },
    workspaceBody: {
      cost: { runCost: 1.5, monthToDate: 12.5 },
      media: { id: 'med_1', status: 'Valid', durationMs: 61000 },
    },
    calls: { activity: 0, summary: 0, workspace: 0 },
    ...overrides,
  };
}

let world: ActivityWorld = newWorld();

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
  if (method === 'GET' && url.includes('/projects/') && url.includes('/activity')) {
    world.calls.activity += 1;
    if (world.activityStatus !== 200) {
      return errorEnvelope('NOT_FOUND', world.activityStatus);
    }
    return jsonResponse({
      items: world.items,
      page: 1,
      pageSize: ACTIVITY_PAGE_SIZE,
      total: world.items.length,
      hasMore: false,
    });
  }
  if (method === 'GET' && url.includes('/dashboard/summary')) {
    world.calls.summary += 1;
    return jsonResponse(world.summaryBody);
  }
  if (method === 'GET' && url.includes('/workspace')) {
    world.calls.workspace += 1;
    return jsonResponse(world.workspaceBody);
  }
  if (method === 'GET' && url.includes('/me/preferences')) {
    return jsonResponse({});
  }
  return jsonResponse({});
}

function authenticate(): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', ['project.view', 'project.edit']);
}

function renderWithProviders(node: React.ReactNode, initialEntries: string[] = ['/projects/prj_1/activity']): void {
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

beforeEach(() => {
  world = newWorld();
  setInnerFetchForTests(mockFetch as typeof fetch);
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  resetQuotaBannerDismissalForTests();
  try {
    window.sessionStorage.clear();
  } catch {
    // Ignore.
  }
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

describe('timeline columns', () => {
  it('renders timestamp, actor, action, and summary for every event', async () => {
    renderWithProviders(<AuditTimeline projectId="prj_1" />);
    expect(await screen.findByTestId('activity-row-act_1')).toBeDefined();
    expect(screen.getByTestId('activity-timestamp-act_1')).toBeDefined();
    expect(screen.getByTestId('activity-actor-act_1').textContent).toBe('System');
    expect(screen.getByTestId('activity-action-act_1').textContent).toBe('RunStarted');
    expect(screen.getByTestId('activity-summary-act_1').textContent).toBe('Run started');
    expect(screen.getByTestId('activity-row-act_2')).toBeDefined();
    expect(screen.getByTestId('activity-table')).toBeDefined();
    expect(screen.getByTestId('activity-list-key').textContent).toContain('activity');
    expect(screen.getByTestId('activity-all-key').textContent).toContain('activity');
  });

  it('uses display names only and never leaks emails', async () => {
    renderWithProviders(<AuditTimeline projectId="prj_1" />);
    expect(await screen.findByTestId('activity-row-act_3')).toBeDefined();
    expect(screen.getByTestId('activity-actor-act_3').textContent).toBe('Team member');
    expect(document.body.textContent).not.toContain('owner@example.com');
  });

  it('shows an empty state with progress copy for new projects', async () => {
    world.items = [];
    renderWithProviders(<AuditTimeline projectId="prj_1" />);
    expect(await screen.findByTestId('activity-empty')).toBeDefined();
    expect((screen.getByTestId('activity-empty').textContent ?? '').toLowerCase()).toContain(
      'events appear as work progresses',
    );
  });
});

describe('hidden advanced details', () => {
  it('hides advanced fields until the per-row expander opens', async () => {
    renderWithProviders(<AuditTimeline projectId="prj_1" />);
    expect(await screen.findByTestId('activity-row-act_1')).toBeDefined();
    expect(screen.queryByTestId('activity-advanced-act_1')).toBeNull();
    fireEvent.click(screen.getByTestId('activity-row-act_1-toggle'));
    expect(screen.getByTestId('activity-advanced-act_1')).toBeDefined();
    expect(screen.getByTestId('activity-advanced-act_1').textContent).toContain('Transcription');
    fireEvent.click(screen.getByTestId('activity-row-act_1-toggle'));
    expect(screen.queryByTestId('activity-advanced-act_1')).toBeNull();
  });

  it('never renders reservation ids or provider-internal keys', async () => {
    world.items = [
      {
        ...activityRow('act_secret', { summary: 'Run reserved', occurredAt: '2024-01-16T12:00:00Z' }),
        reservationId: 'res_abc123',
        reservation_id: 'res_xyz',
        secret: 'shhh-secret-value',
        token: 'super-secret-token',
        signedUrl: 'https://example.com/signed-value-xyz',
        run: 'run_1',
      },
    ];
    renderWithProviders(<AuditTimeline projectId="prj_1" />);
    expect(await screen.findByTestId('activity-row-act_secret')).toBeDefined();
    fireEvent.click(screen.getByTestId('activity-row-act_secret-toggle'));
    const advanced = screen.getByTestId('activity-advanced-act_secret').textContent ?? '';
    expect(advanced).not.toContain('res_abc123');
    expect(advanced).not.toContain('res_xyz');
    expect(document.body.textContent).not.toContain('shhh-secret-value');
    expect(document.body.textContent).not.toContain('super-secret-token');
    expect(document.body.textContent).not.toContain('signed-value-xyz');
    expect(looksLikeReservationId('res_abc123')).toBe(true);
    expect(looksLikeReservationId('run_1')).toBe(false);
  });

  it('parses defensively and filters purely', () => {
    expect(parseActivityEvent({ noId: true })).toBeUndefined();
    expect(parseActivityEvent({ id: '', summary: 'x' })).toBeUndefined();
    const parsed = parseActivityPage({ items: [activityRow('a'), activityRow('a'), { noId: true }] });
    expect(parsed.items.map((entry) => entry.id)).toEqual(['a']);
    expect(isDefaultActivityFilters({ actor: '', action: '', from: '', to: '' })).toBe(true);
    expect(isDefaultActivityFilters({ actor: 'x', action: '', from: '', to: '' })).toBe(false);
    expect(parseActivityFiltersFromSearch('?actor=System&action=Run')).toEqual({
      actor: 'System',
      action: 'Run',
      from: '',
      to: '',
    });
    expect(serializeActivityFilters({ actor: '', action: '', from: '', to: '' })).toBe('');
    expect(serializeActivityFilters({ actor: 'System', action: '', from: '', to: '' })).toContain('actor=System');
    const filtered = filterActivityEvents(parsed.items, { actor: 'zzz', action: '', from: '', to: '' });
    expect(filtered).toHaveLength(0);
  });
});

describe('filters shareable in the url', () => {
  it('filters by actor and resets cleanly to defaults', async () => {
    renderWithProviders(<AuditTimeline projectId="prj_1" />, ['/projects/prj_1/activity?actor=System']);
    expect(await screen.findByTestId('activity-row-act_1')).toBeDefined();
    expect(screen.getByTestId('activity-filter-actor')).toHaveProperty('value', 'System');
    expect(screen.queryByTestId('activity-row-act_3')).toBeNull();
    fireEvent.click(screen.getByTestId('activity-filters-reset'));
    await waitFor(() => {
      expect(screen.getByTestId('activity-row-act_3')).toBeDefined();
    });
    expect((screen.getByTestId('activity-filter-actor') as HTMLInputElement).value).toBe('');
  });

  it('keeps the factory key stable across filter changes', () => {
    expect(queryKeys.activity.list('prj_1', { page: 1, pageSize: 20 })).toEqual(
      queryKeys.activity.list('prj_1', { page: 1, pageSize: 20 }),
    );
    expect(queryKeys.preferences.list()).toEqual(queryKeys.preferences.list());
    expect(queryKeys.cost.detail('prj_1')).toEqual(queryKeys.cost.detail('prj_1'));
  });
});

describe('cost estimate labeling and quota treatments', () => {
  it('labels preflight estimates and distinguishes actuals', async () => {
    renderWithProviders(<CostSummary projectId="prj_1" />);
    expect(await screen.findByTestId('cost-estimate-label')).toBeDefined();
    expect(screen.getByTestId('cost-estimate-label').textContent).toBe('Estimate');
    expect(screen.getByTestId('cost-estimated').textContent).toContain('Estimate');
    expect(screen.getByTestId('cost-actual')).toBeDefined();
    expect(screen.getByTestId('cost-actual-month')).toBeDefined();
  });

  it('shows unavailable instead of zero-filling when cost predates tracking', async () => {
    world.summaryBody = {};
    world.workspaceBody = {};
    renderWithProviders(<CostSummary projectId="prj_1" />);
    expect(await screen.findByTestId('cost-unavailable')).toBeDefined();
    expect(screen.queryByTestId('cost-actual')).toBeNull();
  });

  it('maps quota states to distinct treatments', () => {
    expect(deriveQuotaState({ remaining: 9, usedBytes: 10, quotaBytes: 100 })).toBe('available');
    expect(deriveQuotaState({ remaining: 2, usedBytes: 10, quotaBytes: 100 })).toBe('near');
    expect(deriveQuotaState({ remaining: 0 })).toBe('exceeded');
    expect(deriveQuotaState({ remaining: 9, usedBytes: 100, quotaBytes: 100 })).toBe('exceeded');
    expect(deriveQuotaState({ remaining: 9, usedBytes: 10, quotaBytes: 100, reservedUsd: 1.5 })).toBe('reserved');
    expect(isQuotaBlocking('exceeded')).toBe(true);
    expect(isQuotaBlocking('near')).toBe(false);
  });

  it('renders each quota banner distinctly', () => {
    const { unmount } = render(
      <MemoryRouter>
        <QuotaBanner state="exceeded" remaining={0} resetsAt="2026-09-25T00:00:00Z" />
      </MemoryRouter>,
    );
    expect(screen.getByTestId('quota-banner-exceeded')).toBeDefined();
    expect(screen.getByTestId('quota-banner-exceeded').getAttribute('data-blocked')).toBe('true');
    expect(screen.getByTestId('quota-banner-explanation')).toBeDefined();
    expect(screen.getByTestId('quota-banner-manage').getAttribute('href')).toBe('/settings');
    unmount();
    cleanup();
    render(
      <MemoryRouter>
        <QuotaBanner state="near" remaining={2} />
      </MemoryRouter>,
    );
    expect(screen.getByTestId('quota-banner-near')).toBeDefined();
    fireEvent.click(screen.getByTestId('quota-banner-dismiss'));
    expect(screen.queryByTestId('quota-banner-near')).toBeNull();
  });
});
