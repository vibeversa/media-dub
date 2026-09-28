// Task 039B: admin residual gap closure (part 3).
//
// Covers the branches `adminMatrix` + `adminMatrix2` leave cold across the
// nine admin feature files: per-panel loading/forbidden/error-retry/empty
// sweeps (global modes so every SectionForbidden/ErrorState/EmptyState path
// renders), flags pending-guard + generic-error + frozen + mixed-shape +
// not-provisioned paths, health list/route empty variants + dash fallbacks +
// secret masking, usage quota-state variants (exceeded/near/capped percent/
// unavailable/dashes) + forbidden/retry, ops queues/workers/errors/DLQ/
// leases/orphans/backlog/failures empty + retry + pagination + status-probe
// variants + provider-failure alerts, retention/audit retry + unprovisioned +
// no-days + timestamp-dash + advanced toggle/forbidden/absent + two-page
// navigation, tenants forbidden/unprovisioned/retry + multi-select + empty
// slug, the DestructiveAction early-return/pending/custom-error/default-
// error/empty-description/reset paths, mutation edge paths (clamped
// pagination, partial receipts, unknown-role denial), and an exhaustive
// types sweep (display names, masking, queue/DLQ/lease/orphan/backlog/
// health/route/usage/quota/tenant/user/audit/retention/flag parsers, lease
// ages, error predicates). Every failure asserts its recovery control per
// §11.6 with text signals (041C); fixtures are synthetic corr-* shapes and
// fetch is intercepted via setInnerFetchForTests (R3). Forbidden states
// assert no data leakage (rows never render).
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
import { deriveQuotaState } from '../../cost/types.js';
import { useAuthStore } from '../../auth/authStore.js';
import { resetRestoreStartedForTests } from '../../auth/useSession.js';
import { AdminPage } from '../AdminPage.js';
import { DestructiveAction } from '../DestructiveAction.js';
import { RetentionAuditPanel } from '../RetentionAuditPanel.js';
import {
  applyFeatureFlags,
  assignUserRole,
  discardDlqEntry,
  redriveDlqEntry,
  setFeatureFlagEnabled,
  useAdminAudit,
  useOpsDlq,
  useOpsLeases,
} from '../useAdminQueries.js';
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

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number): Response {
  return jsonResponse(
    { error: { code, message: `backend ${code}`, correlationId: 'corr-m3', details: {} } },
    status,
  );
}

type SimpleMode = 'ok' | 'empty' | 'forbidden' | 'error';

interface AdminResidualCfg {
  globalNever: boolean;
  tenants: SimpleMode | 'notProvisioned';
  users: SimpleMode | 'notProvisioned';
  health: SimpleMode | 'down' | 'bare';
  routes: SimpleMode;
  usage: SimpleMode | 'exceeded' | 'near' | 'over';
  quotas: SimpleMode;
  queues: SimpleMode;
  dlq: 'ok' | 'empty0' | 'forbidden' | 'error';
  leases: SimpleMode;
  orphans: 'empty' | 'withItems' | 'forbidden' | 'error';
  backlog: SimpleMode;
  retention: SimpleMode | 'notProvisioned' | 'noDays';
  audit: SimpleMode | 'notProvisioned' | 'twopages';
  flags: SimpleMode | 'notProvisioned' | 'frozen' | 'mixed' | 'noDesc' | 'on';
  status: SimpleMode | 'emptyTime' | 'missing';
  flagPatch: 'ok' | 'freeze' | 'error' | 'slow';
  calls: Record<string, number>;
}

let cfg: AdminResidualCfg;

function resetCfg(): void {
  cfg = {
    globalNever: false,
    tenants: 'ok',
    users: 'ok',
    health: 'ok',
    routes: 'ok',
    usage: 'ok',
    quotas: 'ok',
    queues: 'ok',
    dlq: 'ok',
    leases: 'ok',
    orphans: 'empty',
    backlog: 'ok',
    retention: 'ok',
    audit: 'ok',
    flags: 'ok',
    status: 'ok',
    flagPatch: 'ok',
    calls: {},
  };
}

function count(key: string): void {
  cfg.calls[key] = (cfg.calls[key] ?? 0) + 1;
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

function flagsPayload(): unknown {
  switch (cfg.flags) {
    case 'empty':
      return { flags: [] };
    case 'frozen':
      return { flags: [{ key: 'gpu-render', enabled: false, description: 'GPU rendering', frozen: true }] };
    case 'mixed':
      return {
        flags: [
          { key: 'gpu-render', enabled: true, description: 'GPU rendering', frozen: true },
          { key: 'fast-path', enabled: false },
          { key: 'password', enabled: true, description: 'leak', frozen: false },
        ],
      };
    case 'noDesc':
      return { flags: [{ key: 'gpu-render', enabled: false }] };
    case 'on':
      return { flags: [{ key: 'gpu-render', enabled: true, description: 'GPU rendering', frozen: false }] };
    default:
      return { flags: [{ key: 'gpu-render', enabled: false, description: 'GPU rendering', frozen: false }] };
  }
}

function auditPayload(url: string): Response {
  const page = Number.parseInt(new URL(url).searchParams.get('page') ?? '1', 10);
  if (cfg.audit === 'empty') {
    return jsonResponse({ items: [], hasMore: false });
  }
  if (cfg.audit === 'notProvisioned') {
    return errorEnvelope('NOT_FOUND', 404);
  }
  if (cfg.audit === 'twopages') {
    if (page <= 1) {
      return jsonResponse({
        items: [
          { id: 'audit-1', timestamp: '2026-09-28T00:00:00Z', actor: 'system', action: 'admin.access', summary: 'Admin status read', runId: 'run_1' },
          { id: 'audit-2', timestamp: '', actor: 'batch', action: '', summary: 'Flags applied' },
        ],
        hasMore: true,
      });
    }
    return jsonResponse({
      items: [{ id: 'audit-3', timestamp: '2026-09-28T00:01:00Z', actor: 'system', action: 'flags.apply', summary: 'Rollout committed' }],
      hasMore: false,
    });
  }
  if (page >= 2) {
    return errorEnvelope('NOT_FOUND', 404);
  }
  return jsonResponse({
    items: [
      { id: 'audit-1', timestamp: '2026-09-28T00:00:00Z', actor: 'system', action: 'admin.access', summary: 'Admin status read', sourceIp: '10.0.0.1', runId: 'run_1' },
      { id: 'audit-2', timestamp: '', actor: 'batch', action: '', summary: 'Flags applied' },
    ],
    hasMore: true,
  });
}

async function mockFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const url = urlOf(input);
  const method = methodOf(input, init);
  if (!url.includes('/api/v1/')) {
    return jsonResponse({});
  }
  if (cfg.globalNever) {
    return new Promise<Response>(() => {});
  }
  if (method === 'PATCH' && url.includes('/admin/feature-flags/')) {
    count('flagPatch');
    if (cfg.flagPatch === 'freeze') {
      return errorEnvelope('ROLLOUT_FROZEN', 423);
    }
    if (cfg.flagPatch === 'error') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    if (cfg.flagPatch === 'slow') {
      return new Promise<Response>(() => {});
    }
    return jsonResponse({ key: 'gpu-render', enabled: true });
  }
  if (method === 'POST' && url.includes('/admin/feature-flags/apply')) {
    count('flagApply');
    return jsonResponse({ actionId: 'act-flags-3', timestamp: '2026-09-28T00:00:03Z' });
  }
  if (method === 'POST' && url.includes('/admin/diagnostics/dlq/redrive')) {
    count('redrive');
    return jsonResponse({ actionId: 'act-redrive-3', timestamp: '2026-09-28T00:00:03Z' });
  }
  if (method === 'POST' && url.includes('/admin/diagnostics/dlq/discard')) {
    count('discard');
    return jsonResponse({ actionId: 'act-discard-3', timestamp: '2026-09-28T00:00:03Z' });
  }
  if (method === 'POST' && url.includes('/admin/users/')) {
    count('assign');
    return jsonResponse({ actionId: 'act-role-3', timestamp: '2026-09-28T00:00:03Z' });
  }
  if (method === 'GET' && url.includes('/admin/status')) {
    if (cfg.status === 'forbidden') {
      return errorEnvelope('FORBIDDEN', 403);
    }
    if (cfg.status === 'error') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    if (cfg.status === 'emptyTime') {
      return jsonResponse({ status: 'ok', time: '' });
    }
    if (cfg.status === 'missing') {
      return jsonResponse({});
    }
    return jsonResponse({ status: 'ok', time: '2026-09-28T00:00:00Z' });
  }
  if (method === 'GET' && url.includes('/admin/usage')) {
    if (cfg.usage === 'forbidden') {
      return errorEnvelope('FORBIDDEN', 403);
    }
    if (cfg.usage === 'error') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    if (cfg.usage === 'empty') {
      return jsonResponse({});
    }
    if (cfg.usage === 'exceeded') {
      return jsonResponse({ correlationId: 'corr-m3', storageUsedBytes: 1000, storageQuotaBytes: 100000, monthCostUsd: 12.5, projectsTodayRemaining: 0, activeRuns: 1, pendingReviews: 2, totalProjects: 4 });
    }
    if (cfg.usage === 'near') {
      return jsonResponse({ correlationId: 'corr-m3', storageUsedBytes: 1000, storageQuotaBytes: 100000, monthCostUsd: 12.5, projectsTodayRemaining: 2, activeRuns: 1, pendingReviews: 2, totalProjects: 4 });
    }
    if (cfg.usage === 'over') {
      return jsonResponse({ correlationId: 'corr-m3', storageUsedBytes: 200000, storageQuotaBytes: 100000, monthCostUsd: 12.5, projectsTodayRemaining: 9, activeRuns: 1, pendingReviews: 2, totalProjects: 4 });
    }
    return jsonResponse({ correlationId: 'corr-m3', storageUsedBytes: 1000, storageQuotaBytes: 100000, monthCostUsd: 12.5, projectsTodayRemaining: 9, activeRuns: 1, pendingReviews: 2, totalProjects: 4 });
  }
  if (method === 'GET' && url.includes('/admin/quotas')) {
    if (cfg.quotas === 'forbidden') {
      return errorEnvelope('FORBIDDEN', 403);
    }
    if (cfg.quotas === 'error') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    if (cfg.quotas === 'empty') {
      return jsonResponse({});
    }
    return jsonResponse({ correlationId: 'corr-m3', maxActiveProjects: 10, maxProjectsPerDay: 10, maxCostPerProject: 50, maxCostPerSegment: 5, maxSegmentCount: 2000, maxStorageBytes: 100000, maxConcurrentStagesPerTenant: 4 });
  }
  if (method === 'GET' && url.includes('/admin/provider-health')) {
    if (cfg.health === 'forbidden') {
      return errorEnvelope('FORBIDDEN', 403);
    }
    if (cfg.health === 'error') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    if (cfg.health === 'empty') {
      return jsonResponse([]);
    }
    if (cfg.health === 'bare') {
      return jsonResponse([{ provider: 'acme-stt', status: 'Healthy', errorRate: 0.01, lastSuccessAt: '' }]);
    }
    if (cfg.health === 'down') {
      return jsonResponse([
        { provider: 'acme-stt', status: 'Down', latencyMsP95: 1500, errorRate: 0.4, lastSuccessAt: '2026-09-28T00:00:00Z', activeRoutes: ['stt'], circuitBreakerState: 'Open' },
        { provider: 'acme-tts', status: 'Degraded', latencyMsP95: 1200, errorRate: 0.2, lastSuccessAt: '2026-09-28T00:00:00Z', activeRoutes: ['tts'], circuitBreakerState: 'HalfOpen' },
      ]);
    }
    return jsonResponse([{ provider: 'acme-stt', status: 'Healthy', latencyMsP95: 900, errorRate: 0.01, lastSuccessAt: '2026-09-28T00:00:00Z', activeRoutes: ['stt'], circuitBreakerState: 'Closed' }]);
  }
  if (method === 'GET' && url.includes('/admin/provider-routes')) {
    if (cfg.routes === 'forbidden') {
      return errorEnvelope('FORBIDDEN', 403);
    }
    if (cfg.routes === 'error') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    if (cfg.routes === 'empty') {
      return jsonResponse([]);
    }
    return jsonResponse([
      { capability: 'stt', provider: 'pg://primary/db', priority: 0, enabled: true },
      { capability: 'tts', provider: 'masked-route', priority: 1, enabled: false },
    ]);
  }
  if (method === 'GET' && url.includes('/admin/diagnostics/queues')) {
    if (cfg.queues === 'forbidden') {
      return errorEnvelope('FORBIDDEN', 403);
    }
    if (cfg.queues === 'error') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    if (cfg.queues === 'empty') {
      return jsonResponse([]);
    }
    return jsonResponse([{ correlationId: 'corr-m3', queue: 'media.prepare', depth: 2 }]);
  }
  if (method === 'GET' && url.includes('/admin/diagnostics/dlq')) {
    if (cfg.dlq === 'forbidden') {
      return errorEnvelope('FORBIDDEN', 403);
    }
    if (cfg.dlq === 'error') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    if (cfg.dlq === 'empty0') {
      return jsonResponse({ correlationId: 'corr-m3', depth: 0, oldestEnqueuedAt: '', oldestEntryAge: '', topReasons: [] });
    }
    return jsonResponse({ correlationId: 'corr-m3', depth: 1, oldestEnqueuedAt: '2026-09-27T00:00:00Z', oldestEntryAge: '1d 0h', topReasons: [{ code: 'RETRY_EXHAUSTED', count: 3, actions: ['redrive', 'discard'] }] });
  }
  if (method === 'GET' && url.includes('/admin/diagnostics/leases')) {
    if (cfg.leases === 'forbidden') {
      return errorEnvelope('FORBIDDEN', 403);
    }
    if (cfg.leases === 'error') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    if (cfg.leases === 'empty') {
      return jsonResponse({ items: [], page: 1, pageSize: 50, total: 0, hasMore: false });
    }
    return jsonResponse({ items: [{ id: 'lease-1', stageType: 'Transcription', status: 'Running', owner: 'worker-1', startedAt: new Date(Date.now() - 61000).toISOString() }], page: 1, pageSize: 50, total: 1, hasMore: false });
  }
  if (method === 'GET' && url.includes('/admin/diagnostics/orphans')) {
    if (cfg.orphans === 'forbidden') {
      return errorEnvelope('FORBIDDEN', 403);
    }
    if (cfg.orphans === 'error') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    if (cfg.orphans === 'withItems') {
      return jsonResponse({ items: [{ id: 'orph-1', sizeBytes: 123, mediaFormat: 'mp4', contentHash: 'abcdef1234567890', createdAt: '2026-09-28T00:00:00Z' }], cursor: 'cur-1', hasMore: true });
    }
    return jsonResponse({ items: [], cursor: null, hasMore: false });
  }
  if (method === 'GET' && url.includes('/admin/diagnostics/review-backlog')) {
    if (cfg.backlog === 'forbidden') {
      return errorEnvelope('FORBIDDEN', 403);
    }
    if (cfg.backlog === 'error') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    if (cfg.backlog === 'empty') {
      return jsonResponse({ correlationId: 'corr-m3', totalOpen: 0, byStatus: {}, bySeverity: {}, oldestWaitingAt: null, perProject: [] });
    }
    return jsonResponse({ correlationId: 'corr-m3', totalOpen: 2, byStatus: { Open: 2 }, bySeverity: {}, oldestWaitingAt: null, perProject: [{ projectId: 'prj_1', openCount: 2 }] });
  }
  if (method === 'GET' && url.includes('/admin/tenants')) {
    if (cfg.tenants === 'forbidden') {
      return errorEnvelope('FORBIDDEN', 403);
    }
    if (cfg.tenants === 'error') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    if (cfg.tenants === 'notProvisioned') {
      return errorEnvelope('NOT_FOUND', 404);
    }
    if (cfg.tenants === 'empty') {
      return jsonResponse({ items: [] });
    }
    return jsonResponse({ items: [{ id: 'tenant-1', name: 'Acme', slug: 'acme' }, { id: 'tenant-2', name: 'Beta', slug: '' }] });
  }
  if (method === 'GET' && url.includes('/admin/users')) {
    if (cfg.users === 'forbidden') {
      return errorEnvelope('FORBIDDEN', 403);
    }
    if (cfg.users === 'error') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    if (cfg.users === 'notProvisioned') {
      return errorEnvelope('NOT_FOUND', 404);
    }
    if (cfg.users === 'empty') {
      return jsonResponse({ items: [] });
    }
    return jsonResponse({ items: [{ id: 'user-1', displayName: 'Owner', roles: ['ProjectOwner'] }] });
  }
  if (method === 'GET' && url.includes('/admin/audit-events')) {
    if (cfg.audit === 'forbidden') {
      return errorEnvelope('FORBIDDEN', 403);
    }
    if (cfg.audit === 'error') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    return auditPayload(url);
  }
  if (method === 'GET' && url.includes('/admin/retention')) {
    if (cfg.retention === 'forbidden') {
      return errorEnvelope('FORBIDDEN', 403);
    }
    if (cfg.retention === 'error') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    if (cfg.retention === 'notProvisioned') {
      return errorEnvelope('NOT_FOUND', 404);
    }
    if (cfg.retention === 'empty') {
      return jsonResponse({ policies: [] });
    }
    if (cfg.retention === 'noDays') {
      return jsonResponse({ policies: [{ scope: 'audit-events', description: 'Audit window' }] });
    }
    return jsonResponse({ policies: [{ scope: 'audit-events', retentionDays: 90, description: 'Audit window' }] });
  }
  if (method === 'GET' && url.includes('/admin/feature-flags')) {
    if (cfg.flags === 'forbidden') {
      return errorEnvelope('FORBIDDEN', 403);
    }
    if (cfg.flags === 'error') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    if (cfg.flags === 'notProvisioned') {
      return errorEnvelope('NOT_FOUND', 404);
    }
    return jsonResponse(flagsPayload());
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

function setAll(mode: SimpleMode): void {
  cfg.tenants = mode;
  cfg.users = mode;
  cfg.health = mode;
  cfg.routes = mode;
  cfg.usage = mode;
  cfg.quotas = mode;
  cfg.queues = mode;
  cfg.dlq = mode === 'ok' ? 'ok' : mode === 'empty' ? 'empty0' : mode;
  cfg.leases = mode;
  cfg.orphans = mode === 'ok' ? 'empty' : mode === 'empty' ? 'empty' : mode;
  cfg.backlog = mode;
  cfg.retention = mode;
  cfg.audit = mode;
  cfg.flags = mode;
  cfg.status = mode;
}

beforeEach(() => {
  resetCfg();
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

describe('global panel sweeps', () => {
  it('renders every loading skeleton while sections resolve (never blank)', () => {
    cfg.globalNever = true;
    renderWithProviders(<AdminPage />);
    for (const testId of [
      'admin-tenants-loading',
      'admin-users-loading',
      'admin-health-loading',
      'admin-usage-loading',
      'admin-retention-loading',
      'admin-audit-loading',
      'admin-flags-loading',
      'admin-ops-queues-loading',
      'admin-ops-workers-loading',
      'admin-ops-errors-loading',
      'admin-ops-dlq-loading',
      'admin-ops-leases-loading',
      'admin-ops-orphans-loading',
      'admin-ops-backlog-loading',
      'admin-ops-failures-loading',
    ]) {
      expect(screen.getByTestId(testId)).toBeDefined();
    }
  });

  it('locks every section on mid-session 403 without leaking rows (session intact)', async () => {
    setAll('forbidden');
    renderWithProviders(<AdminPage />);
    for (const testId of [
      'admin-tenants-forbidden',
      'admin-users-forbidden',
      'admin-health-forbidden',
      'admin-usage-forbidden',
      'admin-retention-forbidden',
      'admin-audit-forbidden',
      'admin-flags-forbidden',
      'admin-ops-queues-forbidden',
      'admin-ops-workers-forbidden',
      'admin-ops-errors-forbidden',
      'admin-ops-dlq-forbidden',
      'admin-ops-leases-forbidden',
      'admin-ops-orphans-forbidden',
      'admin-ops-backlog-forbidden',
      'admin-ops-failures-forbidden',
    ]) {
      expect(await screen.findByTestId(testId)).toBeDefined();
    }
    expect(document.body.textContent).not.toContain('tenant-1');
    expect(document.body.textContent).not.toContain('Owner');
    expect(document.body.textContent).not.toContain('gpu-render');
    expect(document.body.textContent).not.toContain('media.prepare');
    expect(useAppStore.getState().sessionStatus).toBe('authenticated');
  });

  it('renders every error panel with a working retry (recovery: retry)', async () => {
    setAll('error');
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
      'admin-ops-workers-error',
      'admin-ops-errors-error',
      'admin-ops-dlq-error',
      'admin-ops-leases-error',
      'admin-ops-orphans-error',
      'admin-ops-backlog-error',
      'admin-ops-failures-error',
    ]) {
      expect(await screen.findByTestId(testId)).toBeDefined();
    }
    setAll('ok');
    for (const retry of screen.getAllByRole('button', { name: 'Retry' })) {
      fireEvent.click(retry);
    }
    await waitFor(() => expect(screen.queryByTestId('admin-tenants-error')).toBeNull());
    expect(await screen.findByTestId('admin-tenants-list')).toBeDefined();
    expect(await screen.findByTestId('admin-flags-list')).toBeDefined();
    expect(await screen.findByTestId('admin-ops-queues-list')).toBeDefined();
  });
});

describe('flags residual matrix', () => {
  it('retries flag reads after a transport failure (recovery: retry)', async () => {
    cfg.flags = 'error';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-flags-error')).toBeDefined();
    cfg.flags = 'ok';
    fireEvent.click(within(screen.getByTestId('admin-section-flags')).getByRole('button', { name: 'Retry' }));
    expect(await screen.findByTestId('admin-flags-list')).toBeDefined();
    expect(screen.getByTestId('admin-flag-toggle-gpu-render').textContent).toBe('Off');
  });

  it('marks unprovisioned flag reads explicitly (never an error)', async () => {
    cfg.flags = 'notProvisioned';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-flags-empty')).toBeDefined();
    expect(screen.getByTestId('admin-flags-empty').textContent).toContain('not provisioned');
    expect(screen.queryByTestId('admin-flags-error')).toBeNull();
  });

  it('reverts frozen-flag toggles client-side without transport (recovery: wait)', async () => {
    cfg.flags = 'frozen';
    renderWithProviders(<AdminPage />);
    const toggle = await screen.findByTestId('admin-flag-toggle-gpu-render');
    expect(toggle.textContent).toBe('Off');
    fireEvent.click(toggle);
    expect(await screen.findByTestId('admin-flag-freeze-dialog')).toBeDefined();
    expect(screen.getByTestId('admin-flag-freeze-text').textContent).toContain('freeze');
    expect(cfg.calls['flagPatch'] ?? 0).toBe(0);
    fireEvent.click(screen.getByTestId('admin-flag-freeze-close'));
    await waitFor(() => expect(screen.queryByTestId('admin-flag-freeze-dialog')).toBeNull());
  });

  it('reverts generic toggle failures with an error toast and no freeze dialog (recovery: retry)', async () => {
    cfg.flagPatch = 'error';
    renderWithProviders(<AdminPage />);
    fireEvent.click(await screen.findByTestId('admin-flag-toggle-gpu-render'));
    await waitFor(() => expect(cfg.calls['flagPatch']).toBe(1));
    await waitFor(() => expect(screen.queryByTestId('admin-flag-freeze-dialog')).toBeNull());
    expect(screen.getByTestId('admin-flag-toggle-gpu-render').textContent).toBe('Off');
  });

  it('blocks concurrent toggles while a toggle is pending (recovery: wait)', async () => {
    cfg.flagPatch = 'slow';
    renderWithProviders(<AdminPage />);
    const toggle = await screen.findByTestId('admin-flag-toggle-gpu-render');
    fireEvent.click(toggle);
    await waitFor(() => expect((screen.getByTestId('admin-flag-toggle-gpu-render') as HTMLButtonElement).disabled).toBe(true));
    fireEvent.click(screen.getByTestId('admin-flag-toggle-gpu-render'));
    expect(cfg.calls['flagPatch']).toBe(1);
  });

  it('renders On states, frozen badges, omits empty descriptions, and drops secret keys', async () => {
    cfg.flags = 'mixed';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-flags-list')).toBeDefined();
    const toggle = screen.getByTestId('admin-flag-toggle-gpu-render');
    expect(toggle.textContent).toBe('On');
    expect(toggle.getAttribute('aria-checked')).toBe('true');
    expect(screen.getByTestId('admin-flag-frozen-gpu-render').textContent).toBe('Frozen');
    expect(screen.getByTestId('admin-flag-desc-gpu-render').textContent).toBe('GPU rendering');
    expect(screen.queryByTestId('admin-flag-desc-fast-path')).toBeNull();
    expect(screen.queryByTestId('admin-flag-row-password')).toBeNull();
    expect(document.body.textContent).not.toContain('admin-flag-row-password');
  });
});

describe('health residual matrix', () => {
  it('retries provider reads after a transport failure (recovery: retry)', async () => {
    cfg.health = 'error';
    cfg.routes = 'error';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-health-error')).toBeDefined();
    cfg.health = 'ok';
    cfg.routes = 'ok';
    fireEvent.click(within(screen.getByTestId('admin-health-error')).getByRole('button', { name: 'Retry' }));
    expect(await screen.findByTestId('admin-health-table')).toBeDefined();
  });

  it('renders the both-empty state without error chrome (never blank)', async () => {
    cfg.health = 'empty';
    cfg.routes = 'empty';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-health-empty')).toBeDefined();
    expect(screen.queryByTestId('admin-health-error')).toBeNull();
  });

  it('distinguishes missing health from present routes (text signals)', async () => {
    cfg.health = 'empty';
    cfg.routes = 'ok';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-health-list-empty')).toBeDefined();
    expect(await screen.findByTestId('admin-routes-table')).toBeDefined();
    expect(screen.getByTestId('admin-route-enabled-stt-pg://primary/db').textContent).toBe('On');
  });

  it('renders dash fallbacks, masks secrets, and marks disabled routes (text signals)', async () => {
    cfg.health = 'bare';
    cfg.routes = 'ok';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-health-table')).toBeDefined();
    expect(screen.getByTestId('admin-health-latency-acme-stt').textContent).toBe('—');
    expect(screen.getByTestId('admin-health-success-acme-stt').textContent).toBe('—');
    expect(screen.getByTestId('admin-health-circuit-acme-stt').textContent).toBe('Unknown');
    expect(await screen.findByTestId('admin-routes-table')).toBeDefined();
    expect(screen.getByTestId('admin-route-enabled-stt-pg://primary/db').textContent).toBe('On');
    expect(screen.getByTestId('admin-route-enabled-tts-masked-route').textContent).toBe('Off');
    expect(document.body.textContent).not.toContain('pg://primary/db');
    expect(document.body.textContent).toContain('•••• (masked)');
  });

  it('renders the routes-empty state beside live health (text signals)', async () => {
    cfg.health = 'ok';
    cfg.routes = 'empty';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-health-table')).toBeDefined();
    expect(await screen.findByTestId('admin-routes-empty')).toBeDefined();
    expect(screen.queryByTestId('admin-routes-table')).toBeNull();
  });
});

describe('usage residual matrix', () => {
  it('locks usage on mid-session 403 without leaking aggregates (session intact)', async () => {
    cfg.usage = 'forbidden';
    cfg.quotas = 'forbidden';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-usage-forbidden')).toBeDefined();
    expect(screen.queryByTestId('admin-usage-bars')).toBeNull();
    expect(screen.queryByTestId('admin-quotas-limits')).toBeNull();
    expect(document.body.textContent).not.toContain('projects remaining today');
    expect(useAppStore.getState().sessionStatus).toBe('authenticated');
  });

  it('retries usage reads after a transport failure (recovery: retry)', async () => {
    cfg.usage = 'error';
    cfg.quotas = 'error';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-usage-error')).toBeDefined();
    cfg.usage = 'ok';
    cfg.quotas = 'ok';
    fireEvent.click(within(screen.getByTestId('admin-usage-error')).getByRole('button', { name: 'Retry' }));
    expect(await screen.findByTestId('admin-usage-bars')).toBeDefined();
  });

  it('marks exhausted quotas as blocking with text state (never color alone)', async () => {
    cfg.usage = 'exceeded';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-usage-quota-exceeded')).toBeDefined();
    expect(screen.getByTestId('admin-usage-quota-state').textContent).toBe('exceeded');
    expect(screen.getByTestId('admin-usage').getAttribute('data-blocked')).toBe('true');
    expect(screen.getByTestId('admin-usage-quota-icon').textContent).toBe('✕');
  });

  it('marks near quotas with remaining text (never color alone)', async () => {
    cfg.usage = 'near';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-usage-quota-near')).toBeDefined();
    expect(screen.getByTestId('admin-usage-quota-state').textContent).toBe('near');
    expect(screen.getByTestId('admin-usage-quota-remaining').textContent).toContain('2 projects remaining today');
    expect(screen.getByTestId('admin-usage').getAttribute('data-blocked')).toBe('false');
  });

  it('caps storage percentages at 100 and keeps exceeded state (text signals)', async () => {
    cfg.usage = 'over';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-usage-storage')).toBeDefined();
    expect(screen.getByTestId('admin-usage-storage').textContent).toContain('100%');
    expect(screen.getByTestId('admin-usage-quota-state').textContent).toBe('exceeded');
  });

  it('renders unavailable aggregates and quota dashes without zero-filling', async () => {
    cfg.usage = 'empty';
    cfg.quotas = 'empty';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-usage-bars')).toBeDefined();
    expect(screen.getByTestId('admin-usage-storage').textContent).toBe('Unavailable');
    expect(screen.getByTestId('admin-usage-cost').textContent).toBe('Unavailable');
    expect(screen.getByTestId('admin-usage-runs').textContent).toBe('0');
    expect(screen.getByTestId('admin-quotas-active').textContent).toBe('—');
    expect(screen.getByTestId('admin-quotas-storage').textContent).toBe('—');
    expect(screen.queryByTestId('admin-usage-quota-remaining')).toBeNull();
  });
});

describe('ops residual matrix', () => {
  it('renders idle empties for queues, workers, errors, and DLQ (never blank)', async () => {
    cfg.queues = 'empty';
    cfg.leases = 'empty';
    cfg.dlq = 'empty0';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-ops-queues-empty')).toBeDefined();
    expect(await screen.findByTestId('admin-ops-workers-empty')).toBeDefined();
    expect(await screen.findByTestId('admin-ops-errors-empty')).toBeDefined();
    expect(await screen.findByTestId('admin-ops-dlq-empty')).toBeDefined();
  });

  it('retries leases, orphans, and backlog reads (recovery: retry)', async () => {
    cfg.leases = 'error';
    cfg.orphans = 'error';
    cfg.backlog = 'error';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-ops-leases-error')).toBeDefined();
    expect(await screen.findByTestId('admin-ops-orphans-error')).toBeDefined();
    expect(await screen.findByTestId('admin-ops-backlog-error')).toBeDefined();
    cfg.leases = 'ok';
    cfg.orphans = 'empty';
    cfg.backlog = 'ok';
    for (const testId of ['admin-ops-leases-error', 'admin-ops-orphans-error', 'admin-ops-backlog-error']) {
      fireEvent.click(within(screen.getByTestId(testId)).getByRole('button', { name: 'Retry' }));
    }
    expect(await screen.findByTestId('admin-ops-leases-list')).toBeDefined();
    expect(await screen.findByTestId('admin-ops-orphans-empty')).toBeDefined();
  });

  it('pages the DLQ window forward and back (recovery: none needed)', async () => {
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-ops-dlq-list')).toBeDefined();
    expect(screen.getByTestId('admin-ops-dlq-page').textContent).toContain('Page 1');
    expect((screen.getByTestId('admin-ops-dlq-prev') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByTestId('admin-ops-dlq-next'));
    await waitFor(() => expect(screen.getByTestId('admin-ops-dlq-page').textContent).toContain('Page 2'));
    expect((screen.getByTestId('admin-ops-dlq-prev') as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(screen.getByTestId('admin-ops-dlq-prev'));
    await waitFor(() => expect(screen.getByTestId('admin-ops-dlq-page').textContent).toContain('Page 1'));
  });

  it('lists orphan artifacts with truncated hashes (text signals)', async () => {
    cfg.orphans = 'withItems';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-ops-orphans-list')).toBeDefined();
    expect(screen.getByTestId('admin-ops-orphan-format-orph-1').textContent).toBe('mp4');
    expect(screen.getByTestId('admin-ops-orphan-hash-orph-1').textContent).toBe('abcdef123456');
    const orphans = queryClient.getQueryData(['admin', 'diagnostics', 'orphans', { pageSize: 50, cursor: null }]) as
      | { hasMore: boolean; cursor?: string }
      | undefined;
    expect(orphans?.hasMore).toBe(true);
    expect(orphans?.cursor).toBe('cur-1');
  });

  it('renders the all-clear failures state when providers, DLQ, and backlog are healthy', async () => {
    cfg.health = 'ok';
    cfg.dlq = 'empty0';
    cfg.backlog = 'empty';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-ops-failures-empty')).toBeDefined();
    expect(screen.queryByTestId('admin-ops-failures-list')).toBeNull();
  });

  it('alerts degraded providers alongside DLQ and backlog signals (text signals)', async () => {
    cfg.health = 'down';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-ops-failures-list')).toBeDefined();
    expect(screen.getByTestId('admin-ops-failure-acme-stt').textContent).toContain('Down');
    expect(screen.getByTestId('admin-ops-failure-acme-tts').textContent).toContain('Degraded');
    expect(screen.getByTestId('admin-ops-failures-dlq').textContent).toContain('1');
    expect(screen.getByTestId('admin-ops-failures-backlog').textContent).toContain('2');
  });

  it('renders the status probe without a timestamp when the backend omits it', async () => {
    cfg.status = 'emptyTime';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-ops-status')).toBeDefined();
    expect(screen.getByTestId('admin-ops-status').textContent).toBe('Probe: ok');
  });

  it('falls back to unknown probe state on shapeless status payloads', async () => {
    cfg.status = 'missing';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-ops-status')).toBeDefined();
    expect(screen.getByTestId('admin-ops-status').textContent).toContain('unknown');
  });
});

describe('retention/audit residual matrix', () => {
  it('retries retention reads after a transport failure (recovery: retry)', async () => {
    cfg.retention = 'error';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-retention-error')).toBeDefined();
    cfg.retention = 'ok';
    fireEvent.click(within(screen.getByTestId('admin-retention-error')).getByRole('button', { name: 'Retry' }));
    expect(await screen.findByTestId('admin-retention-list')).toBeDefined();
    expect(screen.getByTestId('admin-retention-days-audit-events').textContent).toBe('90 days');
  });

  it('marks unprovisioned retention reads explicitly (never an error)', async () => {
    cfg.retention = 'notProvisioned';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-retention-empty')).toBeDefined();
    expect(screen.getByTestId('admin-retention-empty').textContent).toContain('not provisioned');
  });

  it('renders missing day counts as em-dashes (never blank)', async () => {
    cfg.retention = 'noDays';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-retention-list')).toBeDefined();
    expect(screen.getByTestId('admin-retention-scope-audit-events').textContent).toBe('audit-events');
    expect(screen.getByTestId('admin-retention-days-audit-events').textContent).toBe('—');
  });

  it('retries audit reads after a transport failure (recovery: retry)', async () => {
    cfg.audit = 'error';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-audit-error')).toBeDefined();
    cfg.audit = 'ok';
    fireEvent.click(within(screen.getByTestId('admin-audit-error')).getByRole('button', { name: 'Retry' }));
    expect(await screen.findByTestId('admin-audit-table')).toBeDefined();
  });

  it('marks unprovisioned audit reads on the first page explicitly (never an error)', async () => {
    cfg.audit = 'notProvisioned';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-audit-empty')).toBeDefined();
    expect(screen.getByTestId('admin-audit-empty').textContent).toContain('not provisioned');
  });

  it('renders blank timestamps as em-dashes and defaults blank actions to Updated', async () => {
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-audit-table')).toBeDefined();
    expect(screen.getByTestId('admin-audit-timestamp-audit-2').textContent).toBe('—');
    expect(screen.getByTestId('admin-audit-action-audit-2').textContent).toBe('Updated');
    expect(screen.getByTestId('admin-audit-no-advanced-audit-2').textContent).toBe('—');
  });

  it('expands and collapses advanced audit rows for elevated holders (text signals)', async () => {
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-audit-table')).toBeDefined();
    fireEvent.click(screen.getByTestId('admin-audit-row-audit-1-toggle'));
    expect(await screen.findByTestId('admin-audit-advanced-audit-1')).toBeDefined();
    expect(screen.getByTestId('admin-audit-advanced-value-audit-1-runId').textContent).toBe('run_1');
    fireEvent.click(screen.getByTestId('admin-audit-row-audit-1-toggle'));
    await waitFor(() => expect(screen.queryByTestId('admin-audit-advanced-audit-1')).toBeNull());
  });

  it('hides advanced rows behind a placeholder for non-elevated holders (never an error)', async () => {
    authenticate([]);
    renderWithProviders(<RetentionAuditPanel />);
    expect(await screen.findByTestId('admin-audit-table')).toBeDefined();
    expect(screen.getByTestId('admin-audit-advanced-forbidden-audit-1').textContent).toBe('Advanced details hidden');
    expect(screen.queryByTestId('admin-audit-row-audit-1-toggle')).toBeNull();
  });

  it('pages audit windows forward and back with disabled-edge buttons', async () => {
    cfg.audit = 'twopages';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-audit-table')).toBeDefined();
    expect((screen.getByTestId('admin-audit-prev') as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByTestId('admin-audit-page').textContent).toContain('Page 1');
    fireEvent.click(screen.getByTestId('admin-audit-next'));
    await waitFor(() => expect(screen.getByTestId('admin-audit-page').textContent).toContain('Page 2'));
    expect(screen.getByTestId('admin-audit-row-audit-3')).toBeDefined();
    expect((screen.getByTestId('admin-audit-next') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByTestId('admin-audit-prev'));
    await waitFor(() => expect(screen.getByTestId('admin-audit-page').textContent).toContain('Page 1'));
    expect(screen.getByTestId('admin-audit-row-audit-1')).toBeDefined();
  });
});

describe('tenants residual matrix', () => {
  it('locks tenants on mid-session 403 without leaking names (session intact)', async () => {
    cfg.tenants = 'forbidden';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-tenants-forbidden')).toBeDefined();
    expect(document.body.textContent).not.toContain('Acme');
    expect(useAppStore.getState().sessionStatus).toBe('authenticated');
  });

  it('marks unprovisioned tenant reads explicitly (never an error)', async () => {
    cfg.tenants = 'notProvisioned';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-tenants-empty')).toBeDefined();
    expect(screen.getByTestId('admin-tenants-empty').textContent).toContain('not provisioned');
  });

  it('retries tenant reads after a transport failure (recovery: retry)', async () => {
    cfg.tenants = 'error';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-tenants-error')).toBeDefined();
    cfg.tenants = 'ok';
    fireEvent.click(within(screen.getByTestId('admin-tenants-error')).getByRole('button', { name: 'Retry' }));
    expect(await screen.findByTestId('admin-tenants-list')).toBeDefined();
  });

  it('selects tenants with pressed state and dash fallbacks for empty slugs', async () => {
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-tenants-list')).toBeDefined();
    expect(screen.getByTestId('admin-tenant-name-tenant-1').textContent).toBe('Acme');
    fireEvent.click(screen.getByTestId('admin-tenant-select-tenant-2'));
    expect(await screen.findByTestId('admin-tenant-detail')).toBeDefined();
    expect(screen.getByTestId('admin-tenant-detail-name').textContent).toBe('Beta');
    expect(screen.getByTestId('admin-tenant-detail-slug').textContent).toBe('—');
    expect(screen.getByTestId('admin-tenant-select-tenant-2').getAttribute('aria-pressed')).toBe('true');
    expect(screen.getByTestId('admin-tenant-select-tenant-1').getAttribute('aria-pressed')).toBe('false');
  });
});

describe('DestructiveAction residual matrix', () => {
  function renderGate(onConfirm?: (reason: string) => Promise<{ actionId: string; timestamp: string; action: string }>, description?: string): void {
    renderWithProviders(
      <DestructiveAction
        action="dlq.redrive"
        label="Redrive entry"
        confirmToken="RETRY_EXHAUSTED"
        testId="m3-gate"
        description={description ?? 'Requeues this dead-letter entry for processing.'}
        onConfirm={onConfirm ?? (async () => ({ actionId: 'act-1', timestamp: '2026-09-28T00:00:01Z', action: 'dlq.redrive' }))}
      />,
    );
  }

  it('ignores confirm submits until type-to-confirm and reason validate (recovery: elaborate)', () => {
    const onConfirm = vi.fn(async () => ({ actionId: 'act-1', timestamp: '2026-09-28T00:00:01Z', action: 'dlq.redrive' }));
    renderGate(onConfirm);
    fireEvent.click(screen.getByTestId('m3-gate-open'));
    expect(screen.getByTestId('m3-gate-dialog')).toBeDefined();
    expect(screen.queryByTestId('m3-gate-reason-error')).toBeNull();
    fireEvent.click(screen.getByTestId('m3-gate-confirm'));
    expect(onConfirm).not.toHaveBeenCalled();
    expect(screen.getByTestId('m3-gate-dialog')).toBeDefined();
  });

  it('shows pending state and locks cancel while the action runs (recovery: wait)', async () => {
    renderGate(() => new Promise<{ actionId: string; timestamp: string; action: string }>(() => {}));
    fireEvent.click(screen.getByTestId('m3-gate-open'));
    fireEvent.change(screen.getByTestId('m3-gate-confirm-input'), { target: { value: 'RETRY_EXHAUSTED' } });
    fireEvent.change(screen.getByTestId('m3-gate-reason-input'), { target: { value: 'Reprocessing after the provider recovered.' } });
    fireEvent.click(screen.getByTestId('m3-gate-confirm'));
    expect(await screen.findByText('Working…')).toBeDefined();
    expect((screen.getByTestId('m3-gate-cancel') as HTMLButtonElement).disabled).toBe(true);
  });

  it('surfaces custom transport messages with text (recovery: report id)', async () => {
    renderGate(async () => {
      throw new Error('Boom failure');
    });
    fireEvent.click(screen.getByTestId('m3-gate-open'));
    fireEvent.change(screen.getByTestId('m3-gate-confirm-input'), { target: { value: 'RETRY_EXHAUSTED' } });
    fireEvent.change(screen.getByTestId('m3-gate-reason-input'), { target: { value: 'Reprocessing after the provider recovered.' } });
    fireEvent.click(screen.getByTestId('m3-gate-confirm'));
    expect(await screen.findByTestId('m3-gate-error')).toBeDefined();
    expect(screen.getByTestId('m3-gate-error').textContent).toContain('Boom failure');
  });

  it('falls back to a default message for blank transport errors (recovery: retry)', async () => {
    renderGate(async () => {
      throw new Error('');
    });
    fireEvent.click(screen.getByTestId('m3-gate-open'));
    fireEvent.change(screen.getByTestId('m3-gate-confirm-input'), { target: { value: 'RETRY_EXHAUSTED' } });
    fireEvent.change(screen.getByTestId('m3-gate-reason-input'), { target: { value: 'Reprocessing after the provider recovered.' } });
    fireEvent.click(screen.getByTestId('m3-gate-confirm'));
    expect(await screen.findByTestId('m3-gate-error')).toBeDefined();
    expect(screen.getByTestId('m3-gate-error').textContent).toContain('No data was changed');
  });

  it('surfaces server forbiddens without leaking role names (recovery: contact-admin)', async () => {
    renderGate(async () => {
      throw Object.assign(new Error('denied'), { status: 403, code: 'FORBIDDEN' });
    });
    fireEvent.click(screen.getByTestId('m3-gate-open'));
    fireEvent.change(screen.getByTestId('m3-gate-confirm-input'), { target: { value: 'RETRY_EXHAUSTED' } });
    fireEvent.change(screen.getByTestId('m3-gate-reason-input'), { target: { value: 'Reprocessing after the provider recovered.' } });
    fireEvent.click(screen.getByTestId('m3-gate-confirm'));
    expect(await screen.findByTestId('m3-gate-forbidden')).toBeDefined();
    expect(screen.getByTestId('m3-gate-forbidden-text').textContent).toContain('Contact your tenant admin');
  });

  it('omits empty descriptions and clears receipts plus drafts on reopen/cancel (never blank)', async () => {
    renderWithProviders(
      <DestructiveAction
        action="dlq.discard"
        label="Discard entry"
        confirmToken="RETRY_EXHAUSTED"
        testId="m3-nodesc"
        description=""
        onConfirm={async () => ({ actionId: 'act-7', timestamp: '2026-09-28T00:00:07Z', action: 'dlq.discard' })}
      />,
    );
    fireEvent.click(screen.getByTestId('m3-nodesc-open'));
    expect(await screen.findByTestId('m3-nodesc-dialog')).toBeDefined();
    expect(screen.getByTestId('m3-nodesc-dialog').textContent).not.toContain('Requeues');
    fireEvent.change(screen.getByTestId('m3-nodesc-confirm-input'), { target: { value: 'typed' } });
    fireEvent.click(screen.getByTestId('m3-nodesc-cancel'));
    await waitFor(() => expect(screen.queryByTestId('m3-nodesc-dialog')).toBeNull());
    fireEvent.click(screen.getByTestId('m3-nodesc-open'));
    expect(await screen.findByTestId('m3-nodesc-dialog')).toBeDefined();
    expect((screen.getByTestId('m3-nodesc-confirm-input') as HTMLInputElement).value).toBe('');
    fireEvent.change(screen.getByTestId('m3-nodesc-confirm-input'), { target: { value: 'RETRY_EXHAUSTED' } });
    fireEvent.change(screen.getByTestId('m3-nodesc-reason-input'), { target: { value: 'Discarding the poisoned message now.' } });
    fireEvent.click(screen.getByTestId('m3-nodesc-confirm'));
    expect(await screen.findByTestId('m3-nodesc-receipt')).toBeDefined();
    expect(screen.getByTestId('m3-nodesc-receipt-id').textContent).toContain('act-7');
    fireEvent.click(screen.getByTestId('m3-nodesc-open'));
    expect(screen.queryByTestId('m3-nodesc-receipt')).toBeNull();
    expect(screen.getByTestId('m3-nodesc-dialog')).toBeDefined();
  });
});

describe('admin mutation + query residual matrix', () => {
  it('clamps DLQ/lease/audit pagination windows and gates on session', async () => {
    function Probe(): null {
      useOpsDlq(Number.NaN, 999);
      useOpsLeases(-3, 0);
      useAdminAudit(0, 10000);
      return null;
    }
    renderWithProviders(<Probe />);
    await waitFor(() => expect(queryClient.getQueryData(['admin', 'diagnostics', 'dlq', { page: 1, pageSize: 200 }])).toBeDefined());
    expect(queryClient.getQueryData(['admin', 'diagnostics', 'leases', { page: 1, pageSize: 50 }])).toBeDefined();
    expect(queryClient.getQueryData(['admin', 'audit', { page: 1, pageSize: 100 }])).toBeDefined();
  });

  it('mints receipts when backends omit ids or timestamps (recovery: none needed)', async () => {
    setInnerFetchForTests((async () => jsonResponse({ actionId: 'act-partial-3' })) as typeof fetch);
    const partial = await assignUserRole('user-1', 'ProjectOwner', 'Covering the on-call rotation.', ['admin.manage']);
    expect(partial.actionId).toBe('act-partial-3');
    expect(partial.timestamp.length).toBeGreaterThan(0);
    setInnerFetchForTests((async () => jsonResponse({})) as typeof fetch);
    const minted = await redriveDlqEntry('RETRY_EXHAUSTED', 'Reprocessing after the provider recovered.');
    expect(minted.actionId.length).toBeGreaterThan(0);
    expect(minted.timestamp.length).toBeGreaterThan(0);
    expect(minted.action).toBe('dlq.redrive');
    const discarded = await discardDlqEntry('RETRY_EXHAUSTED', 'Discarding the poisoned message now.');
    expect(discarded.action).toBe('dlq.discard');
  });

  it('denies unknown roles and runs flag mutations end to end', async () => {
    setInnerFetchForTests(mockFetch as typeof fetch);
    await expect(assignUserRole('user-1', 'BogusRole', 'Covering the on-call rotation.', ['admin.manage'])).rejects.toMatchObject({ code: 'FORBIDDEN' });
    await setFeatureFlagEnabled('gpu-render', true);
    expect(cfg.calls['flagPatch']).toBe(1);
    const receipt = await applyFeatureFlags('Rolling out GPU rendering to production.');
    expect(receipt.action).toBe('flags.apply');
    expect(receipt.actionId).toBe('act-flags-3');
  });

  it('normalizes flag transport failures to retryable errors (recovery: retry)', async () => {
    setInnerFetchForTests((async () => {
      throw new TypeError('fetch failed');
    }) as typeof fetch);
    await expect(setFeatureFlagEnabled('gpu-render', false)).rejects.toMatchObject({ retryable: true });
  });
});

describe('admin types residual sweep', () => {
  it('sanitizes display names, reasons, ranks, grants, masks, and leak scans', () => {
    expect(sanitizeReasonText('  padded   reason\twith\nnewlines  ').length).toBeGreaterThan(0);
    expect(sanitizeReasonText(`x${'y'.repeat(600)}`).length).toBe(500);
    expect(isValidAuditReason('123456789')).toBe(false);
    expect(isValidAuditReason('1234567890')).toBe(true);
    expect(assignerRank([])).toBe(0);
    expect(assignerRank(['mystery-role'])).toBe(0);
    expect(assignerRank(['Service'])).toBe(6);
    expect(assignerRank(['ProjectViewer', 'TenantAdmin'])).toBe(5);
    expect(canAssignRole(['admin.manage'], 'BogusRole')).toBe(false);
    expect(canAssignRole(['admin.manage'], 'TenantAdmin')).toBe(false);
    expect(canAssignRole(['Service'], 'TenantAdmin')).toBe(true);
    expect(canAssignRole(['ProjectOwner'], 'ProjectViewer')).toBe(true);
    expect(maskConnectionString('   ')).toBe('—');
    expect(maskConnectionString('just-a-name')).toBe('stored://•••• (masked)');
    expect(maskConnectionString('postgres://operator:password@db-host/rows')).toBe('postgres://•••• (masked)');
    expect(maskConnectionString('not a scheme!://x')).toBe('stored://•••• (masked)');
    expect(isForbiddenAdminKey('')).toBe(false);
    expect(isForbiddenAdminKey('displayName')).toBe(false);
    expect(isForbiddenAdminKey('lease_token')).toBe(true);
    expect(isForbiddenAdminKey('reservationId')).toBe(true);
    expect(isAdvertisedDlqAction(' Redrive ')).toBe(true);
    expect(isAdvertisedDlqAction('DISCARD')).toBe(true);
    expect(isAdvertisedDlqAction('nuke')).toBe(false);
    expect(filterAdvertisedDlqActions('nope')).toEqual([]);
    expect(filterAdvertisedDlqActions(['REDRIVE', 'redrive', 'bogus', 'DISCARD', 7])).toEqual(['redrive', 'discard']);
    expect(findSecretLeak([])).toBeUndefined();
    expect(findSecretLeak(['plain healthy text'])).toBeUndefined();
    expect(findSecretLeak(['has bear token inside'])).toBeDefined();
    expect(findSecretLeak(['the secret value'])).toBeDefined();
    expect(looksLikeReservationId('RES_abc')).toBe(true);
    expect(looksLikeReservationId('has reservation inside')).toBe(true);
    expect(looksLikeReservationId('plain')).toBe(false);
    expect(deriveQuotaState({ remaining: 9, usedBytes: 1000, quotaBytes: 100000, reservedUsd: 4 })).toBe('reserved');
    expect(deriveQuotaState({ remaining: 2, usedBytes: 1000, quotaBytes: 100000, reservedUsd: undefined })).toBe('near');
  });

  it('parses queues, DLQ summaries, leases, orphans, and backlogs defensively', () => {
    expect(parseQueueDepths({ items: 'nope' })).toEqual([]);
    expect(parseQueueDepths([{ queue: '', depth: 1 }, 'nope', { queue: 'secret-token', depth: 1 }])).toEqual([]);
    expect(parseQueueDepths([{ Queue: 'media.prepare', Count: 1.9 }, { name: 'media.encode', depth: -2 }])).toEqual([
      { queue: 'media.prepare', depth: 1 },
      { queue: 'media.encode', depth: 0 },
    ]);
    expect(parseQueueDepths([{ queue: 'q', depth: 'deep' }])).toEqual([{ queue: 'q', depth: 0 }]);
    expect(parseDlqSummary({ depth: -4, topReasons: 'nope' }).depth).toBe(0);
    expect(parseDlqSummary({ depth: 2, topReasons: [{ code: '', count: 1 }, 'nope', { code: 'password', count: 1, actions: ['redrive'] }, { code: 'X', count: -1, actions: 'nope' }] }).reasons).toEqual([
      { code: 'X', count: 0, actions: [] },
    ]);
    const summary = parseDlqSummary({ depth: 1, topReasons: [{ code: 'X', count: 1, actions: ['redrive'] }] });
    expect(dlqRowsFromSummary(summary, { X: ['DISCARD', 'nuke'] })[0]?.actions).toEqual(['discard']);
    expect(dlqRowsFromSummary(summary)[0]?.actions).toEqual(['redrive']);
    expect(formatLeaseAge(undefined)).toBe('—');
    expect(formatLeaseAge(Number.NaN)).toBe('—');
    expect(formatLeaseAge(-5)).toBe('—');
    expect(formatLeaseAge(0)).toBe('0s');
    expect(formatLeaseAge(59000)).toBe('59s');
    expect(formatLeaseAge(60000)).toBe('1m');
    expect(formatLeaseAge(59 * 60000)).toBe('59m');
    expect(formatLeaseAge(3600000)).toBe('1h 0m');
    expect(formatLeaseAge(47 * 3600000)).toBe('47h 0m');
    expect(formatLeaseAge(48 * 3600000)).toBe('2d 0h');
    expect(parseStaleLeases({ items: 'nope' })).toEqual([]);
    expect(parseStaleLeases(['nope', { id: '' }, { id: 'res_1234567890abcdef' }])).toEqual([]);
    const leases = parseStaleLeases(
      [{ stageExecutionId: 'lease-9', ownerHint: 'ops@acme.test', startedAt: '2026-09-28T00:00:00Z', leaseExpiresAt: '2026-09-28T01:00:00Z' }],
      Date.parse('2026-09-28T00:01:00Z'),
    );
    expect(leases[0]?.owner).toBe('Team member');
    expect(leases[0]?.ageMs).toBe(60000);
    expect(leases[0]?.leaseExpiresAt).toBe('2026-09-28T01:00:00Z');
    expect(parseStaleLeases([{ id: 'lease-10', owner: 'worker-1', startedAt: '' }])[0]?.ageMs).toBeUndefined();
    expect(parseStaleLeases([{ id: 'lease-11', startedAt: 'not-a-date' }])[0]?.ageMs).toBeUndefined();
    expect(parseStaleLeases([{ id: 'lease-12', startedAt: '2026-09-29T00:00:00Z' }], Date.parse('2026-09-28T00:00:00Z'))[0]?.ageMs).toBe(0);
    expect(parseStaleLeases([{ id: 'lease-13' }])[0]?.stageType).toBe('Unknown');
    expect(parseStaleLeases([{ id: 'lease-13' }])[0]?.status).toBe('Unknown');
    expect(parseOrphans({ items: 'nope' })).toEqual([]);
    expect(parseOrphans(['nope', { id: '' }])).toEqual([]);
    const orphans = parseOrphans([{ id: 'orph-9', sizeBytes: 10.9, mediaFormat: '', contentHash: '', createdAt: '' }]);
    expect(orphans[0]?.sizeBytes).toBe(10);
    expect(orphans[0]?.mediaFormat).toBe('Unknown');
    expect(orphans[0]?.contentHashPrefix).toBe('—');
    expect(parseOrphans([{ contentObjectId: 'orph-10', sizeBytes: -1, mediaFormat: 'mp4', contentHash: 'abcdef1234567890', createdAt: 't' }])[0]?.sizeBytes).toBeUndefined();
    expect(parseReviewBacklog({ totalOpen: -1, perProject: 'nope', byStatus: { password: 3, Open: 'many' }, bySeverity: {} }).totalOpen).toBe(0);
    const backlog = parseReviewBacklog({ totalOpen: 2, perProject: ['nope', { projectId: '' }, { projectId: 'prj_9', openCount: -1 }], byStatus: { Open: 2 }, bySeverity: { critical: 1 } });
    expect(backlog.perProject).toEqual([{ projectId: 'prj_9', openCount: 0 }]);
    expect(backlog.byStatus).toEqual({ Open: 2 });
  });

  it('parses health, routes, usage, quotas, tenants, and users defensively', () => {
    expect(parseProviderHealth([{ provider: '' }, 'nope', { provider: 'password' }])).toEqual([]);
    const health = parseProviderHealth([{ provider: 'acme-stt', status: '', latencyMsP95: -1, errorRate: -1, activeRoutes: ['stt', 7, 'secret-token'], circuitBreakerState: '' }]);
    expect(health[0]?.status).toBe('Unknown');
    expect(health[0]?.latencyMsP95).toBeUndefined();
    expect(health[0]?.errorRate).toBe(0);
    expect(health[0]?.activeRoutes).toEqual(['stt']);
    expect(health[0]?.circuitBreakerState).toBe('Unknown');
    expect(parseProviderRoutes(['nope', { capability: '', provider: 'x' }, { capability: 'stt', provider: '' }, { capability: 'password', provider: 'x' }])).toEqual([]);
    expect(parseProviderRoutes([{ capability: 'stt', provider: 'r', priority: 1.7, enabled: true }, { capability: 'tts', provider: 'r2', priority: 'high' }])).toEqual([
      { capability: 'stt', provider: 'r', priority: 1, enabled: true },
      { capability: 'tts', provider: 'r2', priority: 0, enabled: false },
    ]);
    expect(parseUsage({ storageUsedBytes: -1, storageQuotaBytes: Number.NaN, monthCostUsd: 'x', projectsTodayRemaining: -2, activeRuns: -1, pendingReviews: Number.NaN, totalProjects: 'x' })).toEqual({
      storageUsedBytes: undefined,
      storageQuotaBytes: undefined,
      monthCostUsd: undefined,
      projectsTodayRemaining: undefined,
      activeRuns: 0,
      pendingReviews: 0,
      totalProjects: 0,
    });
    expect(parseUsage({ StorageUsedBytes: 5, StorageQuotaBytes: 10, MonthCostUsd: 1, ProjectsTodayRemaining: 2, ActiveRuns: 1, PendingReviews: 1, TotalProjects: 1 }).storageUsedBytes).toBe(5);
    expect(parseQuotas({ maxActiveProjects: -1, maxProjectsPerDay: Number.NaN, maxCostPerProject: 'x' }).maxActiveProjects).toBeUndefined();
    expect(parseQuotas({ MaxActiveProjects: 3 }).maxActiveProjects).toBe(3);
    expect(usageRatio(undefined, 10)).toBe(0);
    expect(usageRatio(5, undefined)).toBe(0);
    expect(usageRatio(5, 0)).toBe(0);
    expect(usageRatio(-5, 10)).toBe(0);
    expect(usageRatio(5, 10)).toBe(0.5);
    expect(parseTenants({ tenants: [{ id: '', name: 'x' }, 'nope', { tenantId: 't1' }] })).toEqual([{ id: 't1', name: 'Unnamed tenant', slug: '' }]);
    expect(parseTenants([{ id: 't2', name: 'Acme', slug: 'acme' }])[0]?.name).toBe('Acme');
    expect(parseAdminUsers({ users: [{ id: '', displayName: 'x' }, 'nope', { userId: 'u1', email: 'ops@acme.test', roles: 'nope' }] })).toEqual([
      { id: 'u1', displayName: 'Team member', roles: [] },
    ]);
    const users = parseAdminUsers([{ id: 'u2', displayName: '', roles: ['ProjectOwner', 'password', ''] }]);
    expect(users[0]?.displayName).toBe('Unnamed');
    expect(users[0]?.roles).toEqual(['ProjectOwner']);
  });

  it('parses audit, retention, and flags defensively and classifies errors', () => {
    expect(parseAuditEvent(null)).toBeUndefined();
    expect(parseAuditEvent({ id: '', summary: 'x' })).toBeUndefined();
    expect(parseAuditEvent({ id: 'a', summary: '' })).toBeUndefined();
    expect(parseAuditEvent({ id: 'a', summary: 'done', actor: 'sub_1234567890abcdef1234567890abcdef', action: '' })?.actor).toBe('Team member');
    expect(parseAuditEvent({ id: 'a', summary: 'done', action: '' })?.action).toBe('Updated');
    const advanced = parseAuditEvent({ id: 'a', summary: 'done', runId: 'run_1', password: 'x', reservation: 'res_1', redirect: 'https://x.test' })?.advanced;
    expect(advanced).toEqual({ runId: 'run_1' });
    expect(parseAuditEvents({ items: 'nope' })).toEqual([]);
    expect(parseAuditEvents([{ id: 'a', summary: 'one' }, { id: 'a', summary: 'one' }, 'nope']).length).toBe(1);
    expect(parseRetentionPolicies([{ scope: '', retentionDays: 1 }, 'nope', { scope: 'password', retentionDays: 1 }])).toEqual([]);
    expect(parseRetentionPolicies({ policies: [{ scope: 'audit-events', retentionDays: 90.9, description: 'w' }] })[0]?.retentionDays).toBe(90);
    expect(parseRetentionPolicies([{ scope: 'audit-events', retentionDays: -1 }])[0]?.retentionDays).toBeUndefined();
    expect(parseRetentionPolicies([{ scope: 'audit-events' }])[0]?.description).toBe('');
    expect(parseFeatureFlags(['nope', { key: '', enabled: true }, { key: 'password', enabled: true }])).toEqual([]);
    expect(parseFeatureFlags({ flags: [{ key: 'gpu-render', enabled: 'yes', frozen: 'no' }] })[0]).toEqual({ key: 'gpu-render', enabled: false, description: '', frozen: false });
    expect(isAdminForbiddenError({ code: 'USER_DISABLED', status: 500 })).toBe(true);
    expect(isAdminForbiddenError({ code: 'X', status: 500 })).toBe(false);
    expect(isAdminForbiddenError(null)).toBe(false);
    expect(isAdminConflictError({ code: 'X_CONFLICT_Y', status: 500 })).toBe(true);
    expect(isAdminConflictError({ code: 'X', status: 500 })).toBe(false);
    expect(isAdminFreezeError({ status: 423 })).toBe(true);
    expect(isAdminFreezeError({ code: 'ROLLOUT_FROZEN' })).toBe(true);
    expect(isAdminFreezeError({ code: 'X', status: 500 })).toBe(false);
    expect(isAdminUnknownRouteError({ status: 500, code: 'NOT_FOUND' })).toBe(false);
    expect(isAdminUnknownRouteError({ status: 404, code: 'NOT_FOUND' })).toBe(true);
    expect(isAdminUnknownRouteError({ status: 404 })).toBe(true);
    expect(isAdminUnknownRouteError({ status: 404, code: '' })).toBe(true);
    expect(isAdminUnknownRouteError({ status: 404, code: 'FORBIDDEN' })).toBe(false);
  });
});
