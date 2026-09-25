import { useQuery } from '@tanstack/react-query';
import type { UseQueryResult } from '@tanstack/react-query';
import { apiClient } from '../../api/client/index.js';
import { normalizeError } from '../../api/errors/index.js';
import type { AppError } from '../../api/errors/index.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { useIsAuthenticated } from '../auth/useSession.js';
import { estimateRunCostUsd } from '../processing/usePreflight.js';
import { buildCostBreakdown, buildQuotaView } from './types.js';
import type { CostBreakdown, QuotaView } from './types.js';

/**
 * Cost + quota read model (Task 035) over the Task 007 dashboard aggregate
 * plus the Task 008 workspace aggregate.
 *
 * - Dashboard supplies tenant `cost/quota/storage` (month actuals, daily
 *   remaining, bytes). Workspace supplies the project run actual plus media
 *   duration for the breakdown.
 * - The estimate is a client-side preflight mirror (segments guessed from
 *   media duration exactly like the server `ceil(durationMs / 5000)` branch)
 *   and always renders with the `Estimate` label; actuals are metered
 *   server values. Missing cost sections yield undefined actuals so callers
 *   show `UnavailableState` instead of zero-filling.
 * - Quota states map distinctly via `buildQuotaView`; reservation ids never
 *   enter these shapes (reserved amounts are informational totals only).
 */

function guessSegmentsFromDuration(durationMs: number | undefined): number {
  if (durationMs === undefined || !Number.isFinite(durationMs) || durationMs <= 0) {
    return 10;
  }
  return Math.min(2000, Math.max(1, Math.ceil(durationMs / 5000)));
}

function toFiniteNumber(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

function toNonEmptyString(value: unknown): string | undefined {
  return typeof value === 'string' && value !== '' ? value : undefined;
}

export interface CostQuotaData {
  readonly cost: CostBreakdown;
  readonly quota: QuotaView;
  readonly costUnavailable: boolean;
}

async function fetchCostQuota(projectId: string): Promise<CostQuotaData> {
  let summary: Record<string, unknown>;
  let workspace: Record<string, unknown>;
  try {
    summary = (await apiClient.getDashboardSummary()) as unknown as Record<string, unknown>;
  } catch (error) {
    throw normalizeError(error, { method: 'GET' });
  }
  try {
    workspace = (await apiClient.getWorkspace({ path: { projectId } })) as unknown as Record<string, unknown>;
  } catch (error) {
    throw normalizeError(error, { method: 'GET' });
  }

  const costSection = (summary['cost'] ?? {}) as Record<string, unknown>;
  const quotaSection = (summary['quota'] ?? {}) as Record<string, unknown>;
  const storageSection = (summary['storage'] ?? {}) as Record<string, unknown>;
  const workspaceCost = ((workspace['cost'] ?? {}) as Record<string, unknown>);
  const workspaceMedia = ((workspace['media'] ?? {}) as Record<string, unknown>);

  const monthActual = toFiniteNumber(costSection['monthToDate']);
  const currency = toNonEmptyString(costSection['currency']) ?? 'USD';
  const remaining = toFiniteNumber(quotaSection['remaining']);
  const resetsAt = toNonEmptyString(quotaSection['resetsAt']);
  const usedBytes = toFiniteNumber(storageSection['usedBytes']);
  const quotaBytes = toFiniteNumber(storageSection['quotaBytes']);
  const runActual = toFiniteNumber(workspaceCost['runCost'] ?? workspaceCost['RunCost']);
  const workspaceMonth = toFiniteNumber(workspaceCost['monthToDate'] ?? workspaceCost['MonthToDate']);
  const durationMs = toFiniteNumber(workspaceMedia['durationMs'] ?? workspaceMedia['DurationMs']);

  const costUnavailable = monthActual === undefined && runActual === undefined && workspaceMonth === undefined;
  const actualMonth = monthActual ?? workspaceMonth;
  const segments = guessSegmentsFromDuration(durationMs);
  const estimated = estimateRunCostUsd(segments);

  const cost = buildCostBreakdown({
    estimatedUsd: estimated.amountUsd,
    actualRunUsd: runActual,
    actualMonthUsd: actualMonth,
    currency,
    durationMs,
    storageUsedBytes: usedBytes,
    storageQuotaBytes: quotaBytes,
  });
  const quota = buildQuotaView({ remaining, resetsAt, usedBytes, quotaBytes, reservedUsd: undefined });
  return { cost, quota, costUnavailable };
}

/** Cost + quota query for one project (dashboard + workspace, one hook). */
export function useCostQuota(projectId: string): UseQueryResult<CostQuotaData, AppError> {
  const enabled = useIsAuthenticated() && projectId !== '';
  return useQuery<CostQuotaData, AppError>({
    queryKey: queryKeys.cost.detail(projectId),
    queryFn: () => fetchCostQuota(projectId),
    enabled,
    staleTime: 30_000,
    refetchInterval: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    retry: false,
  });
}
