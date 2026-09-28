// Delta 2: cost remaining-branch closure.
//
// Supplements costMatrix (shells, badges, banners, dismissal) with
// CostSummary figure variants (duration windows, actuals present/absent,
// partial storage, quota remaining/resets absences, unavailable flag,
// partial-refresh recovery, blocked quota signal) and the cost types sweep
// (ratios, state derivation, tones, glyphs, view builders, reservation
// hygiene). Synthetic fixtures, fetch intercepted, text signals only.
//
// Intentional-exclusion candidate: the `Reserved` block in CostSummary
// (reservedUsd) is unreachable through `useCostQuota`, which always builds
// the quota view with `reservedUsd: undefined`; the builder branch itself
// is covered below via `buildQuotaView`.
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
import { CostSummary } from '../CostSummary.js';
import {
  buildCostBreakdown,
  buildQuotaView,
  containsReservationId,
  deriveQuotaState,
  iconForQuotaState,
  isQuotaBlocking,
  storageUsageRatio,
  toneForQuotaState,
} from '../types.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

interface World {
  monthToDate: number | undefined;
  runCost: number | undefined;
  workspaceMonth: number | undefined;
  durationMs: number | undefined;
  remaining: number | undefined;
  resetsAt: string | undefined;
  usedBytes: number | undefined;
  quotaBytes: number | undefined;
  summaryStatus: number;
  calls: number;
}

let world: World;

function resetWorld(): void {
  world = {
    monthToDate: 12.5,
    runCost: 1.5,
    workspaceMonth: undefined,
    durationMs: 61000,
    remaining: 9,
    resetsAt: '2026-09-25T00:00:00Z',
    usedBytes: 1000,
    quotaBytes: 100000,
    summaryStatus: 200,
    calls: 0,
  };
}

function urlOf(input: RequestInfo | URL): string {
  if (typeof input === 'string') return input;
  if (input instanceof URL) return input.href;
  return (input as Request).url;
}

async function mockFetch(input: RequestInfo | URL): Promise<Response> {
  const url = urlOf(input);
  if (!url.includes('/api/v1/')) return jsonResponse({});
  if (url.includes('/dashboard/summary')) {
    world.calls += 1;
    if (world.summaryStatus !== 200) {
      return jsonResponse(
        { error: { code: 'COST_ERROR', message: 'backend COST_ERROR', correlationId: 'corr-c35b', details: {} } },
        world.summaryStatus,
      );
    }
    return jsonResponse({
      cost: world.monthToDate === undefined ? {} : { monthToDate: world.monthToDate, currency: 'USD' },
      quota: {
        ...(world.remaining === undefined ? {} : { remaining: world.remaining }),
        ...(world.resetsAt === undefined ? {} : { resetsAt: world.resetsAt }),
      },
      storage: {
        ...(world.usedBytes === undefined ? {} : { usedBytes: world.usedBytes }),
        ...(world.quotaBytes === undefined ? {} : { quotaBytes: world.quotaBytes }),
      },
    });
  }
  if (url.includes('/workspace')) {
    world.calls += 1;
    return jsonResponse({
      cost: {
        ...(world.runCost === undefined ? {} : { runCost: world.runCost }),
        ...(world.workspaceMonth === undefined ? {} : { monthToDate: world.workspaceMonth }),
      },
      media: world.durationMs === undefined ? {} : { id: 'med_1', status: 'Valid', durationMs: world.durationMs },
    });
  }
  return jsonResponse({});
}

function renderSummary(): void {
  render(
    <QueryClientProvider client={queryClient}>
      <LocaleProvider>
        <ToastProvider>
          <MemoryRouter>
            <CostSummary projectId="prj_1" />
          </MemoryRouter>
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

describe('CostSummary figure variants', () => {
  it('renders minute and second duration windows distinctly', async () => {
    renderSummary();
    expect(await screen.findByTestId('cost-duration')).toBeDefined();
    expect(screen.getByTestId('cost-duration').textContent).toBe('1m 1s');
    cleanup();
    queryClient.clear();
    world.durationMs = 4000;
    renderSummary();
    expect(await screen.findByTestId('cost-duration')).toBeDefined();
    expect((await screen.findByTestId('cost-duration')).textContent).toBe('4s');
  });

  it('dashes non-positive or missing durations (never negative text)', async () => {
    world.durationMs = 0;
    renderSummary();
    expect(await screen.findByTestId('cost-duration')).toBeDefined();
    expect(screen.getByTestId('cost-duration').textContent).toBe('—');
    cleanup();
    queryClient.clear();
    world.durationMs = undefined;
    renderSummary();
    expect(await screen.findByTestId('cost-duration')).toBeDefined();
    expect(screen.getByTestId('cost-duration').textContent).toBe('—');
  });

  it('marks missing actuals unavailable instead of zero-filling', async () => {
    world.runCost = undefined;
    world.monthToDate = undefined;
    world.workspaceMonth = undefined;
    renderSummary();
    expect(await screen.findByTestId('cost-unavailable')).toBeDefined();
    cleanup();
    queryClient.clear();
    resetWorld();
    world.runCost = undefined;
    renderSummary();
    expect(await screen.findByTestId('cost-actual')).toBeDefined();
    expect(screen.getByTestId('cost-actual').textContent).toBe('Unavailable');
    expect(screen.getByTestId('cost-actual-month').textContent).not.toBe('Unavailable');
  });

  it('dashes partial storage pairs (never half figures)', async () => {
    world.quotaBytes = undefined;
    renderSummary();
    expect(await screen.findByTestId('cost-storage')).toBeDefined();
    expect(screen.getByTestId('cost-storage').textContent).toBe('—');
    cleanup();
    queryClient.clear();
    resetWorld();
    world.usedBytes = undefined;
    renderSummary();
    expect(await screen.findByTestId('cost-storage')).toBeDefined();
    expect(screen.getByTestId('cost-storage').textContent).toBe('—');
  });

  it('omits quota remaining/resets spans when absent (no blanks)', async () => {
    world.remaining = undefined;
    world.resetsAt = undefined;
    renderSummary();
    expect(await screen.findByTestId('cost-quota-state')).toBeDefined();
    expect(screen.queryByTestId('cost-quota-remaining')).toBeNull();
    expect(screen.queryByTestId('cost-quota-resets')).toBeNull();
  });

  it('flags exceeded quotas as blocked with error tone', async () => {
    world.remaining = 0;
    renderSummary();
    expect(await screen.findByTestId('cost-quota-state')).toBeDefined();
    const section = screen.getByTestId('cost-summary');
    expect(section.getAttribute('data-blocked')).toBe('true');
    expect(section.getAttribute('data-quota')).toBe('exceeded');
  });

  it('refreshes partial figures without losing the last load (recovery: refresh)', async () => {
    renderSummary();
    expect(await screen.findByTestId('cost-estimated')).toBeDefined();
    setInnerFetchForTests((async () => new Promise<Response>(() => {})) as typeof fetch);
    const callsBefore = world.calls;
    void queryClient.invalidateQueries();
    expect(await screen.findByTestId('cost-partial')).toBeDefined();
    setInnerFetchForTests(mockFetch as typeof fetch);
    fireEvent.click(screen.getByTestId('cost-partial-refresh'));
    await waitFor(() => expect(world.calls).toBeGreaterThan(callsBefore));
  });
});

describe('cost types sweep', () => {
  it('ratios zero on unknown or non-positive inputs', () => {
    expect(storageUsageRatio(undefined, 100)).toBe(0);
    expect(storageUsageRatio(50, undefined)).toBe(0);
    expect(storageUsageRatio(50, 0)).toBe(0);
    expect(storageUsageRatio(0, 100)).toBe(0);
    expect(storageUsageRatio(-5, 100)).toBe(0);
    expect(storageUsageRatio(50, 100)).toBe(0.5);
  });

  it('derives exceeded/reserved/near/available in priority order', () => {
    expect(deriveQuotaState({ remaining: 0 })).toBe('exceeded');
    expect(deriveQuotaState({ usedBytes: 100, quotaBytes: 100 })).toBe('exceeded');
    expect(deriveQuotaState({ reservedUsd: 5 })).toBe('reserved');
    expect(deriveQuotaState({ reservedUsd: 0, remaining: 9 })).toBe('available');
    expect(deriveQuotaState({ remaining: 2 })).toBe('near');
    expect(deriveQuotaState({ usedBytes: 85, quotaBytes: 100 })).toBe('near');
    expect(deriveQuotaState({ remaining: 9 })).toBe('available');
    expect(deriveQuotaState({})).toBe('available');
  });

  it('tones, glyphs, and blocking map every state with text fallbacks', () => {
    expect(toneForQuotaState('exceeded')).toBe('error');
    expect(toneForQuotaState('near')).toBe('warning');
    expect(toneForQuotaState('reserved')).toBe('info');
    expect(toneForQuotaState('available')).toBe('success');
    expect(toneForQuotaState('bogus' as never)).toBe('success');
    expect(iconForQuotaState('available')).toBe('✓');
    expect(iconForQuotaState('near')).toBe('⚠');
    expect(iconForQuotaState('exceeded')).toBe('✕');
    expect(iconForQuotaState('reserved')).toBe('◷');
    expect(iconForQuotaState('bogus' as never)).toBe('•');
    expect(isQuotaBlocking('exceeded')).toBe(true);
    expect(isQuotaBlocking('near')).toBe(false);
  });

  it('builds quota views with positive-only reserves', () => {
    expect(buildQuotaView({ remaining: 2, resetsAt: 'x', usedBytes: 1, quotaBytes: 10, reservedUsd: 4 }).reservedUsd).toBe(4);
    expect(buildQuotaView({ reservedUsd: 0 }).reservedUsd).toBeUndefined();
    expect(buildQuotaView({ reservedUsd: -2 }).reservedUsd).toBeUndefined();
    expect(buildQuotaView({ remaining: 'nope', resetsAt: '', usedBytes: 'x', quotaBytes: 0 }).state).toBe('available');
  });

  it('builds breakdowns with guarded numerics and currency fallback', () => {
    expect(buildCostBreakdown({ estimatedUsd: -5 }).estimatedUsd).toBe(0);
    expect(buildCostBreakdown({ estimatedUsd: 'nope' }).estimatedUsd).toBe(0);
    expect(buildCostBreakdown({ estimatedUsd: 3, currency: '' }).currency).toBe('USD');
    expect(buildCostBreakdown({ estimatedUsd: 3, durationMs: 0 }).durationMs).toBeUndefined();
    expect(buildCostBreakdown({ estimatedUsd: 3, storageUsedBytes: -1 }).storageUsedBytes).toBeUndefined();
    expect(buildCostBreakdown({ estimatedUsd: 3, storageQuotaBytes: 0 }).storageQuotaBytes).toBeUndefined();
    expect(buildCostBreakdown({ estimatedUsd: 3, providerUnits: 2.7 }).providerUnits).toBe(2);
    expect(buildCostBreakdown({ estimatedUsd: 3, providerUnits: 0 }).providerUnits).toBeUndefined();
    expect(containsReservationId(['cost ok'])).toBe(false);
    expect(containsReservationId(['has res_abc'])).toBe(true);
    expect(containsReservationId(['Reservation detected'])).toBe(true);
  });
});
