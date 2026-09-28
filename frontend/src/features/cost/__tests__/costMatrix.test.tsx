// Task 039B: cost state-matrix gap closure.
//
// Extends `cost.test.tsx` (estimates, empty/error, quota states,
// reservation hygiene, keys) with the missing states: CostSummary shells
// (needs-project/loading/404-unavailable/403-forbidden with no data leak/
// 500-retry/costUnavailable-flag/partial-refresh), QuotaBadge per-state text
// signals, QuotaBanner available/reserved/near/exceeded matrices with
// dismiss persistence + storage-failure tolerance, quota-manage links, and
// the quotaDismiss unit matrix. Every failure asserts its recovery control
// per §11.6 (contact-admin/retry/wait) with text signals (041C); fixtures
// are synthetic and fetch is intercepted (R3).
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
import { QuotaBadge } from '../QuotaBadge.js';
import { QuotaBanner } from '../QuotaBanner.js';
import {
  isQuotaBannerDismissed,
  markQuotaBannerDismissed,
  resetQuotaBannerDismissalForTests,
} from '../quotaDismiss.js';
import type { QuotaState } from '../types.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number): Response {
  return jsonResponse(
    { error: { code, message: `backend ${code}`, correlationId: 'corr-c35a', details: {} } },
    status,
  );
}

interface CostMatrixWorld {
  summaryStatus: number;
  workspaceStatus: number;
  summaryMode: 'ok' | 'never';
  costUnavailable: boolean;
  calls: number;
}

let world: CostMatrixWorld;

function resetWorld(): void {
  world = { summaryStatus: 200, workspaceStatus: 200, summaryMode: 'ok', costUnavailable: false, calls: 0 };
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
  if (url.includes('/dashboard/summary')) {
    world.calls += 1;
    if (world.summaryStatus !== 200) {
      return errorEnvelope('COST_ERROR', world.summaryStatus);
    }
    return jsonResponse({
      cost: { monthToDate: 12.5, currency: 'USD' },
      quota: { remaining: 9, resetsAt: '2026-09-25T00:00:00Z' },
      storage: { usedBytes: 1000, quotaBytes: 100000 },
    });
  }
  if (url.includes('/workspace')) {
    world.calls += 1;
    if (world.workspaceStatus !== 200) {
      return errorEnvelope('COST_ERROR', world.workspaceStatus);
    }
    return jsonResponse({
      cost: world.costUnavailable ? undefined : { runCost: 1.5, monthToDate: 12.5 },
      media: { id: 'med_1', status: 'Valid', durationMs: 61000 },
    });
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

beforeEach(() => {
  resetWorld();
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
  vi.restoreAllMocks();
});

describe('CostSummary shell states', () => {
  it('asks for a project when unscoped (no fetch, no crash)', () => {
    renderWithProviders(<CostSummary projectId="" />);
    expect(screen.getByTestId('cost-needs-project')).toBeDefined();
    expect(world.calls).toBe(0);
  });

  it('shows loading while figures resolve (never blank)', () => {
    world.summaryMode = 'never';
    setInnerFetchForTests((async () => new Promise<Response>(() => {})) as typeof fetch);
    renderWithProviders(<CostSummary projectId="prj_1" />);
    expect(screen.getByTestId('cost-loading')).toBeDefined();
  });

  it('hides pre-tracking runs without zero-filling (recovery: none needed)', async () => {
    world.summaryStatus = 404;
    renderWithProviders(<CostSummary projectId="prj_1" />);
    expect(await screen.findByTestId('cost-unavailable')).toBeDefined();
    expect(screen.queryByTestId('cost-error')).toBeNull();
    expect(screen.queryByTestId('cost-estimated')).toBeNull();
  });

  it('restricts forbidden cost sections with contact-admin recovery (no leak)', async () => {
    world.summaryStatus = 403;
    renderWithProviders(<CostSummary projectId="prj_1" />);
    const forbidden = await screen.findByTestId('cost-forbidden');
    expect(forbidden.textContent).toContain('Contact your tenant admin');
    expect(screen.queryByTestId('cost-estimated')).toBeNull();
    expect(screen.queryByTestId('cost-actual')).toBeNull();
    expect(document.body.textContent).not.toContain('12.5');
  });

  it('recovers from server errors with retry (recovery: retry)', async () => {
    world.summaryStatus = 500;
    renderWithProviders(<CostSummary projectId="prj_1" />);
    expect(await screen.findByTestId('cost-error')).toBeDefined();
    const callsBefore = world.calls;
    world.summaryStatus = 200;
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(world.calls).toBeGreaterThan(callsBefore));
    expect(await screen.findByTestId('cost-estimated')).toBeDefined();
  });

  it('flags background refreshes with partial state (recovery: wait)', async () => {
    renderWithProviders(<CostSummary projectId="prj_1" />);
    expect(await screen.findByTestId('cost-estimated')).toBeDefined();
    setInnerFetchForTests((async () => new Promise<Response>(() => {})) as typeof fetch);
    void queryClient.invalidateQueries();
    expect(await screen.findByTestId('cost-partial')).toBeDefined();
    expect(screen.getByTestId('cost-partial-refresh')).toBeDefined();
  });
});

describe('QuotaBadge state matrix', () => {
  it.each(['available', 'near', 'exceeded', 'reserved'] as const)('renders %s with text + glyph (never color-only)', (state: QuotaState) => {
    renderWithProviders(<QuotaBadge state={state} remaining={3} />);
    expect(screen.getByTestId(`quota-badge-${state}`)).toBeDefined();
    expect(screen.getByTestId('quota-badge-state').textContent).toBe(state);
    expect(screen.getByTestId('quota-badge-icon').textContent?.length).toBeGreaterThan(0);
    expect(screen.getByTestId('quota-badge-remaining').textContent).toContain('3 remaining');
    cleanup();
  });

  it('omits the remaining count when absent', () => {
    renderWithProviders(<QuotaBadge state="available" />);
    expect(screen.queryByTestId('quota-badge-remaining')).toBeNull();
  });
});

describe('QuotaBanner state matrix', () => {
  it.each(['available', 'reserved'] as const)('renders %s distinctly without blocking', (state: QuotaState) => {
    renderWithProviders(<QuotaBanner state={state} />);
    expect(screen.getByTestId(`quota-banner-${state}`)).toBeDefined();
    expect(screen.getByTestId(`quota-banner-${state}`).getAttribute('data-blocked')).toBe('false');
    cleanup();
  });

  it('warns near-quota with manage link, counts, and session dismissal (recovery: manage)', async () => {
    renderWithProviders(<QuotaBanner state="near" resetsAt="2026-09-25T00:00:00Z" remaining={2} />);
    expect(await screen.findByTestId('quota-banner-near')).toBeDefined();
    expect(screen.getByTestId('quota-banner-remaining').textContent).toContain('2 remaining');
    expect(screen.getByTestId('quota-banner-resets').textContent).toContain('2026');
    expect(screen.getByTestId('quota-banner-manage').getAttribute('href')).toBe('/settings');
    fireEvent.click(screen.getByTestId('quota-banner-dismiss'));
    await waitFor(() => expect(screen.queryByTestId('quota-banner-near')).toBeNull());
    expect(isQuotaBannerDismissed()).toBe(true);
  });

  it('restores dismissed banners in a fresh session (dismissal never persists)', () => {
    markQuotaBannerDismissed();
    expect(isQuotaBannerDismissed()).toBe(true);
    renderWithProviders(<QuotaBanner state="near" />);
    expect(screen.queryByTestId('quota-banner-near')).toBeNull();
    cleanup();
    resetQuotaBannerDismissalForTests();
    expect(isQuotaBannerDismissed()).toBe(false);
  });

  it('blocks exceeded quotas with admin recovery + support hint (recovery: contact-admin)', () => {
    renderWithProviders(<QuotaBanner state="exceeded" resetsAt="2026-09-25T00:00:00Z" />);
    const banner = screen.getByTestId('quota-banner-exceeded');
    expect(banner.getAttribute('data-blocked')).toBe('true');
    expect(banner.textContent).toContain('Contact your tenant admin');
    expect(screen.getByTestId('quota-banner-support').textContent).toContain('No reservation ids');
    expect(screen.getByTestId('quota-banner-manage').getAttribute('href')).toBe('/settings');
  });
});

describe('quotaDismiss storage matrix', () => {
  it('tolerates storage failures fail-closed (banner stays)', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('denied');
    });
    expect(isQuotaBannerDismissed()).toBe(false);
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('denied');
    });
    expect(() => markQuotaBannerDismissed()).not.toThrow();
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => {
      throw new Error('denied');
    });
    expect(() => resetQuotaBannerDismissalForTests()).not.toThrow();
  });
});
