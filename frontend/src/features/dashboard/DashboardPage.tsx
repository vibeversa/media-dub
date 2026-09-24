import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { hasAdminPermission } from '../../app/session/permissions.js';
import { useAppStore } from '../../stores/index.js';
import { BacklogCard } from './BacklogCard.js';
import { ProviderWarnings } from './ProviderWarnings.js';
import { QuotaCard } from './QuotaCard.js';
import { RecentOutputs } from './RecentOutputs.js';
import { StatCard } from './StatCard.js';
import { StorageCostCard } from './StorageCostCard.js';
import {
  getBacklogSlice,
  getCostSlice,
  getCountsSlice,
  getQuotaSlice,
  getRecentOutputs,
  getStorageSlice,
  getWarningViews,
  isEmptyTenant,
  useDashboardSummary,
} from './api.js';

/**
 * Tenant dashboard (Task 020): first landing screen after login, mounted in
 * the Task 018 shell at `/dashboard`.
 *
 * - Single aggregate source: `GET /dashboard/summary` via
 *   `useDashboardSummary` (R1 — no other aggregate calls; drill-downs are
 *   client-side links to existing routes).
 * - States: loading skeletons; empty-tenant onboarding (zero projects → CTA
 *   to the Task 022 creation wizard at `/projects/new`); per-card errors
 *   with retry (R3); quota warning/blocked meter (R2); global error with
 *   retry. Stale cache renders stale data plus a background-refresh indicator.
 * - Security: storage/cost/quota cards render only with the usage permission
 *   (`hasAdminPermission`); hidden otherwise with no error flash. No per-user
 *   rows are ever rendered from this tenant aggregate.
 */
export function DashboardPage(): ReactNode {
  const { t } = useTranslation();
  const permissions = useAppStore((s) => s.permissions);
  const canViewUsage = hasAdminPermission(permissions);
  const summaryQuery = useDashboardSummary();
  const { data, isError, isFetching, isPending, error, refetch } = summaryQuery;

  const handleRetry = (): void => {
    void refetch();
  };

  const counts = getCountsSlice(data);
  const outputs = getRecentOutputs(data);
  const storage = getStorageSlice(data);
  const cost = getCostSlice(data);
  const quota = getQuotaSlice(data);
  const warnings = getWarningViews(data);
  const backlog = getBacklogSlice(data);
  const showStale = data !== undefined && isFetching;

  let body: ReactNode;
  if (isPending && data === undefined) {
    body = (
      <div data-testid="dashboard-loading">
        <Skeleton lines={6} />
      </div>
    );
  } else if (isError && data === undefined) {
    body = (
      <div data-testid="dashboard-error">
        <ErrorState
          title={t('dashboard:loadError.title')}
          message={error?.message ?? t('dashboard:loadError.message')}
          correlationId={error?.correlationId}
          onRetry={handleRetry}
        />
      </div>
    );
  } else if (isEmptyTenant(data)) {
    body = (
      <div data-testid="dashboard-empty">
        <EmptyState
          title={t('dashboard:empty.title')}
          description={t('dashboard:empty.description')}
          action={
            <Link to="/projects/new" data-testid="dashboard-empty-cta">
              {t('dashboard:empty.cta')}
            </Link>
          }
        />
      </div>
    );
  } else {
    body = (
      <div data-testid="dashboard-grid">
        <StatCard counts={counts} onRetry={handleRetry} />
        <RecentOutputs outputs={outputs} onRetry={handleRetry} />
        {canViewUsage ? <StorageCostCard storage={storage} cost={cost} onRetry={handleRetry} /> : null}
        {canViewUsage ? <QuotaCard quota={quota} storage={storage} onRetry={handleRetry} /> : null}
        <ProviderWarnings warnings={warnings} onRetry={handleRetry} />
        <BacklogCard backlog={backlog} onRetry={handleRetry} />
      </div>
    );
  }

  return (
    <section data-testid="page-dashboard">
      <h1 className="text-xl font-semibold">{t('dashboard:title')}</h1>
      <p className="mt-2 text-sm text-slate-600">{t('dashboard:subtitle')}</p>
      {showStale ? (
        <p role="status" data-testid="dashboard-stale-indicator">
          {t('dashboard:stale')}
        </p>
      ) : null}
      {body}
    </section>
  );
}
