import { useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Alert } from '../../components/Alert/Alert.js';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { useToast } from '../../components/Toast/useToast.js';
import { formatDate } from '../../i18n/format.js';
import { useAppStore } from '../../stores/index.js';
import { DestructiveAction } from './DestructiveAction.js';
import { dlqRowsFromSummary, formatLeaseAge, isAdminConflictError, isAdminForbiddenError } from './types.js';
import { useTranslation } from 'react-i18next';
import {
  discardDlqEntry,
  invalidateAdminQueries,
  redriveDlqEntry,
  useAdminStatus,
  useOpsBacklog,
  useOpsDlq,
  useOpsLeases,
  useOpsOrphans,
  useOpsQueues,
  useProviderHealth,
} from './useAdminQueries.js';

/**
 * Operator diagnostics dashboard (Task 036, R3).
 *
 * Panels: queues, workers (derived from lease owners + the status probe),
 * errors (DLQ reason breakdown), DLQ (advertised-actions-only rows), leases
 * (age + owner, never raw lock tokens), orphans, review backlog, and failures
 * (degraded providers + DLQ depth + open backlog). Every panel carries
 * loading/empty/failure states; an empty DLQ or zero orphans renders a
 * healthy `EmptyState`, never a hidden panel. Diagnostics queries refetch
 * every 30s (paused when the tab is hidden — see `useAdminQueries`).
 *
 * DLQ controls render only where the backend advertises `actions[]`
 * (`redrive`/`discard`); anything else never becomes a button. Redrive races
 * (409 already redriven) refresh the row + toast without a duplicate submit.
 * Mid-session 403 locks the affected panel + toasts; the session stays
 * intact (no logout, no redirect).
 */

function useForbiddenSectionToast(isForbidden: boolean, section: string): void {
  const { push } = useToast();
  const notifiedRef = useRef(false);
  useEffect(() => {
    if (isForbidden && !notifiedRef.current) {
      notifiedRef.current = true;
      push('error', `${section} is locked: elevated access was revoked. Session otherwise intact.`);
    }
    if (!isForbidden) {
      notifiedRef.current = false;
    }
  }, [isForbidden, section, push]);
}

function SectionForbidden({ testId, title }: { readonly testId: string; readonly title: string }): ReactNode {
    const { t } = useTranslation();
return (
    <div data-testid={testId}>
      <EmptyState title={title} description={t('admin:opsDashboard.you-do-not-have-permission-to')} />
    </div>
  );
}

export function OpsDashboard(): ReactNode {
    const { t } = useTranslation();
const queryClient = useQueryClient();
  const { push } = useToast();
  const locale = useAppStore((s) => s.locale);
  const tenantTimezone = useAppStore((s) => s.tenantTimezone);
  const [dlqPage, setDlqPage] = useState(1);

  const statusQuery = useAdminStatus();
  const queuesQuery = useOpsQueues();
  const dlqQuery = useOpsDlq(dlqPage);
  const leasesQuery = useOpsLeases();
  const orphansQuery = useOpsOrphans();
  const backlogQuery = useOpsBacklog();
  const healthQuery = useProviderHealth();

  const forbidden =
    isAdminForbiddenError(statusQuery.error) ||
    isAdminForbiddenError(queuesQuery.error) ||
    isAdminForbiddenError(dlqQuery.error) ||
    isAdminForbiddenError(leasesQuery.error) ||
    isAdminForbiddenError(orphansQuery.error) ||
    isAdminForbiddenError(backlogQuery.error) ||
    isAdminForbiddenError(healthQuery.error);
  useForbiddenSectionToast(forbidden, 'Ops dashboard');

  const dlqRows = dlqQuery.data !== undefined ? dlqRowsFromSummary(dlqQuery.data) : [];
  const leases = leasesQuery.data ?? [];
  const orphans = orphansQuery.data;
  const backlog = backlogQuery.data;
  const health = healthQuery.data ?? [];
  const failedProviders = health.filter((provider) => provider.status === 'Down' || provider.status === 'Degraded');

  const workers = (() => {
    const byOwner = new Map<string, number>();
    for (const lease of leases) {
      byOwner.set(lease.owner, (byOwner.get(lease.owner) ?? 0) + 1);
    }
    return [...byOwner.entries()].map(([owner, activeJobs]) => ({ owner, activeJobs }));
  })();

  async function redriveWithConflictRefresh(entryId: string, reason: string): Promise<{ actionId: string; timestamp: string; action: string }> {
    try {
      return await redriveDlqEntry(entryId, reason);
    } catch (error) {
      if (isAdminConflictError(error)) {
        // 409 already redriven: refresh the row + toast, never re-submit.
        await invalidateAdminQueries(queryClient);
        push('info', 'Entry was already redriven. The row was refreshed.');
        throw new Error('This entry was already redriven. The row was refreshed; no duplicate was submitted.');
      }
      throw error;
    }
  }

  return (
    <section data-testid="admin-ops" aria-label={t('admin:opsDashboard.operations-dashboard')}>
      <h3>{t('admin:opsDashboard.operations')}</h3>

      <div data-testid="admin-ops-queues">
        <h4>{t('admin:opsDashboard.queues')}</h4>
        {queuesQuery.isPending && queuesQuery.data === undefined ? (
          <div data-testid="admin-ops-queues-loading">
            <Skeleton lines={3} />
          </div>
        ) : queuesQuery.isError && queuesQuery.data === undefined ? (
          isAdminForbiddenError(queuesQuery.error) ? (
            <SectionForbidden testId="admin-ops-queues-forbidden" title={t('admin:opsDashboard.queues-locked')} />
          ) : (
            <div data-testid="admin-ops-queues-error">
              <ErrorState
                title={t('admin:opsDashboard.queues-unavailable')}
                message={queuesQuery.error?.message ?? t('admin:opsDashboard.queue-depths-could-not-be-loaded')}
                correlationId={queuesQuery.error?.correlationId}
                onRetry={() => {
                  void queuesQuery.refetch();
                }}
              />
            </div>
          )
        ) : (queuesQuery.data ?? []).length === 0 ? (
          <div data-testid="admin-ops-queues-empty">
            <EmptyState title={t('admin:opsDashboard.queues-idle')} description={t('admin:opsDashboard.no-pending-messages-across-the-queue')} />
          </div>
        ) : (
          <dl data-testid="admin-ops-queues-list">
            {(queuesQuery.data ?? []).map((queue) => (
              <div key={queue.queue}>
                <dt data-testid={`admin-ops-queue-name-${queue.queue}`}>{queue.queue}</dt>
                <dd data-testid={`admin-ops-queue-depth-${queue.queue}`}>{String(queue.depth)}</dd>
              </div>
            ))}
          </dl>
        )}
      </div>

      <div data-testid="admin-ops-workers">
        <h4>{t('admin:opsDashboard.workers')}</h4>
        {leasesQuery.isPending && leasesQuery.data === undefined ? (
          <div data-testid="admin-ops-workers-loading">
            <Skeleton lines={3} />
          </div>
        ) : leasesQuery.isError && leasesQuery.data === undefined ? (
          isAdminForbiddenError(leasesQuery.error) ? (
            <SectionForbidden testId="admin-ops-workers-forbidden" title={t('admin:opsDashboard.workers-locked')} />
          ) : (
            <div data-testid="admin-ops-workers-error">
              <ErrorState
                title={t('admin:opsDashboard.workers-unavailable')}
                message={leasesQuery.error?.message ?? t('admin:opsDashboard.worker-state-could-not-be-loaded')}
                correlationId={leasesQuery.error?.correlationId}
                onRetry={() => {
                  void leasesQuery.refetch();
                }}
              />
            </div>
          )
        ) : workers.length === 0 ? (
          <div data-testid="admin-ops-workers-empty">
            <EmptyState title={t('admin:opsDashboard.no-active-workers')} description={t('admin:opsDashboard.no-leases-are-held-workers-are')} />
          </div>
        ) : (
          <ul data-testid="admin-ops-workers-list">
            {workers.map((worker) => (
              <li key={worker.owner} data-testid={`admin-ops-worker-${worker.owner}`}>
                <span data-testid={`admin-ops-worker-owner-${worker.owner}`}>{worker.owner}</span>{' '}
                <span data-testid={`admin-ops-worker-jobs-${worker.owner}`}>{String(worker.activeJobs)} {t('admin:opsDashboard.active')}</span>
              </li>
            ))}
          </ul>
        )}
        {statusQuery.data !== undefined ? (
          <p className="dp-muted" data-testid="admin-ops-status">
            {t('admin:opsDashboard.probe')} {statusQuery.data.status}
            {statusQuery.data.time !== '' ? ` at ${formatDate(statusQuery.data.time, { locale, timeZone: tenantTimezone })}` : ''}
          </p>
        ) : null}
      </div>

      <div data-testid="admin-ops-errors">
        <h4>{t('admin:opsDashboard.errors')}</h4>
        {dlqQuery.isPending && dlqQuery.data === undefined ? (
          <div data-testid="admin-ops-errors-loading">
            <Skeleton lines={3} />
          </div>
        ) : dlqQuery.isError && dlqQuery.data === undefined ? (
          isAdminForbiddenError(dlqQuery.error) ? (
            <SectionForbidden testId="admin-ops-errors-forbidden" title={t('admin:opsDashboard.errors-locked')} />
          ) : (
            <div data-testid="admin-ops-errors-error">
              <ErrorState
                title={t('admin:opsDashboard.errors-unavailable')}
                message={dlqQuery.error?.message ?? t('admin:opsDashboard.error-breakdown-could-not-be-loaded')}
                correlationId={dlqQuery.error?.correlationId}
                onRetry={() => {
                  void dlqQuery.refetch();
                }}
              />
            </div>
          )
        ) : dlqRows.length === 0 ? (
          <div data-testid="admin-ops-errors-empty">
            <EmptyState title={t('admin:opsDashboard.no-errors')} description={t('admin:opsDashboard.no-dead-letter-reasons-recorded')} />
          </div>
        ) : (
          <ul data-testid="admin-ops-errors-list">
            {dlqRows.map((row) => (
              <li key={row.id} data-testid={`admin-ops-error-${row.id}`}>
                <span data-testid={`admin-ops-error-code-${row.id}`}>{row.code}</span>{' '}
                <span data-testid={`admin-ops-error-count-${row.id}`}>{String(row.count)}</span>
              </li>
            ))}
          </ul>
        )}
      </div>

      <div data-testid="admin-ops-dlq">
        <h4>{t('admin:opsDashboard.dead-letter-queue')}</h4>
        {dlqQuery.isPending && dlqQuery.data === undefined ? (
          <div data-testid="admin-ops-dlq-loading">
            <Skeleton lines={3} />
          </div>
        ) : dlqQuery.isError && dlqQuery.data === undefined ? (
          isAdminForbiddenError(dlqQuery.error) ? (
            <SectionForbidden testId="admin-ops-dlq-forbidden" title={t('admin:opsDashboard.dlq-locked')} />
          ) : (
            <div data-testid="admin-ops-dlq-error">
              <ErrorState
                title={t('admin:opsDashboard.dlq-unavailable')}
                message={dlqQuery.error?.message ?? t('admin:opsDashboard.dlq-summary-could-not-be-loaded')}
                correlationId={dlqQuery.error?.correlationId}
                onRetry={() => {
                  void dlqQuery.refetch();
                }}
              />
            </div>
          )
        ) : dlqQuery.data !== undefined && dlqQuery.data.depth === 0 ? (
          <div data-testid="admin-ops-dlq-empty">
            <EmptyState title={t('admin:opsDashboard.dlq-healthy')} description={t('admin:opsDashboard.depth-is-zero-nothing-awaits-redrive')} />
          </div>
        ) : (
          <div data-testid="admin-ops-dlq-list">
            <p data-testid="admin-ops-dlq-depth">{t('admin:opsDashboard.depth')} {String(dlqQuery.data?.depth ?? 0)}</p>
            <ul>
              {dlqRows.map((row) => (
                <li key={row.id} data-testid={`admin-ops-dlq-row-${row.id}`}>
                  <span data-testid={`admin-ops-dlq-code-${row.id}`}>{row.code}</span>{' '}
                  <span data-testid={`admin-ops-dlq-count-${row.id}`}>{String(row.count)}</span>
                  {row.actions.includes('redrive') ? (
                    <DestructiveAction
                      action="dlq.redrive"
                      label={t('admin:opsDashboard.redrive-entry')}
                      confirmToken={row.code}
                      testId={`admin-ops-dlq-redrive-${row.id}`}
                      description={t('admin:opsDashboard.requeues-this-dead-letter-entry-for')}
                      onConfirm={(reason) => redriveWithConflictRefresh(row.code, reason)}
                    />
                  ) : null}
                  {row.actions.includes('discard') ? (
                    <DestructiveAction
                      action="dlq.discard"
                      label={t('admin:opsDashboard.discard-entry')}
                      confirmToken={row.code}
                      testId={`admin-ops-dlq-discard-${row.id}`}
                      description={t('admin:opsDashboard.permanently-discards-this-dead-letter-entry')}
                      onConfirm={(reason) => discardDlqEntry(row.code, reason)}
                    />
                  ) : null}
                  {row.actions.length === 0 ? (
                    <span data-testid={`admin-ops-dlq-readonly-${row.id}`} className="dp-muted">
                      {t('admin:opsDashboard.read-only')}
                    </span>
                  ) : null}
                </li>
              ))}
            </ul>
            <div style={{ display: 'flex', gap: 'var(--space-2)', alignItems: 'center' }}>
              <button
                type="button"
                data-testid="admin-ops-dlq-prev"
                className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
                disabled={dlqPage <= 1 || dlqQuery.isFetching}
                onClick={() => {
                  setDlqPage((page) => Math.max(1, page - 1));
                }}
              >
                {t('admin:opsDashboard.previous')}
              </button>
              <p data-testid="admin-ops-dlq-page" className="dp-muted">
                {t('admin:opsDashboard.page')} {String(dlqPage)}
              </p>
              <button
                type="button"
                data-testid="admin-ops-dlq-next"
                className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
                disabled={dlqQuery.isFetching}
                onClick={() => {
                  setDlqPage((page) => page + 1);
                }}
              >
                {t('admin:opsDashboard.next')}
              </button>
            </div>
          </div>
        )}
      </div>

      <div data-testid="admin-ops-leases">
        <h4>{t('admin:opsDashboard.leases')}</h4>
        {leasesQuery.isPending && leasesQuery.data === undefined ? (
          <div data-testid="admin-ops-leases-loading">
            <Skeleton lines={3} />
          </div>
        ) : leasesQuery.isError && leasesQuery.data === undefined ? (
          isAdminForbiddenError(leasesQuery.error) ? (
            <SectionForbidden testId="admin-ops-leases-forbidden" title={t('admin:opsDashboard.leases-locked')} />
          ) : (
            <div data-testid="admin-ops-leases-error">
              <ErrorState
                title={t('admin:opsDashboard.leases-unavailable')}
                message={leasesQuery.error?.message ?? t('admin:opsDashboard.stale-leases-could-not-be-loaded')}
                correlationId={leasesQuery.error?.correlationId}
                onRetry={() => {
                  void leasesQuery.refetch();
                }}
              />
            </div>
          )
        ) : leases.length === 0 ? (
          <div data-testid="admin-ops-leases-empty">
            <EmptyState title={t('admin:opsDashboard.no-stale-leases')} description={t('admin:opsDashboard.all-leases-are-fresh-nothing-is')} />
          </div>
        ) : (
          <ul data-testid="admin-ops-leases-list">
            {leases.map((lease) => (
              <li key={lease.id} data-testid={`admin-ops-lease-${lease.id}`}>
                <span data-testid={`admin-ops-lease-stage-${lease.id}`}>{lease.stageType}</span>{' '}
                <span data-testid={`admin-ops-lease-status-${lease.id}`}>{lease.status}</span>{' '}
                <span data-testid={`admin-ops-lease-owner-${lease.id}`}>{lease.owner}</span>{' '}
                <span data-testid={`admin-ops-lease-age-${lease.id}`}>{t('admin:opsDashboard.age')} {formatLeaseAge(lease.ageMs)}</span>
              </li>
            ))}
          </ul>
        )}
      </div>

      <div data-testid="admin-ops-orphans">
        <h4>{t('admin:opsDashboard.orphans')}</h4>
        {orphansQuery.isPending && orphansQuery.data === undefined ? (
          <div data-testid="admin-ops-orphans-loading">
            <Skeleton lines={3} />
          </div>
        ) : orphansQuery.isError && orphansQuery.data === undefined ? (
          isAdminForbiddenError(orphansQuery.error) ? (
            <SectionForbidden testId="admin-ops-orphans-forbidden" title={t('admin:opsDashboard.orphans-locked')} />
          ) : (
            <div data-testid="admin-ops-orphans-error">
              <ErrorState
                title={t('admin:opsDashboard.orphans-unavailable')}
                message={orphansQuery.error?.message ?? t('admin:opsDashboard.orphan-artifacts-could-not-be-loaded')}
                correlationId={orphansQuery.error?.correlationId}
                onRetry={() => {
                  void orphansQuery.refetch();
                }}
              />
            </div>
          )
        ) : (orphans?.items ?? []).length === 0 ? (
          <div data-testid="admin-ops-orphans-empty">
            <EmptyState title={t('admin:opsDashboard.no-orphans')} description={t('admin:opsDashboard.every-stored-object-has-an-owning')} />
          </div>
        ) : (
          <ul data-testid="admin-ops-orphans-list">
            {(orphans?.items ?? []).map((orphan) => (
              <li key={orphan.id} data-testid={`admin-ops-orphan-${orphan.id}`}>
                <span data-testid={`admin-ops-orphan-format-${orphan.id}`}>{orphan.mediaFormat}</span>{' '}
                <span data-testid={`admin-ops-orphan-hash-${orphan.id}`}>{orphan.contentHashPrefix}</span>
              </li>
            ))}
          </ul>
        )}
      </div>

      <div data-testid="admin-ops-backlog">
        <h4>{t('admin:opsDashboard.review-backlog')}</h4>
        {backlogQuery.isPending && backlogQuery.data === undefined ? (
          <div data-testid="admin-ops-backlog-loading">
            <Skeleton lines={3} />
          </div>
        ) : backlogQuery.isError && backlogQuery.data === undefined ? (
          isAdminForbiddenError(backlogQuery.error) ? (
            <SectionForbidden testId="admin-ops-backlog-forbidden" title={t('admin:opsDashboard.backlog-locked')} />
          ) : (
            <div data-testid="admin-ops-backlog-error">
              <ErrorState
                title={t('admin:opsDashboard.backlog-unavailable')}
                message={backlogQuery.error?.message ?? t('admin:opsDashboard.review-backlog-could-not-be-loaded')}
                correlationId={backlogQuery.error?.correlationId}
                onRetry={() => {
                  void backlogQuery.refetch();
                }}
              />
            </div>
          )
        ) : (backlog?.totalOpen ?? 0) === 0 ? (
          <div data-testid="admin-ops-backlog-empty">
            <EmptyState title={t('admin:opsDashboard.backlog-clear')} description={t('admin:opsDashboard.no-open-reviews-await-attention')} />
          </div>
        ) : (
          <div data-testid="admin-ops-backlog-summary">
            <p data-testid="admin-ops-backlog-total">{t('admin:opsDashboard.open')} {String(backlog?.totalOpen ?? 0)}</p>
            <ul data-testid="admin-ops-backlog-projects">
              {(backlog?.perProject ?? []).map((entry) => (
                <li key={entry.projectId} data-testid={`admin-ops-backlog-project-${entry.projectId}`}>
                  {entry.projectId}: {String(entry.openCount)} {t('admin:opsDashboard.open')}
                </li>
              ))}
            </ul>
          </div>
        )}
      </div>

      <div data-testid="admin-ops-failures">
        <h4>{t('admin:opsDashboard.failures')}</h4>
        {healthQuery.isPending && healthQuery.data === undefined ? (
          <div data-testid="admin-ops-failures-loading">
            <Skeleton lines={2} />
          </div>
        ) : healthQuery.isError && healthQuery.data === undefined ? (
          isAdminForbiddenError(healthQuery.error) ? (
            <SectionForbidden testId="admin-ops-failures-forbidden" title={t('admin:opsDashboard.failures-locked')} />
          ) : (
            <div data-testid="admin-ops-failures-error">
              <ErrorState
                title={t('admin:opsDashboard.failures-unavailable')}
                message={healthQuery.error?.message ?? t('admin:opsDashboard.failure-signals-could-not-be-loaded')}
                correlationId={healthQuery.error?.correlationId}
                onRetry={() => {
                  void healthQuery.refetch();
                }}
              />
            </div>
          )
        ) : failedProviders.length === 0 && (dlqQuery.data?.depth ?? 0) === 0 && (backlog?.totalOpen ?? 0) === 0 ? (
          <div data-testid="admin-ops-failures-empty">
            <EmptyState title={t('admin:opsDashboard.no-failures')} description={t('admin:opsDashboard.providers-healthy-dlq-empty-backlog-clear')} />
          </div>
        ) : (
          <div data-testid="admin-ops-failures-list">
            {failedProviders.length > 0 ? (
              <Alert tone="error" title={t('admin:opsDashboard.provider-failures')}>
                <ul>
                  {failedProviders.map((provider) => (
                    <li key={provider.provider} data-testid={`admin-ops-failure-${provider.provider}`}>
                      {provider.provider}: {provider.status}
                    </li>
                  ))}
                </ul>
              </Alert>
            ) : null}
            {(dlqQuery.data?.depth ?? 0) > 0 ? (
              <p data-testid="admin-ops-failures-dlq">{t('admin:opsDashboard.dlq-depth')} {String(dlqQuery.data?.depth ?? 0)}</p>
            ) : null}
            {(backlog?.totalOpen ?? 0) > 0 ? (
              <p data-testid="admin-ops-failures-backlog">{t('admin:opsDashboard.open-reviews')} {String(backlog?.totalOpen ?? 0)}</p>
            ) : null}
          </div>
        )}
      </div>
    </section>
  );
}
