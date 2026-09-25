import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Alert } from '../../components/Alert/Alert.js';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { formatDate, formatNumber } from '../../i18n/format.js';
import { useAppStore } from '../../stores/index.js';
import { iconForQuotaState, isQuotaBlocking, toneForQuotaState } from './types.js';
import { useCostQuota } from './useCostQuota.js';

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
 * Cost + quota summary (Task 035, R2/R3).
 *
 * - Estimated vs actual are always distinguished: the client-side preflight
 *   mirror renders with the `Estimate` label (planning figure only, never a
 *   promise); metered server actuals render without it.
 * - Breakdown covers duration (workspace media), storage bytes (dashboard),
 *   and month/run actuals. A 404 cost section (run predates cost tracking)
 *   renders `UnavailableState` — hidden, never zero-filled.
 * - Quota states `available/near/exceeded/reserved` map to distinct tones
 *   plus text glyphs (never color alone); reserved amounts are informational
 *   totals only — reservation ids never render (the parser drops them).
 */
export function CostSummary({ projectId }: CostSummaryProps): ReactNode {
  const locale = useAppStore((s) => s.locale);
  const tenantTimezone = useAppStore((s) => s.tenantTimezone);
  const costQuery = useCostQuota(projectId);
  const { data, isPending, isError, error, refetch } = costQuery;

  if (projectId === '') {
    return (
      <section data-testid="cost-summary" aria-label="Cost summary">
        <div data-testid="cost-needs-project">
          <EmptyState title="Select a project" description="Enter a project id to load its cost summary." />
        </div>
      </section>
    );
  }

  if (isPending && data === undefined) {
    return (
      <section data-testid="cost-summary" aria-label="Cost summary">
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
        <section data-testid="cost-summary" aria-label="Cost summary">
          <div data-testid="cost-unavailable">
            <EmptyState title="Cost unavailable" description="This run predates cost tracking. No estimate is shown." />
          </div>
        </section>
      );
    }
    return (
      <section data-testid="cost-summary" aria-label="Cost summary">
        <div data-testid="cost-error">
          <ErrorState
            title="Cost unavailable"
            message={error?.message ?? 'Cost could not be loaded. No data was changed.'}
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
      <section data-testid="cost-summary" aria-label="Cost summary">
        <div data-testid="cost-unavailable">
          <EmptyState title="Cost unavailable" description="This run predates cost tracking. No estimate is shown." />
        </div>
      </section>
    );
  }

  const { cost, quota } = view;
  const estimatedText = formatNumber(cost.estimatedUsd, { locale, style: 'currency', currency: cost.currency });
  const tone = toneForQuotaState(quota.state);
  const icon = iconForQuotaState(quota.state);
  const blocking = isQuotaBlocking(quota.state);

  return (
    <section data-testid="cost-summary" aria-label="Cost summary" data-quota={quota.state} data-blocked={blocking ? 'true' : 'false'}>
      <h3>Cost summary</h3>
      <dl>
        <div>
          <dt>Estimated</dt>
          <dd data-testid="cost-estimated">
            {estimatedText} <span data-testid="cost-estimate-label">Estimate</span>
          </dd>
        </div>
        <div>
          <dt>Actual run cost</dt>
          <dd data-testid="cost-actual">
            {cost.actualRunUsd !== undefined
              ? formatNumber(cost.actualRunUsd, { locale, style: 'currency', currency: cost.currency })
              : 'Unavailable'}
          </dd>
        </div>
        <div>
          <dt>Actual month to date</dt>
          <dd data-testid="cost-actual-month">
            {cost.actualMonthUsd !== undefined
              ? formatNumber(cost.actualMonthUsd, { locale, style: 'currency', currency: cost.currency })
              : 'Unavailable'}
          </dd>
        </div>
        <div>
          <dt>Duration</dt>
          <dd data-testid="cost-duration">{cost.durationMs !== undefined ? formatDurationMs(cost.durationMs) : '—'}</dd>
        </div>
        <div>
          <dt>Storage</dt>
          <dd data-testid="cost-storage">
            {cost.storageUsedBytes !== undefined && cost.storageQuotaBytes !== undefined
              ? `${formatNumber(cost.storageUsedBytes, { locale })} / ${formatNumber(cost.storageQuotaBytes, { locale })}`
              : '—'}
          </dd>
        </div>
        {quota.reservedUsd !== undefined ? (
          <div>
            <dt>Reserved</dt>
            <dd data-testid="cost-reserved">
              {formatNumber(quota.reservedUsd, { locale, style: 'currency', currency: cost.currency })}{' '}
              <span className="dp-muted" data-testid="cost-reserved-note">
                Informational hold only
              </span>
            </dd>
          </div>
        ) : null}
      </dl>
      <p className="dp-muted" data-testid="cost-estimate-note">
        Estimates are planning figures only — never promises. Actual cost is metered server-side.
      </p>
      <div data-testid={`cost-quota-${quota.state}`} data-tone={tone}>
        <Alert tone={tone} title={`Quota ${quota.state}`}>
          <p>
            <span data-testid="cost-quota-icon" aria-hidden="true">
              {icon}
            </span>{' '}
            <span data-testid="cost-quota-state">{quota.state}</span>
            {quota.remaining !== undefined ? (
              <span data-testid="cost-quota-remaining"> · {String(quota.remaining)} remaining</span>
            ) : null}
            {quota.resetsAt !== undefined ? (
              <span data-testid="cost-quota-resets">
                {' '}
                · Resets {formatDate(quota.resetsAt, { locale, timeZone: tenantTimezone })}
              </span>
            ) : null}
          </p>
          <p>
            <Link data-testid="cost-quota-manage" to="/settings">
              Manage in settings
            </Link>
          </p>
        </Alert>
      </div>
    </section>
  );
}
