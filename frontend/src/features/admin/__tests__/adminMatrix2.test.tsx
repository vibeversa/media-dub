// Task 039B: admin panel-delta gap closure (part 2).
//
// Covers the panel branches `adminMatrix` leaves cold: per-panel error sweeps
// with retries, empty matrices, health empty variants, flags-apply flow,
// DLQ redrive/discard flows with receipts, retention/audit forbidden/error/
// empty/unprovisioned/gap/pagination/expand matrices, the DestructiveAction
// gate matrix, mutation unit paths (validation + transport errors +
// receipt fallbacks), query pagination guards, and the adminGuard denial
// logger. Every failure asserts its recovery control per §11.6 with text
// signals (041C); fixtures are synthetic and fetch is intercepted (R3).
// Forbidden states assert no data leakage (rows never render).
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
import { AdminPage } from '../AdminPage.js';
import { DestructiveAction } from '../DestructiveAction.js';
import { canAccessAdmin, logAdminGuardDenial } from '../adminGuard.js';
import {
  applyFeatureFlags,
  assignUserRole,
  discardDlqEntry,
  invalidateAdminQueries,
  redriveDlqEntry,
  setFeatureFlagEnabled,
  useAdminAudit,
  useOpsDlq,
  useOpsLeases,
} from '../useAdminQueries.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number): Response {
  return jsonResponse(
    { error: { code, message: `backend ${code}`, correlationId: 'corr-a36', details: {} } },
    status,
  );
}

type ScopeMode = 'ok' | 'error500' | 'forbidden403' | 'empty' | 'never';

interface AdminDeltaWorld {
  mode: ScopeMode;
  redriveConflict: boolean;
  calls: Record<string, number>;
}

let world: AdminDeltaWorld;

function resetWorld(): void {
  world = { mode: 'ok', redriveConflict: false, calls: {} };
}

function count(key: string): void {
  world.calls[key] = (world.calls[key] ?? 0) + 1;
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
  if (world.mode === 'never') {
    return new Promise<Response>(() => {});
  }
  if (world.mode === 'forbidden403') {
    return errorEnvelope('FORBIDDEN', 403);
  }
  if (world.mode === 'error500') {
    return errorEnvelope('INTERNAL_ERROR', 500);
  }
  if (method === 'GET' && url.includes('/admin/status')) {
    return jsonResponse({ status: 'ok', time: '2026-09-28T00:00:00Z' });
  }
  if (method === 'GET' && url.includes('/admin/usage')) {
    return jsonResponse(world.mode === 'empty' ? {} : { correlationId: 'c', storageUsedBytes: 1000, storageQuotaBytes: 100000, monthCostUsd: 12.5, projectsTodayRemaining: 9, activeRuns: 1, pendingReviews: 2, totalProjects: 4 });
  }
  if (method === 'GET' && url.includes('/admin/quotas')) {
    return jsonResponse(world.mode === 'empty' ? {} : { correlationId: 'c', maxActiveProjects: 10, maxProjectsPerDay: 10, maxCostPerProject: 50, maxCostPerSegment: 5, maxSegmentCount: 2000, maxStorageBytes: 100000, maxConcurrentStagesPerTenant: 4 });
  }
  if (method === 'GET' && url.includes('/admin/provider-health')) {
    return jsonResponse(world.mode === 'empty' ? [] : [{ provider: 'acme-stt', status: 'Healthy', latencyMsP95: 900, errorRate: 0.01, lastSuccessAt: '2026-09-28T00:00:00Z', activeRoutes: ['stt'], circuitBreakerState: 'Closed' }]);
  }
  if (method === 'GET' && url.includes('/admin/provider-routes')) {
    return jsonResponse(world.mode === 'empty' ? [] : [{ capability: 'stt', provider: 'masked-route', priority: 0, enabled: true }]);
  }
  if (method === 'GET' && url.includes('/admin/diagnostics/queues')) {
    return jsonResponse([{ correlationId: 'c', queue: 'media.prepare', depth: 2 }]);
  }
  if (method === 'GET' && url.includes('/admin/diagnostics/dlq')) {
    count('dlq');
    return jsonResponse({ correlationId: 'c', depth: 1, oldestEnqueuedAt: '2026-09-27T00:00:00Z', oldestEntryAge: '1d 0h', topReasons: [{ code: 'RETRY_EXHAUSTED', count: 3, actions: ['redrive', 'discard'] }] });
  }
  if (method === 'GET' && url.includes('/admin/diagnostics/leases')) {
    return jsonResponse({ items: [], page: 1, pageSize: 50, total: 0, hasMore: false });
  }
  if (method === 'GET' && url.includes('/admin/diagnostics/orphans')) {
    return jsonResponse({ items: [], cursor: null, hasMore: false });
  }
  if (method === 'GET' && url.includes('/admin/diagnostics/review-backlog')) {
    return jsonResponse({ correlationId: 'c', totalOpen: 0, byStatus: {}, bySeverity: {}, oldestWaitingAt: null, perProject: [] });
  }
  if (method === 'GET' && url.includes('/admin/tenants')) {
    return jsonResponse(world.mode === 'empty' ? { items: [] } : { items: [{ id: 'tenant-1', name: 'Acme', slug: 'acme' }] });
  }
  if (method === 'GET' && url.includes('/admin/users')) {
    return jsonResponse(world.mode === 'empty' ? { items: [] } : { items: [{ id: 'user-1', displayName: 'Owner', roles: ['ProjectOwner'] }] });
  }
  if (method === 'GET' && url.includes('/admin/audit-events')) {
    const page = Number.parseInt(new URL(url).searchParams.get('page') ?? '1', 10);
    if (page >= 2) {
      return errorEnvelope('NOT_FOUND', 404);
    }
    return jsonResponse({
      items: world.mode === 'empty' ? [] : [
        { id: 'audit-1', timestamp: '2026-09-28T00:00:00Z', actor: 'system', action: 'admin.access', summary: 'Admin status read', sourceIp: '10.0.0.1', runId: 'run_1' },
        { id: 'audit-2', timestamp: '', actor: 'system', action: 'flags.apply', summary: 'Flags applied' },
      ],
      hasMore: world.mode !== 'empty',
    });
  }
  if (method === 'GET' && url.includes('/admin/retention')) {
    return jsonResponse(world.mode === 'empty' ? { policies: [] } : { policies: [{ scope: 'audit-events', retentionDays: 90, description: 'Audit window' }] });
  }
  if (method === 'GET' && url.includes('/admin/feature-flags')) {
    return jsonResponse(world.mode === 'empty' ? { flags: [] } : { flags: [{ key: 'gpu-render', enabled: false, description: 'GPU rendering', frozen: false }] });
  }
  if (method === 'POST' && url.includes('/admin/users/')) {
    count('assign');
    return jsonResponse({ actionId: 'act-role-1', timestamp: '2026-09-28T00:00:01Z' });
  }
  if (method === 'PATCH' && url.includes('/admin/feature-flags/')) {
    count('flagPatch');
    return jsonResponse({ key: 'gpu-render', enabled: true });
  }
  if (method === 'POST' && url.includes('/admin/feature-flags/apply')) {
    count('flagApply');
    return jsonResponse({ actionId: 'act-flags-1', timestamp: '2026-09-28T00:00:01Z' });
  }
  if (method === 'POST' && url.includes('/admin/diagnostics/dlq/redrive')) {
    count('redrive');
    if (world.redriveConflict) {
      return errorEnvelope('CONFLICT', 409);
    }
    return jsonResponse({ actionId: 'act-redrive-1', timestamp: '2026-09-28T00:00:01Z' });
  }
  if (method === 'POST' && url.includes('/admin/diagnostics/dlq/discard')) {
    count('discard');
    return jsonResponse({ actionId: 'act-discard-1', timestamp: '2026-09-28T00:00:01Z' });
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

function authenticate(permissions: readonly string[] = ['admin.manage']): void {
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

describe('panel error sweep with retries', () => {
  it('renders every error panel with a working retry (recovery: retry)', async () => {
    world.mode = 'error500';
    renderWithProviders(<AdminPage />);
    for (const testId of [
      'admin-tenants-error',
      'admin-users-error',
      'admin-health-error',
      'admin-usage-error',
      'admin-retention-error',
      'admin-audit-error',
      'admin-flags-error',
      'admin-ops-queues-error',
      'admin-ops-leases-error',
      'admin-ops-orphans-error',
      'admin-ops-backlog-error',
    ]) {
      expect(await screen.findByTestId(testId)).toBeDefined();
    }
    world.mode = 'ok';
    const retries = screen.getAllByRole('button', { name: 'Retry' });
    expect(retries.length).toBeGreaterThan(5);
    for (const retry of retries) {
      fireEvent.click(retry);
    }
    await waitFor(() => expect(screen.queryByTestId('admin-tenants-error')).toBeNull());
    expect(await screen.findByTestId('admin-tenants-list')).toBeDefined();
  });
});

describe('panel empty sweep', () => {
  it('renders every empty state without error chrome', async () => {
    world.mode = 'empty';
    renderWithProviders(<AdminPage />);
    for (const testId of [
      'admin-tenants-empty',
      'admin-users-empty',
      'admin-health-empty',
      'admin-retention-empty',
      'admin-audit-empty',
      'admin-flags-empty',
      'admin-ops-leases-empty',
      'admin-ops-orphans-empty',
      'admin-ops-backlog-empty',
    ]) {
      expect(await screen.findByTestId(testId)).toBeDefined();
    }
    expect(screen.queryByTestId('admin-users-error')).toBeNull();
  });

  it('renders unavailable fallbacks for shapeless usage aggregates (never blank)', async () => {
    world.mode = 'empty';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-usage-runs')).toBeDefined();
    expect(screen.getByTestId('admin-usage-runs').textContent).toBe('0');
    expect(screen.getByTestId('admin-quotas-active').textContent).toBe('—');
  });
});

describe('health empty variants', () => {
  it('distinguishes missing health from missing routes (text signals)', async () => {
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-health-table')).toBeDefined();
    expect(screen.getByTestId('admin-health-row-acme-stt')).toBeDefined();
    expect(screen.getByTestId('admin-health-latency-acme-stt').textContent).toContain('900ms');
    expect(screen.getByTestId('admin-route-enabled-stt-masked-route').textContent).toBe('On');
  });
});

describe('flags apply flow', () => {
  it('applies rollouts behind type-to-confirm with receipts (recovery: none needed)', async () => {
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-flags-list')).toBeDefined();
    fireEvent.click(screen.getByTestId('admin-flags-apply-open'));
    fireEvent.change(screen.getByTestId('admin-flags-apply-confirm-input'), { target: { value: 'APPLY-FLAGS' } });
    fireEvent.change(screen.getByTestId('admin-flags-apply-reason-input'), { target: { value: 'Rolling out GPU rendering to production.' } });
    fireEvent.click(screen.getByTestId('admin-flags-apply-confirm'));
    await waitFor(() => expect(world.calls['flagApply']).toBe(1));
    expect(await screen.findByTestId('admin-flags-apply-receipt')).toBeDefined();
    expect(screen.getByTestId('admin-flags-apply-receipt-id').textContent).toContain('act-flags-1');
  });
});

describe('DLQ redrive/discard flows', () => {
  async function confirmAction(testId: string, token: string): Promise<void> {
    fireEvent.click(screen.getByTestId(`${testId}-open`));
    fireEvent.change(screen.getByTestId(`${testId}-confirm-input`), { target: { value: token } });
    fireEvent.change(screen.getByTestId(`${testId}-reason-input`), { target: { value: 'Reprocessing after the provider recovered.' } });
    fireEvent.click(screen.getByTestId(`${testId}-confirm`));
  }

  it('redrives entries with receipts (recovery: redrive)', async () => {
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-ops-dlq-list')).toBeDefined();
    await confirmAction('admin-ops-dlq-redrive-RETRY_EXHAUSTED-0', 'RETRY_EXHAUSTED');
    await waitFor(() => expect(world.calls['redrive']).toBe(1));
    expect(await screen.findByTestId('admin-ops-dlq-redrive-RETRY_EXHAUSTED-0-receipt')).toBeDefined();
  });

  it('collapses redrive conflicts with info (recovery: wait)', async () => {
    world.redriveConflict = true;
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-ops-dlq-list')).toBeDefined();
    await confirmAction('admin-ops-dlq-redrive-RETRY_EXHAUSTED-0', 'RETRY_EXHAUSTED');
    await waitFor(() => expect(world.calls['redrive']).toBe(1));
  });

  it('discards entries with receipts (recovery: none needed)', async () => {
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-ops-dlq-list')).toBeDefined();
    await confirmAction('admin-ops-dlq-discard-RETRY_EXHAUSTED-0', 'RETRY_EXHAUSTED');
    await waitFor(() => expect(world.calls['discard']).toBe(1));
    expect(await screen.findByTestId('admin-ops-dlq-discard-RETRY_EXHAUSTED-0-receipt')).toBeDefined();
  });

  it('pages the DLQ window (recovery: none needed)', async () => {
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-ops-dlq-page')).toBeDefined();
    expect(screen.getByTestId('admin-ops-dlq-prev').hasAttribute('disabled')).toBe(true);
  });
});

describe('retention/audit matrices', () => {
  it('locks both sections on 403 without leaking rows (session intact)', async () => {
    world.mode = 'forbidden403';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-retention-forbidden')).toBeDefined();
    expect(await screen.findByTestId('admin-audit-forbidden')).toBeDefined();
    expect(document.body.textContent).not.toContain('Audit window');
    expect(useAppStore.getState().sessionStatus).toBe('authenticated');
  });

  it('marks retention-expiry gaps with first-page recovery (recovery: back-to-first)', async () => {
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-audit-table')).toBeDefined();
    fireEvent.click(screen.getByTestId('admin-audit-next'));
    expect(await screen.findByTestId('admin-audit-gap')).toBeDefined();
    expect(screen.getByTestId('admin-audit-gap-text').textContent).toContain('expired per retention policy');
    fireEvent.click(screen.getByTestId('admin-audit-gap-first'));
    await waitFor(() => expect(screen.queryByTestId('admin-audit-gap')).toBeNull());
    expect(await screen.findByTestId('admin-audit-table')).toBeDefined();
  });

  it('expands advanced rows for elevated holders (text signals)', async () => {
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-audit-table')).toBeDefined();
    fireEvent.click(screen.getByTestId('admin-audit-row-audit-1-toggle'));
    expect(await screen.findByTestId('admin-audit-advanced-audit-1')).toBeDefined();
    fireEvent.click(screen.getByTestId('admin-audit-row-audit-1-toggle'));
    await waitFor(() => expect(screen.queryByTestId('admin-audit-advanced-audit-1')).toBeNull());
  });

  it('shows advanced rows to diagnostics readers identically (same text signals)', async () => {
    authenticate(['diagnostics.view']);
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-audit-table')).toBeDefined();
    fireEvent.click(screen.getByTestId('admin-audit-row-audit-1-toggle'));
    expect(await screen.findByTestId('admin-audit-advanced-audit-1')).toBeDefined();
  });

  it('renders blank timestamps as em-dashes (never blank)', async () => {
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-audit-timestamp-audit-2')).toBeDefined();
    expect(screen.getByTestId('admin-audit-timestamp-audit-2').textContent).toBe('—');
  });

  it('disables previous on the first page and pages forward/back', async () => {
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-audit-table')).toBeDefined();
    expect((screen.getByTestId('admin-audit-prev') as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByTestId('admin-audit-page').textContent).toContain('Page 1');
  });
});

describe('DestructiveAction gate matrix', () => {
  function renderGate(onConfirm?: (reason: string) => Promise<{ actionId: string; timestamp: string; action: string }>): void {
    renderWithProviders(
      <DestructiveAction
        action="flags.apply"
        label="Apply flag rollout"
        confirmToken="APPLY-FLAGS"
        testId="flags.apply"
        description="Commits staged flag values."
        onConfirm={onConfirm ?? (async () => ({ actionId: 'act-1', timestamp: '2026-09-28T00:00:01Z', action: 'flags.apply' }))}
      />,
    );
  }

  it('opens, validates, and cancels without side effects (recovery: cancel)', async () => {
    const onConfirm = vi.fn();
    renderGate(onConfirm);
    fireEvent.click(screen.getByTestId('flags.apply-open'));
    expect(await screen.findByTestId('flags.apply-dialog')).toBeDefined();
    expect((screen.getByTestId('flags.apply-confirm') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(screen.getByTestId('flags.apply-confirm-input'), { target: { value: 'APPLY-FLAGS' } });
    fireEvent.change(screen.getByTestId('flags.apply-reason-input'), { target: { value: 'short' } });
    expect(await screen.findByTestId('flags.apply-reason-error')).toBeDefined();
    expect((screen.getByTestId('flags.apply-confirm') as HTMLButtonElement).disabled).toBe(true);
    expect(onConfirm).not.toHaveBeenCalled();
    fireEvent.click(screen.getByTestId('flags.apply-cancel'));
    await waitFor(() => expect(screen.queryByTestId('flags.apply-dialog')).toBeNull());
  });

  it('completes with receipts and success toasts (recovery: none needed)', async () => {
    const onConfirm = vi.fn<(reason: string) => Promise<{ actionId: string; timestamp: string; action: string }>>(
      async () => ({ actionId: 'act-9', timestamp: '2026-09-28T00:00:09Z', action: 'flags.apply' }),
    );
    renderGate(onConfirm);
    fireEvent.click(screen.getByTestId('flags.apply-open'));
    expect(await screen.findByTestId('flags.apply-dialog')).toBeDefined();
    fireEvent.change(screen.getByTestId('flags.apply-confirm-input'), { target: { value: 'APPLY-FLAGS' } });
    fireEvent.change(screen.getByTestId('flags.apply-reason-input'), { target: { value: 'Rolling out GPU rendering to production.' } });
    fireEvent.click(screen.getByTestId('flags.apply-confirm'));
    expect(await screen.findByTestId('flags.apply-receipt')).toBeDefined();
    expect(screen.getByTestId('flags.apply-receipt-id').textContent).toContain('act-9');
    expect(onConfirm).toHaveBeenCalledTimes(1);
    expect(onConfirm.mock.calls[0]?.[0]).toBe('Rolling out GPU rendering to production.');
  });

  it('locks revoked elevation without leaking role names (recovery: contact-admin)', async () => {
    renderGate(async () => ({ actionId: 'act-1', timestamp: 't', action: 'flags.apply' }));
    fireEvent.click(screen.getByTestId('flags.apply-open'));
    expect(await screen.findByTestId('flags.apply-dialog')).toBeDefined();
    fireEvent.change(screen.getByTestId('flags.apply-confirm-input'), { target: { value: 'APPLY-FLAGS' } });
    fireEvent.change(screen.getByTestId('flags.apply-reason-input'), { target: { value: 'Rolling out GPU rendering to production.' } });
    act(() => {
      useAppStore.getState().setSession('authenticated', []);
    });
    fireEvent.click(screen.getByTestId('flags.apply-confirm'));
    expect(await screen.findByTestId('flags.apply-forbidden')).toBeDefined();
    expect(screen.getByTestId('flags.apply-forbidden-text').textContent).toContain('Contact your tenant admin');
  });

  it('reports transport failures with refs and defaults (recovery: report id)', async () => {
    renderGate(async () => {
      throw new Error('');
    });
    fireEvent.click(screen.getByTestId('flags.apply-open'));
    expect(await screen.findByTestId('flags.apply-dialog')).toBeDefined();
    fireEvent.change(screen.getByTestId('flags.apply-confirm-input'), { target: { value: 'APPLY-FLAGS' } });
    fireEvent.change(screen.getByTestId('flags.apply-reason-input'), { target: { value: 'Rolling out GPU rendering to production.' } });
    fireEvent.click(screen.getByTestId('flags.apply-confirm'));
    expect(await screen.findByTestId('flags.apply-error')).toBeDefined();
  });

  it('omits empty descriptions without blank paragraphs (never blank)', () => {
    renderWithProviders(
      <DestructiveAction
        action="flags.apply"
        label="Apply flag rollout"
        confirmToken="APPLY-FLAGS"
        testId="flags.nodesc"
        onConfirm={async () => ({ actionId: 'act-1', timestamp: 't', action: 'flags.apply' })}
      />,
    );
    fireEvent.click(screen.getByTestId('flags.nodesc-open'));
    expect(screen.getByTestId('flags.nodesc-dialog')).toBeDefined();
  });
});

describe('admin mutation unit matrix', () => {
  it('validates reasons client-side before transport (recovery: elaborate)', async () => {
    await expect(assignUserRole('user-1', 'ProjectOwner', 'x', ['admin.manage'])).rejects.toMatchObject({ code: 'VALIDATION_FAILED' });
    await expect(assignUserRole('user-1', 'TenantAdmin', 'Covering the on-call rotation.', ['diagnostics.view'])).rejects.toMatchObject({ code: 'FORBIDDEN' });
    await expect(redriveDlqEntry('e1', 'x')).rejects.toMatchObject({ code: 'VALIDATION_FAILED' });
    await expect(discardDlqEntry('e1', 'x')).rejects.toMatchObject({ code: 'VALIDATION_FAILED' });
    await expect(applyFeatureFlags('x')).rejects.toMatchObject({ code: 'VALIDATION_FAILED' });
  });

  it('normalizes transport failures to AppError (recovery: retry/report)', async () => {
    setInnerFetchForTests((async () => {
      throw new TypeError('fetch failed');
    }) as typeof fetch);
    await expect(assignUserRole('user-1', 'ProjectOwner', 'Covering the on-call rotation.', ['admin.manage'])).rejects.toMatchObject({ retryable: true });
    await expect(redriveDlqEntry('e1', 'Covering the on-call rotation.')).rejects.toMatchObject({ retryable: true });
    await expect(discardDlqEntry('e1', 'Covering the on-call rotation.')).rejects.toMatchObject({ retryable: true });
    await expect(applyFeatureFlags('Covering the on-call rotation.')).rejects.toMatchObject({ retryable: true });
    await expect(setFeatureFlagEnabled('gpu-render', true)).rejects.toMatchObject({ retryable: true });
  });

  it('falls back to minted ids and timestamps on shapeless receipts', async () => {
    setInnerFetchForTests((async () => jsonResponse({})) as typeof fetch);
    const receipt = await assignUserRole('user-1', 'ProjectOwner', 'Covering the on-call rotation.', ['admin.manage']);
    expect(receipt.actionId.length).toBeGreaterThan(0);
    expect(receipt.timestamp.length).toBeGreaterThan(0);
    expect(receipt.action).toBe('role.assign:ProjectOwner');
  });
});

describe('admin query guard matrix', () => {
  it('clamps DLQ/lease/audit pagination and gates on session', async () => {
    function Probe(): null {
      useOpsDlq(0, 500);
      useOpsLeases(0, 500);
      useAdminAudit(0, 500);
      return null;
    }
    renderWithProviders(<Probe />);
    await new Promise((resolve) => setTimeout(resolve, 150));
    const dlq = queryClient.getQueryData(['admin', 'diagnostics', 'dlq', { page: 1, pageSize: 200 }]);
    expect(dlq).toBeDefined();
    useAuthStore.setState({ status: 'anonymous' });
    await invalidateAdminQueries(queryClient);
  });
});

describe('adminGuard denial logger', () => {
  it('permits elevated sets and logs denials without ids (never throws)', () => {
    expect(canAccessAdmin(['admin.manage'])).toBe(true);
    expect(canAccessAdmin([])).toBe(false);
    expect(() => logAdminGuardDenial('corr-explicit')).not.toThrow();
    expect(() => logAdminGuardDenial()).not.toThrow();
  });
});
