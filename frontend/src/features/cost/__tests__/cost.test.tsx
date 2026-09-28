import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
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
import { queryKeys } from '../../../api/queryKeys/index.js';
import { CostSummary } from '../CostSummary.js';
import { QuotaBadge } from '../QuotaBadge.js';
import { QuotaBanner } from '../QuotaBanner.js';
import { resetQuotaBannerDismissalForTests } from '../quotaDismiss.js';
import {
  buildCostBreakdown,
  buildQuotaView,
  containsReservationId,
  deriveQuotaState,
  iconForQuotaState,
  isQuotaBlocking,
  toneForQuotaState,
} from '../types.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number): Response {
  return jsonResponse(
    { error: { code, message: `backend ${code}`, correlationId: 'corr-c35a', details: {} } },
    status,
  );
}

interface CostWorld {
  summaryBody: Record<string, unknown>;
  workspaceBody: Record<string, unknown>;
  summaryStatus: number;
  workspaceStatus: number;
  calls: { summary: number; workspace: number };
}

function newWorld(overrides: Partial<CostWorld> = {}): CostWorld {
  return {
    summaryBody: {
      cost: { monthToDate: 12.5, currency: 'USD' },
      quota: { remaining: 9, resetsAt: '2026-09-25T00:00:00Z' },
      storage: { usedBytes: 1000, quotaBytes: 100000 },
    },
    workspaceBody: {
      cost: { runCost: 1.5, monthToDate: 12.5 },
      media: { id: 'med_1', status: 'Valid', durationMs: 61000 },
    },
    summaryStatus: 200,
    workspaceStatus: 200,
    calls: { summary: 0, workspace: 0 },
    ...overrides,
  };
}

let world: CostWorld = newWorld();

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
  if (method === 'GET' && url.includes('/dashboard/summary')) {
    world.calls.summary += 1;
    if (world.summaryStatus !== 200) {
      return errorEnvelope('COST_ERROR', world.summaryStatus);
    }
    return jsonResponse(world.summaryBody);
  }
  if (method === 'GET' && url.includes('/workspace')) {
    world.calls.workspace += 1;
    if (world.workspaceStatus !== 200) {
      return errorEnvelope('COST_ERROR', world.workspaceStatus);
    }
    return jsonResponse(world.workspaceBody);
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

describe('estimate labeling (R3)', () => {
  it('labels preflight estimates and distinguishes metered actuals', async () => {
    renderWithProviders(<CostSummary projectId="prj_1" />);
    expect(await screen.findByTestId('cost-estimate-label')).toBeDefined();
    expect(screen.getByTestId('cost-estimate-label').textContent).toBe('Estimate');
    expect(screen.getByTestId('cost-estimated').textContent).toContain('Estimate');
    expect(screen.getByTestId('cost-estimate-note').textContent).toMatch(/planning figures only/);
    expect(screen.getByTestId('cost-actual')).toBeDefined();
    expect(screen.getByTestId('cost-actual-month')).toBeDefined();
  });

  it('renders duration, provider units, and storage breakdown', async () => {
    renderWithProviders(<CostSummary projectId="prj_1" />);
    expect(await screen.findByTestId('cost-duration')).toBeDefined();
    expect(screen.getByTestId('cost-duration').textContent).not.toBe('');
    expect(screen.getByTestId('cost-provider-units')).toBeDefined();
    expect(screen.getByTestId('cost-provider-units').textContent).toContain('segments');
    expect(screen.getByTestId('cost-storage')).toBeDefined();
  });
});

describe('empty and error states (R5)', () => {
  it('shows unavailable instead of zero-filling when cost predates tracking', async () => {
    world.summaryBody = {};
    world.workspaceBody = {};
    renderWithProviders(<CostSummary projectId="prj_1" />);
    expect(await screen.findByTestId('cost-unavailable')).toBeDefined();
    expect(screen.queryByTestId('cost-actual')).toBeNull();
  });

  it('shows a 404 unavailable state for missing cost sections', async () => {
    world.summaryStatus = 404;
    renderWithProviders(<CostSummary projectId="prj_1" />);
    expect(await screen.findByTestId('cost-unavailable')).toBeDefined();
  });

  it('shows a forbidden state without bypassing on 403', async () => {
    world.summaryStatus = 403;
    renderWithProviders(<CostSummary projectId="prj_1" />);
    expect(await screen.findByTestId('cost-forbidden')).toBeDefined();
    expect(screen.queryByTestId('cost-actual')).toBeNull();
    expect(screen.getByTestId('cost-forbidden').textContent).toMatch(/authorized roles only/);
  });

  it('shows an error with retry on 500', async () => {
    world.summaryStatus = 500;
    renderWithProviders(<CostSummary projectId="prj_1" />);
    expect(await screen.findByTestId('cost-error')).toBeDefined();
  });

  it('shows a loading skeleton while pending', async () => {
    let resolveFetch!: (value: Response) => void;
    const gate = new Promise<Response>((resolve) => {
      resolveFetch = resolve;
    });
    setInnerFetchForTests((() => gate) as typeof fetch);
    renderWithProviders(<CostSummary projectId="prj_1" />);
    expect(await screen.findByTestId('cost-loading')).toBeDefined();
    resolveFetch(jsonResponse(world.summaryBody));
  });
});

describe('quota states (available|near|exceeded|reserved)', () => {
  it('maps quota states to distinct treatments purely', () => {
    expect(deriveQuotaState({ remaining: 9, usedBytes: 10, quotaBytes: 100 })).toBe('available');
    expect(deriveQuotaState({ remaining: 2, usedBytes: 10, quotaBytes: 100 })).toBe('near');
    expect(deriveQuotaState({ remaining: 0 })).toBe('exceeded');
    expect(deriveQuotaState({ remaining: 9, usedBytes: 100, quotaBytes: 100 })).toBe('exceeded');
    expect(deriveQuotaState({ remaining: 9, usedBytes: 10, quotaBytes: 100, reservedUsd: 1.5 })).toBe('reserved');
    expect(isQuotaBlocking('exceeded')).toBe(true);
    expect(isQuotaBlocking('near')).toBe(false);
    expect(isQuotaBlocking('available')).toBe(false);
    expect(isQuotaBlocking('reserved')).toBe(false);
    expect(toneForQuotaState('available')).toBe('success');
    expect(toneForQuotaState('near')).toBe('warning');
    expect(toneForQuotaState('exceeded')).toBe('error');
    expect(toneForQuotaState('reserved')).toBe('info');
    expect(iconForQuotaState('available')).toBe('✓');
    expect(iconForQuotaState('exceeded')).toBe('✕');
  });

  it('renders the quota badge distinctly per state', () => {
    const { unmount } = render(
      <MemoryRouter>
        <QuotaBadge state="available" remaining={9} />
      </MemoryRouter>,
    );
    expect(screen.getByTestId('quota-badge-available')).toBeDefined();
    expect(screen.getByTestId('quota-badge-state').textContent).toBe('available');
    unmount();
    cleanup();
    render(
      <MemoryRouter>
        <QuotaBadge state="exceeded" remaining={0} />
      </MemoryRouter>,
    );
    expect(screen.getByTestId('quota-badge-exceeded')).toBeDefined();
    expect(screen.getByTestId('quota-badge-exceeded').getAttribute('data-tone')).toBe('error');
  });

  it('renders each quota banner distinctly with recovery actions', () => {
    const { unmount } = render(
      <MemoryRouter>
        <QuotaBanner state="exceeded" remaining={0} resetsAt="2026-09-25T00:00:00Z" />
      </MemoryRouter>,
    );
    expect(screen.getByTestId('quota-banner-exceeded')).toBeDefined();
    expect(screen.getByTestId('quota-banner-exceeded').getAttribute('data-blocked')).toBe('true');
    expect(screen.getByTestId('quota-banner-explanation')).toBeDefined();
    expect(screen.getByTestId('quota-banner-explanation').textContent).toMatch(/Contact your tenant admin/);
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

  it('renders reserved as informational without blocking', () => {
    render(
      <MemoryRouter>
        <QuotaBanner state="reserved" />
      </MemoryRouter>,
    );
    expect(screen.getByTestId('quota-banner-reserved')).toBeDefined();
    expect(screen.getByTestId('quota-banner-reserved').getAttribute('data-blocked')).toBe('false');
    expect(screen.getByTestId('quota-banner-reserved-note').textContent).toMatch(/Informational only/);
  });
});

describe('reservation-id hygiene (R4)', () => {
  it('never carries reservation ids through cost shapes', () => {
    expect(containsReservationId(['run_1', '1.50 USD'])).toBe(false);
    expect(containsReservationId(['res_abc123'])).toBe(true);
    expect(containsReservationId(['Reservation hold 2.00'])).toBe(true);
    const cost = buildCostBreakdown({
      estimatedUsd: 1.2,
      actualRunUsd: 1.5,
      currency: 'USD',
      providerUnits: 13,
    });
    expect(cost.providerUnits).toBe(13);
    expect(JSON.stringify(cost).toLowerCase()).not.toContain('res_');
    const quota = buildQuotaView({ remaining: 9, usedBytes: 10, quotaBytes: 100, reservedUsd: 1.5 });
    expect(quota.state).toBe('reserved');
    expect(JSON.stringify(quota).toLowerCase()).not.toContain('res_abc');
  });

  it('renders no reservation ids in the cost summary DOM', async () => {
    renderWithProviders(<CostSummary projectId="prj_1" />);
    expect(await screen.findByTestId('cost-summary')).toBeDefined();
    const text = screen.getByTestId('cost-summary').textContent ?? '';
    expect(text.toLowerCase()).not.toContain('res_');
    expect(text.toLowerCase()).not.toContain('reservation');
  });
});

describe('tenant-scoped query keys', () => {
  it('keeps activity and cost keys project-scoped without leakage', () => {
    expect(queryKeys.activity.list('prj_1', { page: 1, pageSize: 20 })).toEqual(
      queryKeys.activity.list('prj_1', { page: 1, pageSize: 20 }),
    );
    expect(queryKeys.activity.all('prj_1')).not.toEqual(queryKeys.activity.all('prj_2'));
    expect(queryKeys.cost.detail('prj_1')).toEqual(queryKeys.cost.detail('prj_1'));
    expect(queryKeys.cost.detail('prj_1')).not.toEqual(queryKeys.cost.detail('prj_2'));
  });
});
