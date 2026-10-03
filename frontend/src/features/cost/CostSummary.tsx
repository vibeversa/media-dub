import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Alert } from '../../components/Alert/Alert.js';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { formatDate, formatNumber } from '../../i18n/format.js';
import { useAppStore } from '../../stores/index.js';
import { QuotaBadge } from './QuotaBadge.js';
import { iconForQuotaState, isQuotaBlocking, toneForQuotaState } from './types.js';
import { useCostQuota } from './useCostQuota.js';
import { useTranslation } from 'react-i18next';

export interface CostSummaryProps {
  readonly projectId: string;
}

function formatDurationMs(value: number): string {
  if (!Number.isFinite(value) || value <= 0) {
    return '—';
  }
  const totalSeconds = Math.round(value / 1000);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  if (minutes <= 0) {
    return `${String(seconds)}s`;
  }
  return `${String(minutes)}m ${String(seconds)}s`;
}

/**
 * Cost + quota summary (Task 035A, R3/R4).
 *
 * - Estimated vs actual are always distinguished: the client-side preflight
 *   mirror renders with the `Estimate` label (planning figure only, never a
 *   promise); metered server actuals render without it.
 * - Breakdown covers duration (workspace media), provider units (priced
 *   segment-count mirror, planning figure only), and storage bytes
 *   (dashboard), plus month/run actuals. A 404 cost section (run predates
 *   cost tracking) renders `UnavailableState` — hidden, never zero-filled.
 * - A 403 cost section (backend gates cost to authorized roles) renders
 *   `ForbiddenState` — details hidden without bypassing, with a
 *   `contact admin` recovery hint.
 * - Quota states `available/near/exceeded/reserved` map to distinct tones
 *   plus text glyphs (never color alone) via `QuotaBadge` + the detailed
 *   quota Alert; reserved amounts are informational totals only —
 *   reservation ids never render (the parser drops them).
 * - Projection-lag / background refresh renders a `partial` notice with
 *   refetch (never an error toast) while the last loaded figures stay
 *   visible.
 */
export function CostSummary({ projectId }: CostSummaryProps): ReactNode {
    const { t } = useTranslation();
const locale = useAppStore((s) => s.locale);
  const tenantTimezone = useAppStore((s) => s.tenantTimezone);
  const costQuery = useCostQuota(projectId);
  const { data, isPending, isError, isFetching, error, refetch } = costQuery;

  if (projectId === '') {
    return (
      <section data-testid="cost-summary" aria-label={t('cost:costSummary.cost-summary')}>
        <div data-testid="cost-needs-project">
          <EmptyState title={t('cost:costSummary.select-a-project')} description={t('cost:costSummary.enter-a-project-id-to-load')} />
        </div>
      </section>
    );
  }

  if (isPending && data === undefined) {
    return (
      <section data-testid="cost-summary" aria-label={t('cost:costSummary.cost-summary2')}>
        <div data-testid="cost-loading">
          <Skeleton lines={5} />
        </div>
      </section>
    );
  }

  if (isError && data === undefined) {
    const status = error?.status ?? 0;
    if (status === 404) {
      return (
        <section data-testid="cost-summary" aria-label={t('cost:costSummary.cost-summary3')}>
          <div data-testid="cost-unavailable">
            <EmptyState title={t('cost:costSummary.cost-unavailable')} description={t('cost:costSummary.this-run-predates-cost-tracking-no')} />
          </div>
        </section>
      );
    }
    if (status === 403) {
      return (
        <section data-testid="cost-summary" aria-label={t('cost:costSummary.cost-summary4')}>
          <div data-testid="cost-forbidden">
            <EmptyState
              title={t('cost:costSummary.cost-visibility-restricted')}
              description={t('cost:costSummary.cost-details-are-visible-to-authorized')}
            />
          </div>
        </section>
      );
    }
    return (
      <section data-testid="cost-summary" aria-label={t('cost:costSummary.cost-summary5')}>
        <div data-testid="cost-error">
          <ErrorState
            title={t('cost:costSummary.cost-unavailable2')}
            message={error?.message ?? t('cost:costSummary.cost-could-not-be-loaded-no')}
            correlationId={error?.correlationId}
            onRetry={() => {
              void refetch();
            }}
          />
        </div>
      </section>
    );
  }

  const view = data as NonNullable<typeof data>;
  if (view.costUnavailable) {
    return (
      <section data-testid="cost-summary" aria-label={t('cost:costSummary.cost-summary6')}>
        <div data-testid="cost-unavailable">
          <EmptyState title={t('cost:costSummary.cost-unavailable3')} description={t('cost:costSummary.this-run-predates-cost-tracking-no2')} />
        </div>
      </section>
    );
  }

  const { cost, quota } = view;
  const estimatedText = formatNumber(cost.estimatedUsd, { locale, style: 'currency', currency: cost.currency });
  const tone = toneForQuotaState(quota.state);
  const icon = iconForQuotaState(quota.state);
  const blocking = isQuotaBlocking(quota.state);
  const showPartial = !isPending && isFetching && data !== undefined;

  return (
    <section data-testid="cost-summary" aria-label={t('cost:costSummary.cost-summary7')} data-quota={quota.state} data-blocked={blocking ? 'true' : 'false'}>
      <h3>{t('cost:costSummary.cost-summary8')}</h3>
      {showPartial ? (
        <div data-testid="cost-partial">
          <Alert tone="warning" title={t('cost:costSummary.cost-figures-refreshing')}>
            <p>{t('cost:costSummary.showing-the-last-loaded-figures-new')}</p>
            <button
              type="button"
              data-testid="cost-partial-refresh"
              className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
              onClick={() => {
                void refetch();
              }}
            >
              {t('cost:costSummary.refresh-cost')}
            </button>
          </Alert>
        </div>
      ) : null}
      <div data-testid="cost-quota-badge-wrap">
        <QuotaBadge state={quota.state} remaining={quota.remaining} />
      </div>
      <dl>
        <div>
          <dt>{t('cost:costSummary.estimated')}</dt>
          <dd data-testid="cost-estimated">
            {estimatedText} <span data-testid="cost-estimate-label">{t('cost:costSummary.estimate')}</span>
          </dd>
        </div>
        <div>
          <dt>{t('cost:costSummary.actual-run-cost')}</dt>
          <dd data-testid="cost-actual">
            {cost.actualRunUsd !== undefined
              ? formatNumber(cost.actualRunUsd, { locale, style: 'currency', currency: cost.currency })
              : 'Unavailable'}
          </dd>
        </div>
        <div>
          <dt>{t('cost:costSummary.actual-month-to-date')}</dt>
          <dd data-testid="cost-actual-month">
            {cost.actualMonthUsd !== undefined
              ? formatNumber(cost.actualMonthUsd, { locale, style: 'currency', currency: cost.currency })
              : 'Unavailable'}
          </dd>
        </div>
        <div>
          <dt>{t('cost:costSummary.duration')}</dt>
          <dd data-testid="cost-duration">{cost.durationMs !== undefined ? formatDurationMs(cost.durationMs) : '—'}</dd>
        </div>
        <div>
          <dt>{t('cost:costSummary.provider-units')}</dt>
          <dd data-testid="cost-provider-units">
            {cost.providerUnits !== undefined ? `${String(cost.providerUnits)} segments (planning figure)` : '—'}
          </dd>
        </div>
        <div>
          <dt>{t('cost:costSummary.storage')}</dt>
          <dd data-testid="cost-storage">
            {cost.storageUsedBytes !== undefined && cost.storageQuotaBytes !== undefined
              ? `${formatNumber(cost.storageUsedBytes, { locale })} / ${formatNumber(cost.storageQuotaBytes, { locale })}`
              : '—'}
          </dd>
        </div>
        {quota.reservedUsd !== undefined ? (
          <div>
            <dt>{t('cost:costSummary.reserved')}</dt>
            <dd data-testid="cost-reserved">
              {formatNumber(quota.reservedUsd, { locale, style: 'currency', currency: cost.currency })}{' '}
              <span className="dp-muted" data-testid="cost-reserved-note">
                {t('cost:costSummary.informational-hold-only')}
              </span>
            </dd>
          </div>
        ) : null}
      </dl>
      <p className="dp-muted" data-testid="cost-estimate-note">
        {t('cost:costSummary.estimates-are-planning-figures-only-never')}
      </p>
      <div data-testid={`cost-quota-${quota.state}`} data-tone={tone}>
        <Alert tone={tone} title={t('cost:costSummary.quota', { v0: quota.state })}>
          <p>
            <span data-testid="cost-quota-icon" aria-hidden="true">
              {icon}
            </span>{' '}
            <span data-testid="cost-quota-state">{quota.state}</span>
            {quota.remaining !== undefined ? (
              <span data-testid="cost-quota-remaining"> · {String(quota.remaining)} {t('cost:costSummary.remaining')}</span>
            ) : null}
            {quota.resetsAt !== undefined ? (
              <span data-testid="cost-quota-resets">
                {' '}
                {t('cost:costSummary.resets')} {formatDate(quota.resetsAt, { locale, timeZone: tenantTimezone })}
              </span>
            ) : null}
          </p>
          <p>
            <Link data-testid="cost-quota-manage" to="/settings">
              {t('cost:costSummary.manage-in-settings')}
            </Link>
          </p>
        </Alert>
      </div>
    </section>
  );
}
