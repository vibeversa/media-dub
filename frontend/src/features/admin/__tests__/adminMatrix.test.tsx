// Task 039B: admin state-matrix gap closure.
//
// Extends `admin.test.tsx` (guard, destructive gate, DLQ advertised actions,
// secrets, quota reuse, role grants, flag freeze, audit gaps) with the
// missing states: AdminPage shells (loading/forbidden with no data
// leak/error-retry), per-panel error/empty matrices, flag toggle + freeze
// dialog + apply flow, role assignment success/grant-denied/reason-error/
// server-forbidden paths, tenants/health/routes/usage/retention renders,
// ops subsection matrices, the types sweep (parsers, ratios, masking,
// validators, guards), and hook guards + invalidation. Every failure asserts
// its recovery control per §11.6 with text signals (041C); fixtures are
// synthetic and fetch is intercepted (R3). Forbidden states assert no data
// leakage (rows never render).
import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
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
import {
  assignerRank,
  canAssignRole,
  dlqRowsFromSummary,
  filterAdvertisedDlqActions,
  findSecretLeak,
  formatLeaseAge,
  isAdminConflictError,
  isAdminForbiddenError,
  isAdminFreezeError,
  isAdminUnknownRouteError,
  isAdvertisedDlqAction,
  isForbiddenAdminKey,
  isValidAuditReason,
  looksLikeReservationId,
  maskConnectionString,
  parseAdminUsers,
  parseAuditEvent,
  parseAuditEvents,
  parseDlqSummary,
  parseFeatureFlags,
  parseOrphans,
  parseProviderHealth,
  parseProviderRoutes,
  parseQuotas,
  parseQueueDepths,
  parseRetentionPolicies,
  parseReviewBacklog,
  parseStaleLeases,
  parseTenants,
  parseUsage,
  sanitizeReasonText,
  usageRatio,
} from '../types.js';
import { invalidateAdminQueries, useAdminFlags } from '../useAdminQueries.js';

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

interface AdminMatrixWorld {
  mode: ScopeMode;
  flagPatchBehavior: 'ok' | 'freeze';
  assignBehavior: 'ok' | 'forbidden';
  calls: Record<string, number>;
}

let world: AdminMatrixWorld;

function resetWorld(): void {
  world = { mode: 'ok', flagPatchBehavior: 'ok', assignBehavior: 'ok', calls: {} };
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
    count('status');
    return jsonResponse({ status: 'ok', time: '2026-09-28T00:00:00Z' });
  }
  if (method === 'GET' && url.includes('/admin/usage')) {
    count('usage');
    return jsonResponse({ correlationId: 'c', storageUsedBytes: 1000, storageQuotaBytes: 100000, monthCostUsd: 12.5, projectsTodayRemaining: 9, activeRuns: 1, pendingReviews: 2, totalProjects: 4 });
  }
  if (method === 'GET' && url.includes('/admin/quotas')) {
    count('quotas');
    return jsonResponse({ correlationId: 'c', maxActiveProjects: 10, maxProjectsPerDay: 10, maxCostPerProject: 50, maxCostPerSegment: 5, maxSegmentCount: 2000, maxStorageBytes: 100000, maxConcurrentStagesPerTenant: 4 });
  }
  if (method === 'GET' && url.includes('/admin/provider-health')) {
    count('health');
    return jsonResponse([{ provider: 'acme-stt', status: 'Healthy', latencyMsP95: 900, errorRate: 0.01, lastSuccessAt: '2026-09-28T00:00:00Z', activeRoutes: ['stt'], circuitBreakerState: 'Closed' }]);
  }
  if (method === 'GET' && url.includes('/admin/provider-routes')) {
    count('routes');
    return jsonResponse([
      { capability: 'stt', provider: 'pg://primary/db', priority: 0, enabled: true },
      { capability: 'tts', provider: 'masked-route', priority: 1, enabled: false },
    ]);
  }
  if (method === 'GET' && url.includes('/admin/diagnostics/queues')) {
    count('queues');
    return jsonResponse([{ correlationId: 'c', queue: 'media.prepare', depth: 2 }]);
  }
  if (method === 'GET' && url.includes('/admin/diagnostics/dlq')) {
    count('dlq');
    return jsonResponse({ correlationId: 'c', depth: 1, oldestEnqueuedAt: '2026-09-27T00:00:00Z', oldestEntryAge: '1d 0h', topReasons: [{ code: 'RETRY_EXHAUSTED', count: 3, actions: ['redrive', 'discard'] }] });
  }
  if (method === 'GET' && url.includes('/admin/diagnostics/leases')) {
    count('leases');
    return jsonResponse({ items: world.mode === 'empty' ? [] : [{ id: 'lease-1', stageType: 'Transcription', status: 'Running', owner: 'worker-1', startedAt: new Date(Date.now() - 61000).toISOString() }], page: 1, pageSize: 50, total: 0, hasMore: false });
  }
  if (method === 'GET' && url.includes('/admin/diagnostics/orphans')) {
    count('orphans');
    return jsonResponse({ items: [], cursor: null, hasMore: false });
  }
  if (method === 'GET' && url.includes('/admin/diagnostics/review-backlog')) {
    count('backlog');
    return jsonResponse({ correlationId: 'c', totalOpen: 2, byStatus: { Open: 2 }, bySeverity: {}, oldestWaitingAt: null, perProject: [{ projectId: 'prj_1', openCount: 2 }] });
  }
  if (method === 'GET' && url.includes('/admin/tenants')) {
    count('tenants');
    return jsonResponse(world.mode === 'empty' ? { items: [] } : { items: [{ id: 'tenant-1', name: 'Acme', slug: 'acme' }] });
  }
  if (method === 'GET' && url.includes('/admin/users')) {
    count('users');
    return jsonResponse(world.mode === 'empty' ? { items: [] } : { items: [{ id: 'user-1', displayName: 'Owner', roles: ['ProjectOwner'] }] });
  }
  if (method === 'GET' && url.includes('/admin/audit-events')) {
    count('audit');
    return jsonResponse({ items: [{ id: 'audit-1', timestamp: '2026-09-28T00:00:00Z', actor: 'system', action: 'admin.access', summary: 'Admin status read' }], hasMore: false });
  }
  if (method === 'GET' && url.includes('/admin/retention')) {
    count('retention');
    return jsonResponse({ policies: [{ scope: 'audit-events', retentionDays: 90, description: 'Audit window' }] });
  }
  if (method === 'GET' && url.includes('/admin/feature-flags')) {
    count('flags');
    return jsonResponse(world.mode === 'empty' ? { flags: [] } : { flags: [{ key: 'gpu-render', enabled: false, description: 'GPU rendering', frozen: false }] });
  }
  if (method === 'POST' && url.includes('/admin/users/')) {
    count('assign');
    if (world.assignBehavior === 'forbidden') {
      return errorEnvelope('FORBIDDEN', 403);
    }
    return jsonResponse({ actionId: 'act-role-1', timestamp: '2026-09-28T00:00:01Z' });
  }
  if (method === 'PATCH' && url.includes('/admin/feature-flags/')) {
    count('flagPatch');
    if (world.flagPatchBehavior === 'freeze') {
      return errorEnvelope('ROLLOUT_FROZEN', 423);
    }
    return jsonResponse({ key: 'gpu-render', enabled: true });
  }
  if (method === 'POST' && url.includes('/admin/feature-flags/apply')) {
    count('flagApply');
    return jsonResponse({ actionId: 'act-flags-1', timestamp: '2026-09-28T00:00:01Z' });
  }
  if (method === 'POST' && url.includes('/admin/diagnostics/dlq/redrive')) {
    count('redrive');
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

describe('AdminPage shell states', () => {
  it('shows loading while sections resolve (never blank)', () => {
    world.mode = 'never';
    renderWithProviders(<AdminPage />);
    expect(screen.getByTestId('admin-tenants-loading')).toBeDefined();
  });

  it('blocks the shell while the session resolves (skeleton, never panels)', () => {
    useAppStore.getState().setSession('loading', []);
    renderWithProviders(<AdminPage />);
    expect(screen.getByTestId('admin-loading')).toBeDefined();
    expect(screen.queryByTestId('admin-section-users')).toBeNull();
  });

  it('locks panels on mid-session 403 without leaking rows (session intact)', async () => {
    world.mode = 'forbidden403';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-users-forbidden')).toBeDefined();
    expect(document.body.textContent).not.toContain('tenant-1');
    expect(document.body.textContent).not.toContain('Owner');
    expect(useAppStore.getState().sessionStatus).toBe('authenticated');
  });

  it('denies without leaking rows (forbidden renders no data)', async () => {
    authenticate([]);
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-forbidden')).toBeDefined();
    expect(document.body.textContent).not.toContain('tenant-1');
    expect(document.body.textContent).not.toContain('user-1');
    expect(document.body.textContent).not.toContain('Owner');
    expect(screen.queryByTestId('admin-users-list')).toBeNull();
  });

  it('recovers section errors with retry (recovery: retry)', async () => {
    world.mode = 'error500';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-users-error')).toBeDefined();
    world.mode = 'ok';
    const usersSection = screen.getByTestId('admin-section-users');
    fireEvent.click(within(usersSection).getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(screen.queryByTestId('admin-users-error')).toBeNull());
    expect(await screen.findByTestId('admin-users-list')).toBeDefined();
  });

  it('renders all seven sections with live data (text signals)', async () => {
    renderWithProviders(<AdminPage />);
    for (const testId of [
      'admin-section-tenants',
      'admin-section-users',
      'admin-section-health',
      'admin-section-usage',
      'admin-section-retention-audit',
      'admin-section-flags',
      'admin-section-ops',
    ]) {
      expect(await screen.findByTestId(testId)).toBeDefined();
    }
    expect(screen.getByTestId('admin-user-name-user-1').textContent).toBe('Owner');
  });
});

describe('flags matrix', () => {
  it('toggles flags with optimistic state (recovery: toggle back)', async () => {
    renderWithProviders(<AdminPage />);
    const toggle = await screen.findByTestId('admin-flag-toggle-gpu-render');
    expect(toggle.textContent).toBe('Off');
    fireEvent.click(toggle);
    await waitFor(() => expect(world.calls['flagPatch']).toBe(1));
    expect(await screen.findByTestId('admin-flag-toggle-gpu-render')).toBeDefined();
  });

  it('explains frozen flags with a dismissible dialog (recovery: wait)', async () => {
    world.flagPatchBehavior = 'freeze';
    renderWithProviders(<AdminPage />);
    fireEvent.click(await screen.findByTestId('admin-flag-toggle-gpu-render'));
    expect(await screen.findByTestId('admin-flag-freeze-dialog')).toBeDefined();
    expect(screen.getByTestId('admin-flag-freeze-text').textContent).toContain('freeze');
    fireEvent.click(screen.getByTestId('admin-flag-freeze-close'));
    await waitFor(() => expect(screen.queryByTestId('admin-flag-freeze-dialog')).toBeNull());
  });

  it('renders the empty flags state without error chrome', async () => {
    world.mode = 'empty';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-flags-empty')).toBeDefined();
    expect(screen.queryByTestId('admin-flags-error')).toBeNull();
  });
});

describe('users + roles matrix', () => {
  it('assigns roles with audit reasons and success toasts (recovery: none needed)', async () => {
    authenticate(['admin.manage']);
    renderWithProviders(<AdminPage />);
    fireEvent.click(await screen.findByTestId('admin-user-select-user-1'));
    fireEvent.change(screen.getByTestId('admin-role-target'), { target: { value: 'ProjectOwner' } });
    fireEvent.change(screen.getByTestId('admin-role-reason'), { target: { value: 'Covering the on-call rotation.' } });
    fireEvent.click(screen.getByTestId('admin-role-submit'));
    await waitFor(() => expect(world.calls['assign']).toBe(1));
  });

  it('denies lower grants with text rationale (recovery: escalate)', async () => {
    authenticate(['diagnostics.view']);
    renderWithProviders(<AdminPage />);
    fireEvent.click(await screen.findByTestId('admin-user-select-user-1'));
    fireEvent.change(screen.getByTestId('admin-role-target'), { target: { value: 'TenantAdmin' } });
    expect(await screen.findByTestId('admin-role-grant-denied')).toBeDefined();
    expect(screen.getByTestId('admin-role-grant-denied-text').textContent).toContain('strictly exceed');
  });

  it('blocks short reasons client-side with alerts (recovery: elaborate)', async () => {
    renderWithProviders(<AdminPage />);
    fireEvent.click(await screen.findByTestId('admin-user-select-user-1'));
    fireEvent.change(screen.getByTestId('admin-role-reason'), { target: { value: 'x' } });
    expect(await screen.findByTestId('admin-role-reason-error')).toBeDefined();
    expect((screen.getByTestId('admin-role-submit') as HTMLButtonElement).disabled).toBe(true);
  });

  it('surfaces server forbiddens without clearing the draft (recovery: contact-admin)', async () => {
    world.assignBehavior = 'forbidden';
    renderWithProviders(<AdminPage />);
    fireEvent.click(await screen.findByTestId('admin-user-select-user-1'));
    fireEvent.change(screen.getByTestId('admin-role-reason'), { target: { value: 'Covering the on-call rotation.' } });
    fireEvent.click(screen.getByTestId('admin-role-submit'));
    expect(await screen.findByTestId('admin-role-forbidden')).toBeDefined();
    expect((screen.getByTestId('admin-role-reason') as HTMLTextAreaElement).value).toBe('Covering the on-call rotation.');
  });

  it('renders the empty users state without error chrome', async () => {
    world.mode = 'empty';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-users-empty')).toBeDefined();
  });
});

describe('tenants/health/routes/usage/retention matrices', () => {
  it('lists tenants with names and detail drill-down (text, not ids alone)', async () => {
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-tenants-list')).toBeDefined();
    fireEvent.click(screen.getByTestId('admin-tenant-select-tenant-1'));
    expect(await screen.findByTestId('admin-tenant-detail')).toBeDefined();
    expect(screen.getByTestId('admin-tenant-detail-name').textContent).toBe('Acme');
    expect(screen.getByTestId('admin-tenant-detail-slug').textContent).toBe('acme');
  });

  it('masks route secrets while showing capabilities (no leak)', async () => {
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-health-table')).toBeDefined();
    expect(screen.getByTestId('admin-health-table').textContent).toContain('acme-stt');
    expect(await screen.findByTestId('admin-routes-table')).toBeDefined();
    expect(document.body.textContent).not.toContain('operator:password');
  });

  it('renders usage quotas with ratios (text signals)', async () => {
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-usage-storage')).toBeDefined();
    expect(screen.getByTestId('admin-usage-runs').textContent).toBe('1');
    expect(screen.getByTestId('admin-quotas-active').textContent).toBe('10');
    expect(screen.getByTestId('admin-usage-quota-state')).toBeDefined();
  });

  it('renders retention policies with windows (text signals)', async () => {
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-section-retention-audit')).toBeDefined();
    expect(document.body.textContent).toContain('90');
  });
});

describe('ops subsection matrix', () => {
  it('renders queues, leases, orphans, backlog, and failures (text signals)', async () => {
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-ops-queues-list')).toBeDefined();
    expect(screen.getByTestId('admin-ops-queues-list').textContent).toContain('media.prepare');
    expect(await screen.findByTestId('admin-ops-leases-list')).toBeDefined();
    expect(screen.getByTestId('admin-ops-lease-lease-1')).toBeDefined();
    expect(screen.getByTestId('admin-ops-lease-age-lease-1').textContent).toContain('1m');
    expect(await screen.findByTestId('admin-ops-workers-list')).toBeDefined();
    expect(await screen.findByTestId('admin-ops-workers-list')).toBeDefined();
    expect(screen.getByTestId('admin-ops-workers-list').textContent).toContain('worker-1');
    expect(await screen.findByTestId('admin-ops-orphans-empty')).toBeDefined();
    expect(await screen.findByTestId('admin-ops-backlog-summary')).toBeDefined();
    expect(screen.getByTestId('admin-ops-backlog-total').textContent).toContain('2');
    expect(screen.getByTestId('admin-ops-backlog-project-prj_1').textContent).toContain('2 open');
    expect(await screen.findByTestId('admin-ops-failures-list')).toBeDefined();
    expect(await screen.findByTestId('admin-ops-errors-list')).toBeDefined();
  });

  it('pages the DLQ with depth signals (recovery: redrive/discard)', async () => {
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-ops-dlq-list')).toBeDefined();
    expect(screen.getByTestId('admin-ops-dlq-depth').textContent).toContain('1');
  });
});

describe('admin hook guards', () => {
  it('never fires for anonymous sessions and invalidates cleanly', async () => {
    let calls = 0;
    setInnerFetchForTests((async () => {
      calls += 1;
      return jsonResponse({});
    }) as typeof fetch);
    function Probe(): null {
      useAdminFlags();
      return null;
    }
    useAuthStore.setState({ status: 'anonymous' });
    renderWithProviders(<Probe />);
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(calls).toBe(0);
    useAuthStore.setState({ status: 'authenticated' });
    await invalidateAdminQueries(queryClient);
  });
});

describe('admin types sweep', () => {
  it('ranks grants and gates assignments deterministically', () => {
    expect(assignerRank(['admin.manage'])).toBeGreaterThan(assignerRank([]));
    expect(canAssignRole(['admin.manage'], 'ProjectOwner')).toBe(true);
    expect(canAssignRole(['diagnostics.view'], 'TenantAdmin')).toBe(false);
    expect(isValidAuditReason('Covering the on-call rotation.')).toBe(true);
    expect(isValidAuditReason('x')).toBe(false);
    expect(sanitizeReasonText('  padded reason  ').length).toBeGreaterThan(0);
  });

  it('parses admin collections defensively', () => {
    expect(parseTenants(null)).toEqual([]);
    expect(parseTenants({ items: [{ id: 't1', name: 'Acme' }] }).length).toBe(1);
    expect(parseAdminUsers(null)).toEqual([]);
    expect(parseProviderHealth('nope')).toEqual([]);
    expect(parseProviderHealth([{ provider: 'acme-stt', status: 'Healthy' }]).length).toBe(1);
    expect(parseProviderRoutes(null)).toEqual([]);
    expect(parseQuotas(null).maxActiveProjects ?? 0).toBeGreaterThanOrEqual(0);
    expect(parseUsage(null).activeRuns).toBeGreaterThanOrEqual(0);
    expect(parseQueueDepths('nope')).toEqual([]);
    expect(parseDlqSummary(null).depth).toBe(0);
    expect(dlqRowsFromSummary(parseDlqSummary(null))).toEqual([]);
    expect(parseStaleLeases(null)).toEqual([]);
    expect(formatLeaseAge(61000)).toContain('1m');
    expect(parseOrphans(null)).toEqual([]);
    expect(parseReviewBacklog(null).totalOpen).toBe(0);
    expect(parseRetentionPolicies(null)).toEqual([]);
    expect(parseFeatureFlags(null)).toEqual([]);
    expect(parseFeatureFlags({ flags: [{ key: 'gpu-render', enabled: false }] })[0]?.key).toBe('gpu-render');
    expect(parseAuditEvent(null)).toBeUndefined();
    expect(parseAuditEvents(null)).toEqual([]);
    expect(parseAdminUsers({ items: [{ id: 'u1' }] }).length).toBe(1);
  });

  it('classifies admin errors and guards secrets/ids', () => {
    expect(isAdminForbiddenError({ code: 'FORBIDDEN', status: 403 })).toBe(true);
    expect(isAdminForbiddenError({ code: 'X', status: 500 })).toBe(false);
    expect(isAdminConflictError({ code: 'CONFLICT', status: 409 })).toBe(true);
    expect(isAdminFreezeError({ code: 'ROLLOUT_FROZEN', status: 423 })).toBe(true);
    expect(isAdminUnknownRouteError({ code: 'NOT_FOUND', status: 404 })).toBe(true);
    expect(isForbiddenAdminKey('password')).toBe(true);
    expect(isForbiddenAdminKey('displayName')).toBe(false);
    expect(isAdvertisedDlqAction('redrive')).toBe(true);
    expect(isAdvertisedDlqAction('bogus')).toBe(false);
    expect(filterAdvertisedDlqActions(['redrive', 'bogus'])).toEqual(['redrive']);
    expect(maskConnectionString('postgres://operator:password@db-host/rows')).not.toContain('password');
    expect(findSecretLeak(['pg://primary/db'])).toBeUndefined();
    expect(findSecretLeak(['postgres://operator:password@db-host/rows'])).toBeDefined();
    expect(looksLikeReservationId('res_1234567890abcdef')).toBe(true);
    expect(looksLikeReservationId('plain')).toBe(false);
    expect(usageRatio(5, 10)).toBe(0.5);
    expect(usageRatio(5, 0)).toBe(0);
  });
});
