import type { ReactNode } from 'react';
import { Alert } from '../../components/Alert/Alert.js';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { formatNumber } from '../../i18n/format.js';
import { useAppStore } from '../../stores/index.js';
import { deriveQuotaState, iconForQuotaState, isQuotaBlocking, toneForQuotaState } from '../cost/types.js';
import { isAdminForbiddenError, usageRatio } from './types.js';
import { useAdminQuotas, useAdminUsage } from './useAdminQueries.js';

/**
 * Usage-vs-quota panel (Task 036).
 *
 * Reads `GET /admin/usage` + `GET /admin/quotas` on the diagnostics factory
 * scope. Quota states reuse the Task 035 derivation (`deriveQuotaState` plus
 * tone/icon helpers — never color alone): remaining comes from the daily
 * project allowance, the storage ratio from usage bytes vs quota bytes.
 * Counts and bytes only; reservation ids never enter these shapes.
 */
export function UsageQuotasPanel(): ReactNode {
  const locale = useAppStore((s) => s.locale);
  const usageQuery = useAdminUsage();
  const quotasQuery = useAdminQuotas();

  const loading = (usageQuery.isPending && usageQuery.data === undefined) || (quotasQuery.isPending && quotasQuery.data === undefined);
  if (loading) {
    return (
      <section data-testid="admin-usage" aria-label="Usage and quotas">
        <div data-testid="admin-usage-loading">
          <Skeleton lines={5} />
        </div>
      </section>
    );
  }

  const forbidden = isAdminForbiddenError(usageQuery.error) || isAdminForbiddenError(quotasQuery.error);
  if (forbidden && usageQuery.data === undefined && quotasQuery.data === undefined) {
    return (
      <section data-testid="admin-usage" aria-label="Usage and quotas">
        <div data-testid="admin-usage-forbidden">
          <EmptyState
            title="Usage unavailable"
            description="You do not have permission to view usage. Contact your tenant admin for access."
          />
        </div>
      </section>
    );
  }

  const failed = (usageQuery.isError && usageQuery.data === undefined) || (quotasQuery.isError && quotasQuery.data === undefined);
  if (failed) {
    const error = (usageQuery.isError ? usageQuery.error : undefined) ?? (quotasQuery.isError ? quotasQuery.error : undefined);
    return (
      <section data-testid="admin-usage" aria-label="Usage and quotas">
        <div data-testid="admin-usage-error">
          <ErrorState
            title="Usage unavailable"
            message={error?.message ?? 'Usage could not be loaded. No data was changed.'}
            correlationId={error?.correlationId}
            onRetry={() => {
              void usageQuery.refetch();
              void quotasQuery.refetch();
            }}
          />
        </div>
      </section>
    );
  }

  const usage = usageQuery.data;
  const quotas = quotasQuery.data;
  if (usage === undefined && quotas === undefined) {
    return (
      <section data-testid="admin-usage" aria-label="Usage and quotas">
        <div data-testid="admin-usage-empty">
          <EmptyState title="No usage data" description="Usage aggregates are not reported yet." />
        </div>
      </section>
    );
  }

  const quotaState = deriveQuotaState({
    remaining: usage?.projectsTodayRemaining,
    usedBytes: usage?.storageUsedBytes,
    quotaBytes: usage?.storageQuotaBytes,
    reservedUsd: undefined,
  });
  const tone = toneForQuotaState(quotaState);
  const icon = iconForQuotaState(quotaState);
  const storageRatio = usageRatio(usage?.storageUsedBytes, usage?.storageQuotaBytes);
  const storagePercent = Math.min(100, Math.round(storageRatio * 100));

  return (
    <section data-testid="admin-usage" aria-label="Usage and quotas" data-quota={quotaState} data-blocked={isQuotaBlocking(quotaState) ? 'true' : 'false'}>
      <h3>Usage vs quotas</h3>
      <div data-testid={`admin-usage-quota-${quotaState}`} data-tone={tone}>
        <Alert tone={tone} title={`Quota ${quotaState}`}>
          <p>
            <span data-testid="admin-usage-quota-icon" aria-hidden="true">
              {icon}
            </span>{' '}
            <span data-testid="admin-usage-quota-state">{quotaState}</span>
            {usage?.projectsTodayRemaining !== undefined ? (
              <span data-testid="admin-usage-quota-remaining"> · {String(usage.projectsTodayRemaining)} projects remaining today</span>
            ) : null}
          </p>
        </Alert>
      </div>
      <dl data-testid="admin-usage-bars">
        <div>
          <dt>Storage</dt>
          <dd data-testid="admin-usage-storage">
            {usage?.storageUsedBytes !== undefined && usage?.storageQuotaBytes !== undefined
              ? `${formatNumber(usage.storageUsedBytes, { locale })} / ${formatNumber(usage.storageQuotaBytes, { locale })} (${String(storagePercent)}%)`
              : 'Unavailable'}
          </dd>
        </div>
        <div>
          <dt>Month cost</dt>
          <dd data-testid="admin-usage-cost">
            {usage?.monthCostUsd !== undefined ? formatNumber(usage.monthCostUsd, { locale, style: 'currency', currency: 'USD' }) : 'Unavailable'}
          </dd>
        </div>
        <div>
          <dt>Active runs</dt>
          <dd data-testid="admin-usage-runs">{usage !== undefined ? String(usage.activeRuns) : 'Unavailable'}</dd>
        </div>
        <div>
          <dt>Pending reviews</dt>
          <dd data-testid="admin-usage-reviews">{usage !== undefined ? String(usage.pendingReviews) : 'Unavailable'}</dd>
        </div>
        <div>
          <dt>Total projects</dt>
          <dd data-testid="admin-usage-projects">{usage !== undefined ? String(usage.totalProjects) : 'Unavailable'}</dd>
        </div>
      </dl>
      {quotas !== undefined ? (
        <dl data-testid="admin-quotas-limits">
          <div>
            <dt>Max active projects</dt>
            <dd data-testid="admin-quotas-active">{quotas.maxActiveProjects !== undefined ? String(quotas.maxActiveProjects) : '—'}</dd>
          </div>
          <div>
            <dt>Max projects per day</dt>
            <dd data-testid="admin-quotas-daily">{quotas.maxProjectsPerDay !== undefined ? String(quotas.maxProjectsPerDay) : '—'}</dd>
          </div>
          <div>
            <dt>Max cost per project</dt>
            <dd data-testid="admin-quotas-cost">{quotas.maxCostPerProject !== undefined ? String(quotas.maxCostPerProject) : '—'}</dd>
          </div>
          <div>
            <dt>Max storage bytes</dt>
            <dd data-testid="admin-quotas-storage">{quotas.maxStorageBytes !== undefined ? String(quotas.maxStorageBytes) : '—'}</dd>
          </div>
          <div>
            <dt>Max concurrent stages</dt>
            <dd data-testid="admin-quotas-stages">{quotas.maxConcurrentStagesPerTenant !== undefined ? String(quotas.maxConcurrentStagesPerTenant) : '—'}</dd>
          </div>
        </dl>
      ) : null}
    </section>
  );
}
