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
  return (
    <div data-testid={testId}>
      <EmptyState title={title} description="You do not have permission to view this section. Contact your tenant admin for access." />
    </div>
  );
}

export function OpsDashboard(): ReactNode {
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
    <section data-testid="admin-ops" aria-label="Operations dashboard">
      <h3>Operations</h3>

      <div data-testid="admin-ops-queues">
        <h4>Queues</h4>
        {queuesQuery.isPending && queuesQuery.data === undefined ? (
          <div data-testid="admin-ops-queues-loading">
            <Skeleton lines={3} />
          </div>
        ) : queuesQuery.isError && queuesQuery.data === undefined ? (
          isAdminForbiddenError(queuesQuery.error) ? (
            <SectionForbidden testId="admin-ops-queues-forbidden" title="Queues locked" />
          ) : (
            <div data-testid="admin-ops-queues-error">
              <ErrorState
                title="Queues unavailable"
                message={queuesQuery.error?.message ?? 'Queue depths could not be loaded.'}
                correlationId={queuesQuery.error?.correlationId}
                onRetry={() => {
                  void queuesQuery.refetch();
                }}
              />
            </div>
          )
        ) : (queuesQuery.data ?? []).length === 0 ? (
          <div data-testid="admin-ops-queues-empty">
            <EmptyState title="Queues idle" description="No pending messages across the queue taxonomy." />
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
        <h4>Workers</h4>
        {leasesQuery.isPending && leasesQuery.data === undefined ? (
          <div data-testid="admin-ops-workers-loading">
            <Skeleton lines={3} />
          </div>
        ) : leasesQuery.isError && leasesQuery.data === undefined ? (
          isAdminForbiddenError(leasesQuery.error) ? (
            <SectionForbidden testId="admin-ops-workers-forbidden" title="Workers locked" />
          ) : (
            <div data-testid="admin-ops-workers-error">
              <ErrorState
                title="Workers unavailable"
                message={leasesQuery.error?.message ?? 'Worker state could not be loaded.'}
                correlationId={leasesQuery.error?.correlationId}
                onRetry={() => {
                  void leasesQuery.refetch();
                }}
              />
            </div>
          )
        ) : workers.length === 0 ? (
          <div data-testid="admin-ops-workers-empty">
            <EmptyState title="No active workers" description="No leases are held. Workers are idle." />
          </div>
        ) : (
          <ul data-testid="admin-ops-workers-list">
            {workers.map((worker) => (
              <li key={worker.owner} data-testid={`admin-ops-worker-${worker.owner}`}>
                <span data-testid={`admin-ops-worker-owner-${worker.owner}`}>{worker.owner}</span>{' '}
                <span data-testid={`admin-ops-worker-jobs-${worker.owner}`}>{String(worker.activeJobs)} active</span>
              </li>
            ))}
          </ul>
        )}
        {statusQuery.data !== undefined ? (
          <p className="dp-muted" data-testid="admin-ops-status">
            Probe: {statusQuery.data.status}
            {statusQuery.data.time !== '' ? ` at ${formatDate(statusQuery.data.time, { locale, timeZone: tenantTimezone })}` : ''}
          </p>
        ) : null}
      </div>

      <div data-testid="admin-ops-errors">
        <h4>Errors</h4>
        {dlqQuery.isPending && dlqQuery.data === undefined ? (
          <div data-testid="admin-ops-errors-loading">
            <Skeleton lines={3} />
          </div>
        ) : dlqQuery.isError && dlqQuery.data === undefined ? (
          isAdminForbiddenError(dlqQuery.error) ? (
            <SectionForbidden testId="admin-ops-errors-forbidden" title="Errors locked" />
          ) : (
            <div data-testid="admin-ops-errors-error">
              <ErrorState
                title="Errors unavailable"
                message={dlqQuery.error?.message ?? 'Error breakdown could not be loaded.'}
                correlationId={dlqQuery.error?.correlationId}
                onRetry={() => {
                  void dlqQuery.refetch();
                }}
              />
            </div>
          )
        ) : dlqRows.length === 0 ? (
          <div data-testid="admin-ops-errors-empty">
            <EmptyState title="No errors" description="No dead-letter reasons recorded." />
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
        <h4>Dead-letter queue</h4>
        {dlqQuery.isPending && dlqQuery.data === undefined ? (
          <div data-testid="admin-ops-dlq-loading">
            <Skeleton lines={3} />
          </div>
        ) : dlqQuery.isError && dlqQuery.data === undefined ? (
          isAdminForbiddenError(dlqQuery.error) ? (
            <SectionForbidden testId="admin-ops-dlq-forbidden" title="DLQ locked" />
          ) : (
            <div data-testid="admin-ops-dlq-error">
              <ErrorState
                title="DLQ unavailable"
                message={dlqQuery.error?.message ?? 'DLQ summary could not be loaded.'}
                correlationId={dlqQuery.error?.correlationId}
                onRetry={() => {
                  void dlqQuery.refetch();
                }}
              />
            </div>
          )
        ) : dlqQuery.data !== undefined && dlqQuery.data.depth === 0 ? (
          <div data-testid="admin-ops-dlq-empty">
            <EmptyState title="DLQ healthy" description="Depth is zero. Nothing awaits redrive or discard." />
          </div>
        ) : (
          <div data-testid="admin-ops-dlq-list">
            <p data-testid="admin-ops-dlq-depth">Depth: {String(dlqQuery.data?.depth ?? 0)}</p>
            <ul>
              {dlqRows.map((row) => (
                <li key={row.id} data-testid={`admin-ops-dlq-row-${row.id}`}>
                  <span data-testid={`admin-ops-dlq-code-${row.id}`}>{row.code}</span>{' '}
                  <span data-testid={`admin-ops-dlq-count-${row.id}`}>{String(row.count)}</span>
                  {row.actions.includes('redrive') ? (
                    <DestructiveAction
                      action="dlq.redrive"
                      label="Redrive entry"
                      confirmToken={row.code}
                      testId={`admin-ops-dlq-redrive-${row.id}`}
                      description="Requeues this dead-letter entry for processing."
                      onConfirm={(reason) => redriveWithConflictRefresh(row.code, reason)}
                    />
                  ) : null}
                  {row.actions.includes('discard') ? (
                    <DestructiveAction
                      action="dlq.discard"
                      label="Discard entry"
                      confirmToken={row.code}
                      testId={`admin-ops-dlq-discard-${row.id}`}
                      description="Permanently discards this dead-letter entry."
                      onConfirm={(reason) => discardDlqEntry(row.code, reason)}
                    />
                  ) : null}
                  {row.actions.length === 0 ? (
                    <span data-testid={`admin-ops-dlq-readonly-${row.id}`} className="dp-muted">
                      Read-only
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
                Previous
              </button>
              <p data-testid="admin-ops-dlq-page" className="dp-muted">
                Page {String(dlqPage)}
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
                Next
              </button>
            </div>
          </div>
        )}
      </div>

      <div data-testid="admin-ops-leases">
        <h4>Leases</h4>
        {leasesQuery.isPending && leasesQuery.data === undefined ? (
          <div data-testid="admin-ops-leases-loading">
            <Skeleton lines={3} />
          </div>
        ) : leasesQuery.isError && leasesQuery.data === undefined ? (
          isAdminForbiddenError(leasesQuery.error) ? (
            <SectionForbidden testId="admin-ops-leases-forbidden" title="Leases locked" />
          ) : (
            <div data-testid="admin-ops-leases-error">
              <ErrorState
                title="Leases unavailable"
                message={leasesQuery.error?.message ?? 'Stale leases could not be loaded.'}
                correlationId={leasesQuery.error?.correlationId}
                onRetry={() => {
                  void leasesQuery.refetch();
                }}
              />
            </div>
          )
        ) : leases.length === 0 ? (
          <div data-testid="admin-ops-leases-empty">
            <EmptyState title="No stale leases" description="All leases are fresh. Nothing is orphaned." />
          </div>
        ) : (
          <ul data-testid="admin-ops-leases-list">
            {leases.map((lease) => (
              <li key={lease.id} data-testid={`admin-ops-lease-${lease.id}`}>
                <span data-testid={`admin-ops-lease-stage-${lease.id}`}>{lease.stageType}</span>{' '}
                <span data-testid={`admin-ops-lease-status-${lease.id}`}>{lease.status}</span>{' '}
                <span data-testid={`admin-ops-lease-owner-${lease.id}`}>{lease.owner}</span>{' '}
                <span data-testid={`admin-ops-lease-age-${lease.id}`}>age {formatLeaseAge(lease.ageMs)}</span>
              </li>
            ))}
          </ul>
        )}
      </div>

      <div data-testid="admin-ops-orphans">
        <h4>Orphans</h4>
        {orphansQuery.isPending && orphansQuery.data === undefined ? (
          <div data-testid="admin-ops-orphans-loading">
            <Skeleton lines={3} />
          </div>
        ) : orphansQuery.isError && orphansQuery.data === undefined ? (
          isAdminForbiddenError(orphansQuery.error) ? (
            <SectionForbidden testId="admin-ops-orphans-forbidden" title="Orphans locked" />
          ) : (
            <div data-testid="admin-ops-orphans-error">
              <ErrorState
                title="Orphans unavailable"
                message={orphansQuery.error?.message ?? 'Orphan artifacts could not be loaded.'}
                correlationId={orphansQuery.error?.correlationId}
                onRetry={() => {
                  void orphansQuery.refetch();
                }}
              />
            </div>
          )
        ) : (orphans?.items ?? []).length === 0 ? (
          <div data-testid="admin-ops-orphans-empty">
            <EmptyState title="No orphans" description="Every stored object has an owning reference." />
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
        <h4>Review backlog</h4>
        {backlogQuery.isPending && backlogQuery.data === undefined ? (
          <div data-testid="admin-ops-backlog-loading">
            <Skeleton lines={3} />
          </div>
        ) : backlogQuery.isError && backlogQuery.data === undefined ? (
          isAdminForbiddenError(backlogQuery.error) ? (
            <SectionForbidden testId="admin-ops-backlog-forbidden" title="Backlog locked" />
          ) : (
            <div data-testid="admin-ops-backlog-error">
              <ErrorState
                title="Backlog unavailable"
                message={backlogQuery.error?.message ?? 'Review backlog could not be loaded.'}
                correlationId={backlogQuery.error?.correlationId}
                onRetry={() => {
                  void backlogQuery.refetch();
                }}
              />
            </div>
          )
        ) : (backlog?.totalOpen ?? 0) === 0 ? (
          <div data-testid="admin-ops-backlog-empty">
            <EmptyState title="Backlog clear" description="No open reviews await attention." />
          </div>
        ) : (
          <div data-testid="admin-ops-backlog-summary">
            <p data-testid="admin-ops-backlog-total">Open: {String(backlog?.totalOpen ?? 0)}</p>
            <ul data-testid="admin-ops-backlog-projects">
              {(backlog?.perProject ?? []).map((entry) => (
                <li key={entry.projectId} data-testid={`admin-ops-backlog-project-${entry.projectId}`}>
                  {entry.projectId}: {String(entry.openCount)} open
                </li>
              ))}
            </ul>
          </div>
        )}
      </div>

      <div data-testid="admin-ops-failures">
        <h4>Failures</h4>
        {healthQuery.isPending && healthQuery.data === undefined ? (
          <div data-testid="admin-ops-failures-loading">
            <Skeleton lines={2} />
          </div>
        ) : healthQuery.isError && healthQuery.data === undefined ? (
          isAdminForbiddenError(healthQuery.error) ? (
            <SectionForbidden testId="admin-ops-failures-forbidden" title="Failures locked" />
          ) : (
            <div data-testid="admin-ops-failures-error">
              <ErrorState
                title="Failures unavailable"
                message={healthQuery.error?.message ?? 'Failure signals could not be loaded.'}
                correlationId={healthQuery.error?.correlationId}
                onRetry={() => {
                  void healthQuery.refetch();
                }}
              />
            </div>
          )
        ) : failedProviders.length === 0 && (dlqQuery.data?.depth ?? 0) === 0 && (backlog?.totalOpen ?? 0) === 0 ? (
          <div data-testid="admin-ops-failures-empty">
            <EmptyState title="No failures" description="Providers healthy, DLQ empty, backlog clear." />
          </div>
        ) : (
          <div data-testid="admin-ops-failures-list">
            {failedProviders.length > 0 ? (
              <Alert tone="error" title="Provider failures">
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
              <p data-testid="admin-ops-failures-dlq">DLQ depth: {String(dlqQuery.data?.depth ?? 0)}</p>
            ) : null}
            {(backlog?.totalOpen ?? 0) > 0 ? (
              <p data-testid="admin-ops-failures-backlog">Open reviews: {String(backlog?.totalOpen ?? 0)}</p>
            ) : null}
          </div>
        )}
      </div>
    </section>
  );
}
