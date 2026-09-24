import { useQuery } from '@tanstack/react-query';
import type { UseQueryResult } from '@tanstack/react-query';
import { apiClient } from '../../api/client/index.js';
import type { DashboardSummary } from '../../api/client/index.js';
import { normalizeError } from '../../api/errors/index.js';
import type { AppError } from '../../api/errors/index.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { useIsAuthenticated } from '../auth/useSession.js';

/**
 * Dashboard data surface (Task 020) over the Task 017 transport.
 *
 * R1: the summary screen reads exactly one aggregate —
 * `GET /dashboard/summary` — plus client-side drill-down links. This module
 * exposes a single fetcher and a single query hook; no other aggregate calls
 * exist here by design (the R1 test asserts the fetch only hits
 * `/dashboard/summary`).
 *
 * Shapes come from the generated barrel only (`src/api/client`); no deep
 * `api/generated` imports (enforced by `no-restricted-imports`). All section
 * slices are optional in the bundle, so every consumer parses defensively:
 * a missing/invalid section is a per-card error (R3), never a page blank.
 */

export type { DashboardSummary };

/** Storage-warning threshold: ≥80% of quota shows the warning meter (R2). */
export const QUOTA_WARNING_RATIO = 0.8;

export interface StorageSlice {
  readonly usedBytes: number;
  readonly quotaBytes: number;
}

export interface CostSlice {
  readonly monthToDate: number;
  readonly currency: string;
}

export interface QuotaSlice {
  readonly remaining: number;
  readonly resetsAt: string;
}

export interface BacklogSlice {
  readonly pendingReviews: number;
  readonly runningJobs: number;
}

export interface CountsSlice {
  readonly active: number;
  readonly archived: number;
  readonly total: number;
}

export interface RecentOutputView {
  readonly id: string;
  readonly projectId: string;
  readonly mediaKind: string;
  readonly container: string;
  readonly createdAt: string;
}

export interface WarningView {
  readonly code: string;
  readonly message: string;
  readonly projectId: string | undefined;
}

/** Single aggregate fetch. Never logs or telemeters payload contents. */
export async function fetchDashboardSummary(): Promise<DashboardSummary> {
  return apiClient.getDashboardSummary();
}

/**
 * Summary query on `queryKeys.dashboard.summary()` (Task 017 factory; inline
 * queryKey literals are banned by the R1 scan). Gated on the Task 019
 * session — no feature query fires before authentication.
 */
export function useDashboardSummary(): UseQueryResult<DashboardSummary, AppError> {
  const enabled = useIsAuthenticated();
  return useQuery<DashboardSummary, AppError>({
    queryKey: queryKeys.dashboard.summary(),
    queryFn: async (): Promise<DashboardSummary> => {
      try {
        return await fetchDashboardSummary();
      } catch (error) {
        throw normalizeError(error, { method: 'GET' });
      }
    },
    enabled,
  });
}

function toFiniteNumber(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

function toNonEmptyString(value: unknown): string | undefined {
  return typeof value === 'string' && value !== '' ? value : undefined;
}

/** Counts slice; undefined when the payload section is missing/invalid (R3). */
export function getCountsSlice(summary: Partial<DashboardSummary> | undefined): CountsSlice | undefined {
  const section = summary?.projectCounts as
    | { active?: unknown; archived?: unknown; total?: unknown }
    | undefined;
  if (section === undefined || section === null) {
    return undefined;
  }
  const active = toFiniteNumber(section.active);
  const archived = toFiniteNumber(section.archived);
  const total = toFiniteNumber(section.total);
  if (active === undefined || archived === undefined || total === undefined) {
    return undefined;
  }
  return { active, archived, total };
}

/** True when the tenant has zero projects → onboarding, not a zero grid. */
export function isEmptyTenant(summary: Partial<DashboardSummary> | undefined): boolean {
  const counts = getCountsSlice(summary);
  return counts !== undefined && counts.total === 0;
}

/** Recent-output rows; undefined when the section is not an array (R3). */
export function getRecentOutputs(summary: Partial<DashboardSummary> | undefined): RecentOutputView[] | undefined {
  const raw = summary?.recentOutputs as unknown;
  if (!Array.isArray(raw)) {
    return undefined;
  }
  return raw.map((entry, index) => {
    const record = (typeof entry === 'object' && entry !== null ? entry : {}) as Record<string, unknown>;
    return {
      id: toNonEmptyString(record['id']) ?? `output-${index}`,
      projectId: toNonEmptyString(record['projectId']) ?? toNonEmptyString(record['project_id']) ?? '',
      mediaKind: toNonEmptyString(record['mediaKind']) ?? 'unknown',
      container: toNonEmptyString(record['container']) ?? 'unknown',
      createdAt: toNonEmptyString(record['createdAt']) ?? '',
    };
  });
}

/** Storage slice; undefined when missing/invalid (R3). */
export function getStorageSlice(summary: Partial<DashboardSummary> | undefined): StorageSlice | undefined {
  const section = summary?.storage as { usedBytes?: unknown; quotaBytes?: unknown } | undefined;
  if (section === undefined || section === null) {
    return undefined;
  }
  const usedBytes = toFiniteNumber(section.usedBytes);
  const quotaBytes = toFiniteNumber(section.quotaBytes);
  if (usedBytes === undefined || quotaBytes === undefined) {
    return undefined;
  }
  return { usedBytes, quotaBytes };
}

/** Cost slice; undefined when missing/invalid (R3). */
export function getCostSlice(summary: Partial<DashboardSummary> | undefined): CostSlice | undefined {
  const section = summary?.cost as { monthToDate?: unknown; currency?: unknown } | undefined;
  if (section === undefined || section === null) {
    return undefined;
  }
  const monthToDate = toFiniteNumber(section.monthToDate);
  const currency = toNonEmptyString(section.currency) ?? 'USD';
  if (monthToDate === undefined) {
    return undefined;
  }
  return { monthToDate, currency };
}

/** Quota slice; undefined when missing/invalid (R3). */
export function getQuotaSlice(summary: Partial<DashboardSummary> | undefined): QuotaSlice | undefined {
  const section = summary?.quota as { remaining?: unknown; resetsAt?: unknown } | undefined;
  if (section === undefined || section === null) {
    return undefined;
  }
  const remaining = toFiniteNumber(section.remaining);
  const resetsAt = toNonEmptyString(section.resetsAt);
  if (remaining === undefined || resetsAt === undefined) {
    return undefined;
  }
  return { remaining, resetsAt };
}

/** Backlog slice; undefined when missing/invalid (R3). */
export function getBacklogSlice(summary: Partial<DashboardSummary> | undefined): BacklogSlice | undefined {
  const section = summary?.backlog as { pendingReviews?: unknown; runningJobs?: unknown } | undefined;
  if (section === undefined || section === null) {
    return undefined;
  }
  const pendingReviews = toFiniteNumber(section.pendingReviews);
  const runningJobs = toFiniteNumber(section.runningJobs);
  if (pendingReviews === undefined || runningJobs === undefined) {
    return undefined;
  }
  return { pendingReviews, runningJobs };
}

/**
 * Warning rows; undefined when the section is not an array (R3). Empty is
 * valid and renders as hidden (never an empty card).
 */
export function getWarningViews(summary: Partial<DashboardSummary> | undefined): WarningView[] | undefined {
  const raw = summary?.warnings as unknown;
  if (!Array.isArray(raw)) {
    return undefined;
  }
  return raw.map((entry, index) => {
    const record = (typeof entry === 'object' && entry !== null ? entry : {}) as Record<string, unknown>;
    const projectId = toNonEmptyString(record['projectId']);
    return {
      code: toNonEmptyString(record['code']) ?? `WARNING_${index}`,
      message: toNonEmptyString(record['message']) ?? 'A warning needs attention.',
      projectId,
    };
  });
}

/**
 * Storage usage ratio in [0, +∞). Zero when the quota is unknown/non-positive
 * (never NaN, never throws) so the meter degrades to empty, not broken.
 */
export function getStorageUsageRatio(storage: StorageSlice | undefined): number {
  if (storage === undefined || storage.quotaBytes <= 0) {
    return 0;
  }
  if (storage.usedBytes <= 0) {
    return 0;
  }
  return storage.usedBytes / storage.quotaBytes;
}

/** True at ≥80% of the storage quota (R2 warning meter). */
export function isStorageWarning(storage: StorageSlice | undefined): boolean {
  return getStorageUsageRatio(storage) >= QUOTA_WARNING_RATIO;
}

/** True at ≥100% of the storage quota (R2 blocked state). */
export function isStorageBlocked(storage: StorageSlice | undefined): boolean {
  return getStorageUsageRatio(storage) >= 1;
}

/** True when the daily project quota is exhausted (remaining ≤ 0). */
export function isDailyQuotaExhausted(quota: QuotaSlice | undefined): boolean {
  return quota !== undefined && quota.remaining <= 0;
}

/** True when costly actions pause: storage full or daily quota exhausted. */
export function isQuotaBlocked(storage: StorageSlice | undefined, quota: QuotaSlice | undefined): boolean {
  return isStorageBlocked(storage) || isDailyQuotaExhausted(quota);
}
