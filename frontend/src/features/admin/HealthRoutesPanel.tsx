import type { ReactNode } from 'react';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { formatDate } from '../../i18n/format.js';
import { useAppStore } from '../../stores/index.js';
import { isAdminForbiddenError, maskConnectionString } from './types.js';
import { useProviderHealth, useProviderRoutes } from './useAdminQueries.js';
import { useTranslation } from 'react-i18next';

/**
 * Provider health/routes matrix (Task 036).
 *
 * Reads `GET /admin/provider-health` + `GET /admin/provider-routes` on the
 * diagnostics factory scope. Secret-free: only names, statuses, latency/error
 * aggregates, timestamps, route names, and circuit states render. Any
 * endpoint-looking value is masked via `maskConnectionString` (fingerprints
 * only, never reversible).
 */
export function HealthRoutesPanel(): ReactNode {
    const { t } = useTranslation();
const locale = useAppStore((s) => s.locale);
  const tenantTimezone = useAppStore((s) => s.tenantTimezone);
  const healthQuery = useProviderHealth();
  const routesQuery = useProviderRoutes();

  const loading = (healthQuery.isPending && healthQuery.data === undefined) || (routesQuery.isPending && routesQuery.data === undefined);
  if (loading) {
    return (
      <section data-testid="admin-health" aria-label={t('admin:healthRoutesPanel.provider-health-and-routes')}>
        <div data-testid="admin-health-loading">
          <Skeleton lines={4} />
        </div>
      </section>
    );
  }

  const forbidden = isAdminForbiddenError(healthQuery.error) || isAdminForbiddenError(routesQuery.error);
  if (forbidden && healthQuery.data === undefined && routesQuery.data === undefined) {
    return (
      <section data-testid="admin-health" aria-label={t('admin:healthRoutesPanel.provider-health-and-routes2')}>
        <div data-testid="admin-health-forbidden">
          <EmptyState
            title={t('admin:healthRoutesPanel.provider-health-unavailable')}
            description={t('admin:healthRoutesPanel.you-do-not-have-permission-to')}
          />
        </div>
      </section>
    );
  }

  const failed = (healthQuery.isError && healthQuery.data === undefined) || (routesQuery.isError && routesQuery.data === undefined);
  if (failed) {
    const error = (healthQuery.isError ? healthQuery.error : undefined) ?? (routesQuery.isError ? routesQuery.error : undefined);
    return (
      <section data-testid="admin-health" aria-label={t('admin:healthRoutesPanel.provider-health-and-routes3')}>
        <div data-testid="admin-health-error">
          <ErrorState
            title={t('admin:healthRoutesPanel.provider-health-unavailable2')}
            message={error?.message ?? t('admin:healthRoutesPanel.provider-health-could-not-be-loaded')}
            correlationId={error?.correlationId}
            onRetry={() => {
              void healthQuery.refetch();
              void routesQuery.refetch();
            }}
          />
        </div>
      </section>
    );
  }

  const health = healthQuery.data ?? [];
  const routes = routesQuery.data ?? [];
  if (health.length === 0 && routes.length === 0) {
    return (
      <section data-testid="admin-health" aria-label={t('admin:healthRoutesPanel.provider-health-and-routes4')}>
        <div data-testid="admin-health-empty">
          <EmptyState title={t('admin:healthRoutesPanel.no-provider-data')} description={t('admin:healthRoutesPanel.no-provider-health-snapshots-or-routes')} />
        </div>
      </section>
    );
  }

  return (
    <section data-testid="admin-health" aria-label={t('admin:healthRoutesPanel.provider-health-and-routes5')}>
      <h3>{t('admin:healthRoutesPanel.provider-health')}</h3>
      {health.length === 0 ? (
        <div data-testid="admin-health-list-empty">
          <EmptyState title={t('admin:healthRoutesPanel.no-health-snapshots')} description={t('admin:healthRoutesPanel.providers-have-not-reported-health-yet')} />
        </div>
      ) : (
        <table data-testid="admin-health-table">
          <thead>
            <tr>
              <th scope="col">{t('admin:healthRoutesPanel.provider')}</th>
              <th scope="col">{t('admin:healthRoutesPanel.status')}</th>
              <th scope="col">{t('admin:healthRoutesPanel.p95-latency')}</th>
              <th scope="col">{t('admin:healthRoutesPanel.error-rate')}</th>
              <th scope="col">{t('admin:healthRoutesPanel.last-success')}</th>
              <th scope="col">{t('admin:healthRoutesPanel.circuit')}</th>
            </tr>
          </thead>
          <tbody>
            {health.map((provider) => (
              <tr key={provider.provider} data-testid={`admin-health-row-${provider.provider}`}>
                <td data-testid={`admin-health-provider-${provider.provider}`}>{provider.provider}</td>
                <td data-testid={`admin-health-status-${provider.provider}`}>{provider.status}</td>
                <td data-testid={`admin-health-latency-${provider.provider}`}>
                  {provider.latencyMsP95 !== undefined ? `${String(provider.latencyMsP95)}ms` : '—'}
                </td>
                <td data-testid={`admin-health-errors-${provider.provider}`}>{`${String(Math.round(provider.errorRate * 100))}%`}</td>
                <td data-testid={`admin-health-success-${provider.provider}`}>
                  {provider.lastSuccessAt !== '' ? formatDate(provider.lastSuccessAt, { locale, timeZone: tenantTimezone }) : '—'}
                </td>
                <td data-testid={`admin-health-circuit-${provider.provider}`}>{provider.circuitBreakerState}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <h3>{t('admin:healthRoutesPanel.provider-routes')}</h3>
      {routes.length === 0 ? (
        <div data-testid="admin-routes-empty">
          <EmptyState title={t('admin:healthRoutesPanel.no-provider-routes')} description={t('admin:healthRoutesPanel.no-capability-routes-are-configured')} />
        </div>
      ) : (
        <table data-testid="admin-routes-table">
          <thead>
            <tr>
              <th scope="col">{t('admin:healthRoutesPanel.capability')}</th>
              <th scope="col">{t('admin:healthRoutesPanel.provider2')}</th>
              <th scope="col">{t('admin:healthRoutesPanel.priority')}</th>
              <th scope="col">{t('admin:healthRoutesPanel.enabled')}</th>
            </tr>
          </thead>
          <tbody>
            {routes.map((route) => (
              <tr key={`${route.capability}-${route.provider}`} data-testid={`admin-route-row-${route.capability}-${route.provider}`}>
                <td data-testid={`admin-route-capability-${route.capability}-${route.provider}`}>{route.capability}</td>
                <td data-testid={`admin-route-provider-${route.capability}-${route.provider}`}>
                  {route.provider.includes('://') ? maskConnectionString(route.provider) : route.provider}
                </td>
                <td data-testid={`admin-route-priority-${route.capability}-${route.provider}`}>{String(route.priority)}</td>
                <td data-testid={`admin-route-enabled-${route.capability}-${route.provider}`}>{route.enabled ? 'On' : 'Off'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
