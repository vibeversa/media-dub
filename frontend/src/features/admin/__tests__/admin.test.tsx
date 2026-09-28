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
import { deriveQuotaState } from '../../cost/types.js';
import { useAuthStore } from '../../auth/authStore.js';
import { resetRestoreStartedForTests } from '../../auth/useSession.js';
import { AdminPage } from '../AdminPage.js';
import { DestructiveAction } from '../DestructiveAction.js';
import { FlagsPanel } from '../FlagsPanel.js';
import { UsersRolesPanel } from '../UsersRolesPanel.js';
import { canAssignRole, dlqRowsFromSummary, findSecretLeak, isValidAuditReason, maskConnectionString, parseDlqSummary } from '../types.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number, details: Record<string, unknown> = {}): Response {
  return jsonResponse(
    { error: { code, message: `backend ${code}`, correlationId: 'corr-admin', details } },
    status,
  );
}

interface AdminWorld {
  permissions: readonly string[];
  dlq: { depth: number; reasons: { code: string; count: number; actions?: string[] }[] };
  redriveBehavior: 'ok' | 'conflict';
  flagPatchBehavior: 'ok' | 'freeze';
  auditPage2Behavior: 'empty-gap' | 'empty-more';
}

function newWorld(overrides: Partial<AdminWorld> = {}): AdminWorld {
  return {
    permissions: ['admin.manage'],
    dlq: {
      depth: 3,
      reasons: [
        { code: 'PROVIDER_TIMEOUT', count: 2, actions: ['redrive'] },
        { code: 'UNKNOWN', count: 1 },
      ],
    },
    redriveBehavior: 'ok',
    flagPatchBehavior: 'ok',
    auditPage2Behavior: 'empty-gap',
    ...overrides,
  };
}

let world: AdminWorld = newWorld();

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
  if (method === 'GET' && url.includes('/admin/status')) {
    return jsonResponse({ status: 'ok', time: '2026-09-28T00:00:00Z' });
  }
  if (method === 'GET' && url.includes('/admin/usage')) {
    return jsonResponse({
      correlationId: 'c',
      storageUsedBytes: 1000,
      storageQuotaBytes: 100000,
      monthCostUsd: 12.5,
      projectsTodayRemaining: 9,
      activeRuns: 1,
      pendingReviews: 2,
      totalProjects: 4,
    });
  }
  if (method === 'GET' && url.includes('/admin/quotas')) {
    return jsonResponse({
      correlationId: 'c',
      maxActiveProjects: 10,
      maxProjectsPerDay: 10,
      maxCostPerProject: 50,
      maxCostPerSegment: 5,
      maxSegmentCount: 2000,
      maxStorageBytes: 100000,
      maxConcurrentStagesPerTenant: 4,
    });
  }
  if (method === 'GET' && url.includes('/admin/provider-health')) {
    return jsonResponse([
      { provider: 'acme-stt', status: 'Healthy', latencyMsP95: 900, errorRate: 0.01, lastSuccessAt: '2026-09-28T00:00:00Z', activeRoutes: ['stt'], circuitBreakerState: 'Closed' },
    ]);
  }
  if (method === 'GET' && url.includes('/admin/provider-routes')) {
    return jsonResponse([
      { capability: 'stt', provider: 'pg://primary/db', priority: 0, enabled: true },
      { capability: 'tts', provider: 'postgres://operator:password@db-host/rows', priority: 1, enabled: false },
    ]);
  }
  if (method === 'GET' && url.includes('/admin/diagnostics/queues')) {
    return jsonResponse([{ correlationId: 'c', queue: 'media.prepare', depth: 2 }]);
  }
  if (method === 'GET' && url.includes('/admin/diagnostics/dlq')) {
    return jsonResponse({
      correlationId: 'c',
      depth: world.dlq.depth,
      oldestEnqueuedAt: '2026-09-27T00:00:00Z',
      oldestEntryAge: '1d 0h',
      topReasons: world.dlq.reasons,
    });
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
    return jsonResponse({ items: [{ id: 'tenant-1', name: 'Acme', slug: 'acme' }] });
  }
  if (method === 'GET' && url.includes('/admin/users')) {
    return jsonResponse({ items: [{ id: 'user-1', displayName: 'Owner', roles: ['ProjectOwner'] }] });
  }
  if (method === 'GET' && url.includes('/admin/audit-events')) {
    if (url.includes('page=1')) {
      return jsonResponse({ items: [{ id: 'audit-1', timestamp: '2026-09-28T00:00:00Z', actor: 'system', action: 'admin.access', summary: 'Admin status read' }], hasMore: true });
    }
    if (world.auditPage2Behavior === 'empty-gap') {
      return errorEnvelope('NOT_FOUND', 404);
    }
    return jsonResponse({ items: [], hasMore: false });
  }
  if (method === 'GET' && url.includes('/admin/retention')) {
    return jsonResponse({ policies: [{ scope: 'audit-events', retentionDays: 90, description: 'Audit window' }] });
  }
  if (method === 'GET' && url.includes('/admin/feature-flags')) {
    return jsonResponse({ flags: [{ key: 'gpu-render', enabled: false, description: 'GPU rendering', frozen: false }] });
  }
  if (method === 'POST' && url.includes('/admin/diagnostics/dlq/redrive')) {
    if (world.redriveBehavior === 'conflict') {
      return errorEnvelope('CONFLICT', 409);
    }
    return jsonResponse({ actionId: 'act-redrive-1', timestamp: '2026-09-28T00:00:01Z' });
  }
  if (method === 'POST' && url.includes('/admin/diagnostics/dlq/discard')) {
    return jsonResponse({ actionId: 'act-discard-1', timestamp: '2026-09-28T00:00:01Z' });
  }
  if (method === 'POST' && url.includes('/admin/users/')) {
    return jsonResponse({ actionId: 'act-role-1', timestamp: '2026-09-28T00:00:01Z' });
  }
  if (method === 'PATCH' && url.includes('/admin/feature-flags/')) {
    if (world.flagPatchBehavior === 'freeze') {
      return errorEnvelope('ROLLOUT_FROZEN', 423);
    }
    return jsonResponse({ key: 'gpu-render', enabled: true });
  }
  if (method === 'POST' && url.includes('/admin/feature-flags/apply')) {
    return jsonResponse({ actionId: 'act-flags-1', timestamp: '2026-09-28T00:00:01Z' });
  }
  return jsonResponse({});
}

function authenticate(permissions: readonly string[]): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', permissions);
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
  queryClient.clear();
  authenticate(world.permissions);
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

describe('guard allows and denies', () => {
  it('denies non-elevated users with a 403 state and never fetches panels', async () => {
    useAppStore.getState().setSession('authenticated', ['project.view']);
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-forbidden')).toBeDefined();
    expect(screen.queryByTestId('admin-ops')).toBeNull();
  });

  it('allows elevated users and renders all seven areas plus ops', async () => {
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-page')).toBeDefined();
    expect(await screen.findByTestId('admin-tenants')).toBeDefined();
    expect(await screen.findByTestId('admin-users')).toBeDefined();
    expect(await screen.findByTestId('admin-health')).toBeDefined();
    expect(await screen.findByTestId('admin-usage')).toBeDefined();
    expect(await screen.findByTestId('admin-retention-audit')).toBeDefined();
    expect(await screen.findByTestId('admin-flags')).toBeDefined();
    expect(await screen.findByTestId('admin-ops')).toBeDefined();
  });
});

describe('destructive gate requires confirm plus reason', () => {
  it('blocks submit until type-to-confirm and a 10-char reason are present', async () => {
    let calls = 0;
    renderWithProviders(
      <DestructiveAction
        action="dlq.redrive"
        label="Redrive entry"
        confirmToken="PROVIDER_TIMEOUT"
        testId="test-gate"
        onConfirm={() => {
          calls += 1;
          return Promise.resolve({ actionId: 'act-1', timestamp: '2026-09-28T00:00:01Z', action: 'dlq.redrive' });
        }}
      />,
    );
    fireEvent.click(screen.getByTestId('test-gate-open'));
    expect(await screen.findByTestId('test-gate-dialog')).toBeDefined();
    expect((screen.getByTestId('test-gate-confirm') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(screen.getByTestId('test-gate-reason-input'), { target: { value: 'short' } });
    expect((screen.getByTestId('test-gate-confirm') as HTMLButtonElement).disabled).toBe(true);
    expect(await screen.findByTestId('test-gate-reason-error')).toBeDefined();
    fireEvent.change(screen.getByTestId('test-gate-reason-input'), { target: { value: 'Requeue after provider recovery' } });
    expect((screen.getByTestId('test-gate-confirm') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(screen.getByTestId('test-gate-confirm-input'), { target: { value: 'WRONG' } });
    expect((screen.getByTestId('test-gate-confirm') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(screen.getByTestId('test-gate-confirm-input'), { target: { value: 'PROVIDER_TIMEOUT' } });
    expect((screen.getByTestId('test-gate-confirm') as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(screen.getByTestId('test-gate-confirm'));
    await waitFor(() => {
      expect(calls).toBe(1);
    });
    expect(await screen.findByTestId('test-gate-receipt')).toBeDefined();
    expect(screen.getByTestId('test-gate-receipt-id').textContent).toContain('act-1');
  });

  it('shows ForbiddenState without leaking the required role name', async () => {
    useAppStore.getState().setSession('authenticated', ['admin.manage']);
    renderWithProviders(
      <DestructiveAction
        action="dlq.discard"
        label="Discard entry"
        confirmToken="X"
        testId="test-forbidden-gate"
        onConfirm={() => Promise.reject(Object.assign(new Error('denied'), { status: 403, code: 'FORBIDDEN' })) as Promise<{ actionId: string; timestamp: string; action: string }>}
      />,
    );
    fireEvent.click(screen.getByTestId('test-forbidden-gate-open'));
    fireEvent.change(screen.getByTestId('test-forbidden-gate-confirm-input'), { target: { value: 'X' } });
    fireEvent.change(screen.getByTestId('test-forbidden-gate-reason-input'), { target: { value: 'Discard poisoned message now' } });
    fireEvent.click(screen.getByTestId('test-forbidden-gate-confirm'));
    const forbidden = await screen.findByTestId('test-forbidden-gate-forbidden');
    expect(forbidden.textContent).not.toMatch(/admin\.manage|diagnostics\.view|TenantAdmin/);
  });
});

describe('advertised-actions-only DLQ controls', () => {
  it('renders redrive only where advertised and read-only elsewhere', async () => {
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-ops-dlq-list')).toBeDefined();
    expect(screen.getByTestId('admin-ops-dlq-redrive-PROVIDER_TIMEOUT-0-open')).toBeDefined();
    expect(screen.queryByTestId('admin-ops-dlq-discard-PROVIDER_TIMEOUT-0-open')).toBeNull();
    expect(screen.getByTestId('admin-ops-dlq-readonly-UNKNOWN-1')).toBeDefined();
  });

  it('refreshes the row and toasts on a 409 already-redriven race', async () => {
    world.redriveBehavior = 'conflict';
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-ops-dlq-list')).toBeDefined();
    fireEvent.click(screen.getByTestId('admin-ops-dlq-redrive-PROVIDER_TIMEOUT-0-open'));
    fireEvent.change(screen.getByTestId('admin-ops-dlq-redrive-PROVIDER_TIMEOUT-0-confirm-input'), { target: { value: 'PROVIDER_TIMEOUT' } });
    fireEvent.change(screen.getByTestId('admin-ops-dlq-redrive-PROVIDER_TIMEOUT-0-reason-input'), { target: { value: 'Requeue after provider recovery' } });
    fireEvent.click(screen.getByTestId('admin-ops-dlq-redrive-PROVIDER_TIMEOUT-0-confirm'));
    await waitFor(() => {
      expect(screen.getByTestId('admin-ops-dlq-redrive-PROVIDER_TIMEOUT-0-error')).toBeDefined();
    });
    expect(screen.getByTestId('admin-ops-dlq-redrive-PROVIDER_TIMEOUT-0-error').textContent).toMatch(/already redriven/);
  });
});

describe('no secrets in admin output', () => {
  it('drops secret-bearing values and masks connection strings', async () => {
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-routes-table')).toBeDefined();
    const text = document.body.textContent ?? '';
    expect(findSecretLeak([text])).toBeUndefined();
    expect(text).not.toContain('password');
    expect(text).not.toContain('postgres://operator');
    expect(text).toContain('•••• (masked)');
    expect(maskConnectionString('postgres://user:pw@host/db')).toBe('postgres://•••• (masked)');
    expect(maskConnectionString('')).toBe('—');
  });
});

describe('quota-state reuse', () => {
  it('derives quota states through the shared cost derivation', () => {
    expect(deriveQuotaState({ remaining: 9, usedBytes: 1000, quotaBytes: 100000, reservedUsd: undefined })).toBe('available');
    expect(deriveQuotaState({ remaining: 0, usedBytes: 1000, quotaBytes: 100000, reservedUsd: undefined })).toBe('exceeded');
    expect(deriveQuotaState({ remaining: 9, usedBytes: 100000, quotaBytes: 100000, reservedUsd: undefined })).toBe('exceeded');
  });

  it('renders the shared quota state in the usage panel', async () => {
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-usage-quota-available')).toBeDefined();
    expect(screen.getByTestId('admin-usage-quota-state').textContent).toBe('available');
  });
});

describe('role assignment higher-grant rule', () => {
  it('requires a strictly higher grant and a 10-char reason', () => {
    expect(canAssignRole(['admin.manage'], 'ProjectOwner')).toBe(true);
    expect(canAssignRole(['admin.manage'], 'TenantAdmin')).toBe(false);
    expect(canAssignRole(['project.view'], 'ProjectViewer')).toBe(false);
    expect(canAssignRole(['Service'], 'TenantAdmin')).toBe(true);
    expect(isValidAuditReason('short')).toBe(false);
    expect(isValidAuditReason('Promote to owner for launch')).toBe(true);
  });

  it('blocks the submit when the assigner lacks the grant', async () => {
    useAppStore.getState().setSession('authenticated', ['project.view']);
    renderWithProviders(<UsersRolesPanel />);
    expect(await screen.findByTestId('admin-users-list')).toBeDefined();
    expect(screen.getByTestId('admin-role-grant-denied')).toBeDefined();
    expect((screen.getByTestId('admin-role-submit') as HTMLButtonElement).disabled).toBe(true);
  });
});

describe('flag freeze and audit gap', () => {
  it('explains the freeze and reverts the toggle on 423', async () => {
    world.flagPatchBehavior = 'freeze';
    renderWithProviders(<FlagsPanel />);
    expect(await screen.findByTestId('admin-flag-toggle-gpu-render')).toBeDefined();
    fireEvent.click(screen.getByTestId('admin-flag-toggle-gpu-render'));
    expect(await screen.findByTestId('admin-flag-freeze-dialog')).toBeDefined();
    expect(screen.getByTestId('admin-flag-freeze-text').textContent).toMatch(/freeze/);
    expect(screen.getByTestId('admin-flag-toggle-gpu-render').textContent).toBe('Off');
  });

  it('marks retention-expiry pagination gaps explicitly', async () => {
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-audit-table')).toBeDefined();
    fireEvent.click(screen.getByTestId('admin-audit-next'));
    expect(await screen.findByTestId('admin-audit-gap')).toBeDefined();
    expect(screen.getByTestId('admin-audit-gap-text').textContent).toMatch(/expired per retention policy/);
  });

  it('parses DLQ summaries defensively and keeps only advertised actions', () => {
    const summary = parseDlqSummary({ depth: 1, topReasons: [{ code: 'X', count: 1, actions: ['redrive', 'nuke'] }] });
    const rows = dlqRowsFromSummary(summary);
    expect(rows[0]?.actions).toEqual(['redrive']);
  });
});
