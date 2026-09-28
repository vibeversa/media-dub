import { useQuery } from '@tanstack/react-query';
import type { QueryClient, UseQueryResult } from '@tanstack/react-query';
import { apiClient, apiFetch, newCorrelationId } from '../../api/client/index.js';
import { normalizeError } from '../../api/errors/index.js';
import type { AppError } from '../../api/errors/index.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { useIsAuthenticated } from '../auth/useSession.js';
import { canAssignRole, isValidAuditReason, parseAdminUsers } from './types.js';
import type {
  AdminUserView,
  AuditEventView,
  BacklogView,
  DlqView,
  FeatureFlagView,
  LeaseView,
  OrphanView,
  ProviderHealthView,
  ProviderRouteView,
  QuotasView,
  RetentionPolicyView,
  TenantView,
  UsageView,
} from './types.js';
import {
  parseAuditEvents,
  parseFeatureFlags,
  parseOrphans,
  parseProviderHealth,
  parseProviderRoutes,
  parseQuotas,
  parseQueueDepths,
  parseDlqSummary,
  parseRetentionPolicies,
  parseReviewBacklog,
  parseStaleLeases,
  parseTenants,
  parseUsage,
} from './types.js';
import type { QueueDepthView } from './types.js';

/**
 * Admin read models (Task 036) over the Task 005/013 contracts.
 *
 * - Real endpoints go through the generated client (`apiClient.*`); the
 *   conventional tenant/user/audit/retention/flag reads go through `apiFetch`
 *   (`/admin/tenants`, `/admin/users`, `/admin/audit-events`,
 *   `/admin/retention`, `/admin/feature-flags`) and degrade to
 *   `notProvisioned` empty views on 404 (unknown admin subpath marker) so
 *   panels render `EmptyState` instead of failing when the backend has not
 *   provisioned them yet.
 * - Diagnostics hooks refetch every 30s; `refetchIntervalInBackground` stays
 *   false (default), so refetch pauses when the tab is hidden.
 * - Queries fire whenever the session is authenticated — the server is
 *   authoritative. A 403 (elevated role revoked mid-session) surfaces as a
 *   query error; `AdminPage` locks the section + toasts while the session
 *   stays intact (never a logout, never a redirect loop).
 * - All seven areas plus the ops dashboard read through `queryKeys`
 *   factory scopes only (`adminTenants` / `adminUsers` / `diagnostics` /
 *   `admin.section`); ad-hoc key literals are forbidden (R1 scan).
 */

const DIAGNOSTICS_REFETCH_MS = 30_000;

function normalizeGet(error: unknown): AppError {
  return normalizeError(error, { method: 'GET' });
}

function normalizeMutation(error: unknown): AppError {
  return normalizeError(error, { method: 'POST' });
}

/** Optional conventional reads: 404 (unknown admin subpath) → not provisioned. */
function isNotProvisioned(error: AppError): boolean {
  return error.status === 404;
}

export interface OptionalList<T> {
  readonly items: readonly T[];
  readonly notProvisioned: boolean;
}

async function fetchOptionalList<T>(path: string, parse: (raw: unknown) => T[]): Promise<OptionalList<T>> {
  try {
    const raw = await apiFetch<unknown>(path as `/${string}`);
    return { items: parse(raw), notProvisioned: false };
  } catch (error) {
    const normalized = normalizeGet(error);
    if (isNotProvisioned(normalized)) {
      return { items: [], notProvisioned: true };
    }
    throw normalized;
  }
}

function useOptionalAdminList<T>(key: readonly unknown[], path: string, parse: (raw: unknown) => T[]): UseQueryResult<OptionalList<T>, AppError> {
  const enabled = useIsAuthenticated();
  return useQuery<OptionalList<T>, AppError>({
    queryKey: key,
    queryFn: () => fetchOptionalList(path, parse),
    enabled,
    staleTime: DIAGNOSTICS_REFETCH_MS,
    refetchInterval: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    retry: false,
  });
}

function useDiagnosticsPoll<T>(key: readonly unknown[], fetch: (signal?: AbortSignal) => Promise<T>): UseQueryResult<T, AppError> {
  const enabled = useIsAuthenticated();
  return useQuery<T, AppError>({
    queryKey: key,
    queryFn: ({ signal }) => fetch(signal),
    enabled,
    staleTime: DIAGNOSTICS_REFETCH_MS,
    // Paused when the tab is hidden (`refetchIntervalInBackground` defaults
    // to false); resumes automatically on visibility.
    refetchInterval: DIAGNOSTICS_REFETCH_MS,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    retry: false,
  });
}

// --- Status / usage / quotas / providers ------------------------------------

export interface AdminStatusView {
  readonly status: string;
  readonly time: string;
}

export function useAdminStatus(): UseQueryResult<AdminStatusView, AppError> {
  const enabled = useIsAuthenticated();
  return useQuery<AdminStatusView, AppError>({
    queryKey: queryKeys.diagnostics.status(),
    queryFn: async () => {
      try {
        const raw = (await apiClient.getAdminStatus()) as unknown as Record<string, unknown>;
        const status = typeof raw['status'] === 'string' && raw['status'] !== '' ? (raw['status'] as string) : 'unknown';
        const time = typeof raw['time'] === 'string' ? (raw['time'] as string) : '';
        return { status, time };
      } catch (error) {
        throw normalizeGet(error);
      }
    },
    enabled,
    staleTime: DIAGNOSTICS_REFETCH_MS,
    refetchInterval: DIAGNOSTICS_REFETCH_MS,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    retry: false,
  });
}

export function useAdminUsage(): UseQueryResult<UsageView, AppError> {
  return useDiagnosticsPoll(queryKeys.diagnostics.usage(), async () => {
    try {
      return parseUsage(await apiClient.getAdminUsage());
    } catch (error) {
      throw normalizeGet(error);
    }
  });
}

export function useAdminQuotas(): UseQueryResult<QuotasView, AppError> {
  return useDiagnosticsPoll(queryKeys.diagnostics.quotas(), async () => {
    try {
      return parseQuotas(await apiClient.getAdminQuotas());
    } catch (error) {
      throw normalizeGet(error);
    }
  });
}

export function useProviderHealth(): UseQueryResult<ProviderHealthView[], AppError> {
  return useDiagnosticsPoll(queryKeys.diagnostics.health(), async () => {
    try {
      return parseProviderHealth(await apiClient.getAdminProviderHealth());
    } catch (error) {
      throw normalizeGet(error);
    }
  });
}

export function useProviderRoutes(): UseQueryResult<ProviderRouteView[], AppError> {
  return useDiagnosticsPoll(queryKeys.diagnostics.routes(), async () => {
    try {
      return parseProviderRoutes(await apiClient.getAdminProviderRoutes());
    } catch (error) {
      throw normalizeGet(error);
    }
  });
}

// --- Ops dashboard ------------------------------------------------------------

export function useOpsQueues(): UseQueryResult<QueueDepthView[], AppError> {
  return useDiagnosticsPoll(queryKeys.diagnostics.queues(), async () => {
    try {
      return parseQueueDepths(await apiClient.getDiagnosticsQueues());
    } catch (error) {
      throw normalizeGet(error);
    }
  });
}

export function useOpsDlq(page = 1, pageSize = 50): UseQueryResult<DlqView, AppError> {
  const safePage = Number.isFinite(page) && page >= 1 ? Math.floor(page) : 1;
  const safeSize = Number.isFinite(pageSize) && pageSize >= 1 ? Math.min(200, Math.max(1, Math.floor(pageSize))) : 50;
  return useDiagnosticsPoll(queryKeys.diagnostics.dlq({ page: safePage, pageSize: safeSize }), async () => {
    try {
      const raw = await apiClient.getDiagnosticsDlq({ path: {}, query: { page: safePage, pageSize: safeSize } });
      return parseDlqSummary(raw);
    } catch (error) {
      throw normalizeGet(error);
    }
  });
}

export function useOpsLeases(page = 1, pageSize = 50): UseQueryResult<LeaseView[], AppError> {
  const safePage = Number.isFinite(page) && page >= 1 ? Math.floor(page) : 1;
  const safeSize = Number.isFinite(pageSize) && pageSize >= 1 ? Math.min(200, Math.max(1, Math.floor(pageSize))) : 50;
  return useDiagnosticsPoll(queryKeys.diagnostics.leases({ page: safePage, pageSize: safeSize }), async () => {
    try {
      const raw = await apiClient.getDiagnosticsLeases({ path: {}, query: { page: safePage, pageSize: safeSize } });
      return parseStaleLeases(raw, Date.now());
    } catch (error) {
      throw normalizeGet(error);
    }
  });
}

export interface OrphansView {
  readonly items: readonly OrphanView[];
  readonly hasMore: boolean;
  readonly cursor: string | undefined;
}

export function useOpsOrphans(pageSize = 50): UseQueryResult<OrphansView, AppError> {
  const safeSize = Number.isFinite(pageSize) && pageSize >= 1 ? Math.min(200, Math.max(1, Math.floor(pageSize))) : 50;
  return useDiagnosticsPoll(queryKeys.diagnostics.orphans({ pageSize: safeSize }), async () => {
    try {
      const raw = (await apiClient.getDiagnosticsOrphans({ path: {}, query: { pageSize: safeSize } })) as unknown as Record<string, unknown>;
      const cursor = typeof raw['cursor'] === 'string' && raw['cursor'] !== '' ? (raw['cursor'] as string) : undefined;
      return { items: parseOrphans(raw), hasMore: raw['hasMore'] === true, cursor };
    } catch (error) {
      throw normalizeGet(error);
    }
  });
}

export function useOpsBacklog(): UseQueryResult<BacklogView, AppError> {
  return useDiagnosticsPoll(queryKeys.diagnostics.backlog(), async () => {
    try {
      return parseReviewBacklog(await apiClient.getDiagnosticsReviewBacklog());
    } catch (error) {
      throw normalizeGet(error);
    }
  });
}

// --- Tenants / users (conventional, degrade to not-provisioned) ---------------

export function useAdminTenants(): UseQueryResult<OptionalList<TenantView>, AppError> {
  return useOptionalAdminList(queryKeys.adminTenants.list(), '/admin/tenants', parseTenants);
}

export function useAdminUsers(): UseQueryResult<OptionalList<AdminUserView>, AppError> {
  return useOptionalAdminList(queryKeys.adminUsers.list(), '/admin/users', parseAdminUsers);
}

// --- Audit / retention / flags (conventional, degrade to not-provisioned) -----

export interface AuditPageView {
  readonly events: readonly AuditEventView[];
  readonly page: number;
  readonly pageSize: number;
  readonly hasMore: boolean;
  readonly notProvisioned: boolean;
  /** True when page > 1 expired per the retention policy (explicit marker). */
  readonly gapExpired: boolean;
}

export function useAdminAudit(page = 1, pageSize = 20): UseQueryResult<AuditPageView, AppError> {
  const safePage = Number.isFinite(page) && page >= 1 ? Math.floor(page) : 1;
  const safeSize = Number.isFinite(pageSize) && pageSize >= 1 ? Math.min(100, Math.max(1, Math.floor(pageSize))) : 20;
  const enabled = useIsAuthenticated();
  return useQuery<AuditPageView, AppError>({
    queryKey: queryKeys.admin.section('audit', { page: safePage, pageSize: safeSize }),
    queryFn: async () => {
      try {
        const raw = (await apiFetch<unknown>(`/admin/audit-events?page=${String(safePage)}&pageSize=${String(safeSize)}` as `/${string}`)) as unknown;
        return {
          events: parseAuditEvents(raw),
          page: safePage,
          pageSize: safeSize,
          hasMore: (raw as Record<string, unknown>)['hasMore'] === true,
          notProvisioned: false,
          gapExpired: false,
        };
      } catch (error) {
        const normalized = normalizeGet(error);
        if (isNotProvisioned(normalized)) {
          // Page 1: backend has not provisioned the viewer yet. Later pages:
          // retention expiry removed the window — render the explicit gap
          // marker instead of a failure.
          return {
            events: [],
            page: safePage,
            pageSize: safeSize,
            hasMore: false,
            notProvisioned: safePage <= 1,
            gapExpired: safePage > 1,
          };
        }
        throw normalized;
      }
    },
    enabled,
    staleTime: DIAGNOSTICS_REFETCH_MS,
    refetchInterval: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    retry: false,
  });
}

export function useAdminRetention(): UseQueryResult<OptionalList<RetentionPolicyView>, AppError> {
  return useOptionalAdminList(queryKeys.admin.section('retention'), '/admin/retention', parseRetentionPolicies);
}

export function useAdminFlags(): UseQueryResult<OptionalList<FeatureFlagView>, AppError> {
  return useOptionalAdminList(queryKeys.admin.section('flags'), '/admin/feature-flags', parseFeatureFlags);
}

/** Invalidates every admin scope (prefix cascade under `queryKeys.admin.all`). */
export async function invalidateAdminQueries(queryClient: QueryClient): Promise<void> {
  await queryClient.invalidateQueries({ queryKey: queryKeys.admin.all });
}

// --- Mutations (destructive gates supply confirm + reason + permission) --------

export interface AdminActionReceipt {
  readonly actionId: string;
  readonly timestamp: string;
  readonly action: string;
}

function receiptFrom(raw: unknown, action: string): AdminActionReceipt {
  const record = (typeof raw === 'object' && raw !== null ? (raw as Record<string, unknown>) : {}) as Record<string, unknown>;
  const actionId = typeof record['actionId'] === 'string' && record['actionId'] !== '' ? (record['actionId'] as string) : newCorrelationId();
  const timestamp =
    typeof record['timestamp'] === 'string' && record['timestamp'] !== '' ? (record['timestamp'] as string) : new Date().toISOString();
  return { actionId, timestamp, action };
}

function requireAssignable(assignerRoles: readonly string[], role: string, reason: string): void {
  if (!canAssignRole(assignerRoles, role)) {
    throw normalizeMutation({ status: 403, code: 'FORBIDDEN', message: 'Role assignment denied: the assigner must hold a strictly higher grant.', correlationId: '', details: {} });
  }
  if (!isValidAuditReason(reason)) {
    throw normalizeMutation({ status: 400, code: 'VALIDATION_FAILED', message: 'An audit reason of at least 10 characters is required.', correlationId: '', details: {} });
  }
}

/** Assigns a tenant role (higher-grant + mandatory reason enforced client-side). */
export async function assignUserRole(userId: string, role: string, reason: string, assignerRoles: readonly string[]): Promise<AdminActionReceipt> {
  requireAssignable(assignerRoles, role, reason);
  try {
    const raw = await apiFetch<unknown>(`/admin/users/${encodeURIComponent(userId)}/roles` as `/${string}`, {
      method: 'POST',
      body: { role, reason, auditAction: 'role.assign' },
    });
    return receiptFrom(raw, `role.assign:${role}`);
  } catch (error) {
    throw normalizeMutation(error);
  }
}

/** Redrives one DLQ entry (advertised-actions-only callers; 409 = already redriven). */
export async function redriveDlqEntry(entryId: string, reason: string): Promise<AdminActionReceipt> {
  if (!isValidAuditReason(reason)) {
    throw normalizeMutation({ status: 400, code: 'VALIDATION_FAILED', message: 'An audit reason of at least 10 characters is required.', correlationId: '', details: {} });
  }
  try {
    const raw = await apiFetch<unknown>('/admin/diagnostics/dlq/redrive', {
      method: 'POST',
      body: { entryId, reason, auditAction: 'dlq.redrive' },
    });
    return receiptFrom(raw, 'dlq.redrive');
  } catch (error) {
    throw normalizeMutation(error);
  }
}

/** Discards one DLQ entry (advertised-actions-only callers). */
export async function discardDlqEntry(entryId: string, reason: string): Promise<AdminActionReceipt> {
  if (!isValidAuditReason(reason)) {
    throw normalizeMutation({ status: 400, code: 'VALIDATION_FAILED', message: 'An audit reason of at least 10 characters is required.', correlationId: '', details: {} });
  }
  try {
    const raw = await apiFetch<unknown>('/admin/diagnostics/dlq/discard', {
      method: 'POST',
      body: { entryId, reason, auditAction: 'dlq.discard' },
    });
    return receiptFrom(raw, 'dlq.discard');
  } catch (error) {
    throw normalizeMutation(error);
  }
}

/** Safe flag toggle: immediate, reversible, no audit reason required. */
export async function setFeatureFlagEnabled(key: string, enabled: boolean): Promise<void> {
  try {
    await apiFetch<unknown>(`/admin/feature-flags/${encodeURIComponent(key)}` as `/${string}`, {
      method: 'PATCH',
      body: { enabled },
    });
  } catch (error) {
    throw normalizeMutation(error);
  }
}

/** Destructive flag-apply (rollout commit): gated behind the destructive flow. */
export async function applyFeatureFlags(reason: string): Promise<AdminActionReceipt> {
  if (!isValidAuditReason(reason)) {
    throw normalizeMutation({ status: 400, code: 'VALIDATION_FAILED', message: 'An audit reason of at least 10 characters is required.', correlationId: '', details: {} });
  }
  try {
    const raw = await apiFetch<unknown>('/admin/feature-flags/apply', {
      method: 'POST',
      body: { reason, auditAction: 'flags.apply' },
    });
    return receiptFrom(raw, 'flags.apply');
  } catch (error) {
    throw normalizeMutation(error);
  }
}
