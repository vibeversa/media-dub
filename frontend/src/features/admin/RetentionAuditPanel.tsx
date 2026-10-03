import { useState } from 'react';
import type { ReactNode } from 'react';
import { Alert } from '../../components/Alert/Alert.js';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { hasAdminPermission } from '../../app/session/permissions.js';
import { formatDate } from '../../i18n/format.js';
import { useAppStore } from '../../stores/index.js';
import { isAdminForbiddenError } from './types.js';
import { useAdminAudit, useAdminRetention } from './useAdminQueries.js';
import { useTranslation } from 'react-i18next';

/**
 * Retention policies + audit-event viewer (Task 036).
 *
 * Retention reads `GET /admin/retention` (conventional; `EmptyState` when
 * unprovisioned). The audit viewer reads `GET /admin/audit-events` with
 * columns `timestamp/actor/action/summary` and the same hidden-advanced rule
 * as Task 035: advanced fields stay behind a per-row expander, and the
 * expander itself renders only for elevated holders — otherwise a forbidden
 * placeholder, never an error. A pagination gap from retention expiry renders
 * the explicit "older events expired per retention policy" marker.
 */
export function RetentionAuditPanel(): ReactNode {
    const { t } = useTranslation();
const locale = useAppStore((s) => s.locale);
  const tenantTimezone = useAppStore((s) => s.tenantTimezone);
  const permissions = useAppStore((s) => s.permissions);
  const canViewAdvanced = hasAdminPermission(permissions);
  const retentionQuery = useAdminRetention();
  const [page, setPage] = useState(1);
  const auditQuery = useAdminAudit(page);
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(() => new Set());

  function toggleRow(id: string): void {
    setExpanded((previous) => {
      const next = new Set(previous);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  }

  const retentionLoading = retentionQuery.isPending && retentionQuery.data === undefined;
  const retentionForbidden = retentionQuery.isError && isAdminForbiddenError(retentionQuery.error) && retentionQuery.data === undefined;
  const retentionFailed = retentionQuery.isError && !isAdminForbiddenError(retentionQuery.error) && retentionQuery.data === undefined;
  const policies = retentionQuery.data?.items ?? [];
  const retentionUnprovisioned = retentionQuery.data?.notProvisioned === true;

  const auditLoading = auditQuery.isPending && auditQuery.data === undefined;
  const auditForbidden = auditQuery.isError && isAdminForbiddenError(auditQuery.error) && auditQuery.data === undefined;
  const auditFailed = auditQuery.isError && !isAdminForbiddenError(auditQuery.error) && auditQuery.data === undefined;
  const events = auditQuery.data?.events ?? [];
  const gapExpired = auditQuery.data?.gapExpired === true;
  const auditUnprovisioned = auditQuery.data?.notProvisioned === true;

  return (
    <section data-testid="admin-retention-audit" aria-label={t('admin:retentionAuditPanel.retention-and-audit')}>
      <h3>{t('admin:retentionAuditPanel.retention-policies')}</h3>
      <div data-testid="admin-retention">
        {retentionLoading ? (
          <div data-testid="admin-retention-loading">
            <Skeleton lines={2} />
          </div>
        ) : retentionForbidden ? (
          <div data-testid="admin-retention-forbidden">
            <EmptyState
              title={t('admin:retentionAuditPanel.retention-unavailable')}
              description={t('admin:retentionAuditPanel.you-do-not-have-permission-to')}
            />
          </div>
        ) : retentionFailed ? (
          <div data-testid="admin-retention-error">
            <ErrorState
              title={t('admin:retentionAuditPanel.retention-unavailable2')}
              message={retentionQuery.error?.message ?? t('admin:retentionAuditPanel.retention-policies-could-not-be-loaded')}
              correlationId={retentionQuery.error?.correlationId}
              onRetry={() => {
                void retentionQuery.refetch();
              }}
            />
          </div>
        ) : policies.length === 0 ? (
          <div data-testid="admin-retention-empty">
            <EmptyState
              title={t('admin:retentionAuditPanel.no-retention-policies')}
              description={retentionUnprovisioned ? 'Retention reads are not provisioned on this backend yet.' : 'No retention policies are configured.'}
            />
          </div>
        ) : (
          <dl data-testid="admin-retention-list">
            {policies.map((policy) => (
              <div key={policy.scope}>
                <dt data-testid={`admin-retention-scope-${policy.scope}`}>{policy.scope}</dt>
                <dd data-testid={`admin-retention-days-${policy.scope}`}>
                  {policy.retentionDays !== undefined ? `${String(policy.retentionDays)} days` : '—'}
                </dd>
              </div>
            ))}
          </dl>
        )}
      </div>

      <h3>{t('admin:retentionAuditPanel.audit-events')}</h3>
      <div data-testid="admin-audit">
        {auditLoading ? (
          <div data-testid="admin-audit-loading">
            <Skeleton lines={5} />
          </div>
        ) : auditForbidden ? (
          <div data-testid="admin-audit-forbidden">
            <EmptyState
              title={t('admin:retentionAuditPanel.audit-unavailable')}
              description={t('admin:retentionAuditPanel.you-do-not-have-permission-to2')}
            />
          </div>
        ) : auditFailed ? (
          <div data-testid="admin-audit-error">
            <ErrorState
              title={t('admin:retentionAuditPanel.audit-unavailable2')}
              message={auditQuery.error?.message ?? t('admin:retentionAuditPanel.audit-events-could-not-be-loaded')}
              correlationId={auditQuery.error?.correlationId}
              onRetry={() => {
                void auditQuery.refetch();
              }}
            />
          </div>
        ) : gapExpired ? (
          <div data-testid="admin-audit-gap">
            <Alert tone="info" title={t('admin:retentionAuditPanel.older-events-expired')}>
              <p data-testid="admin-audit-gap-text">{t('admin:retentionAuditPanel.older-events-expired-per-retention-policy')}</p>
              <button
                type="button"
                data-testid="admin-audit-gap-first"
                className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
                onClick={() => {
                  setPage(1);
                }}
              >
                {t('admin:retentionAuditPanel.back-to-first-page')}
              </button>
            </Alert>
          </div>
        ) : events.length === 0 ? (
          <div data-testid="admin-audit-empty">
            <EmptyState
              title={t('admin:retentionAuditPanel.no-audit-events')}
              description={auditUnprovisioned ? 'Audit reads are not provisioned on this backend yet.' : 'Events appear as admin operations run.'}
            />
          </div>
        ) : (
          <div data-testid="admin-audit-table-wrap">
            <table data-testid="admin-audit-table">
              <thead>
                <tr>
                  <th scope="col">{t('admin:retentionAuditPanel.timestamp')}</th>
                  <th scope="col">{t('admin:retentionAuditPanel.actor')}</th>
                  <th scope="col">{t('admin:retentionAuditPanel.action')}</th>
                  <th scope="col">{t('admin:retentionAuditPanel.summary')}</th>
                  <th scope="col">{t('admin:retentionAuditPanel.details')}</th>
                </tr>
              </thead>
              <tbody>
                {events.map((event) => {
                  const open = expanded.has(event.id);
                  return (
                    <tr key={event.id} data-testid={`admin-audit-row-${event.id}`}>
                      <td data-testid={`admin-audit-timestamp-${event.id}`}>
                        {event.timestamp !== '' ? formatDate(event.timestamp, { locale, timeZone: tenantTimezone }) : '—'}
                      </td>
                      <td data-testid={`admin-audit-actor-${event.id}`}>{event.actor}</td>
                      <td data-testid={`admin-audit-action-${event.id}`}>{event.action}</td>
                      <td data-testid={`admin-audit-summary-${event.id}`}>{event.summary}</td>
                      <td>
                        {event.hasAdvanced ? (
                          canViewAdvanced ? (
                            <>
                              <button
                                type="button"
                                data-testid={`admin-audit-row-${event.id}-toggle`}
                                aria-expanded={open}
                                className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
                                onClick={() => {
                                  toggleRow(event.id);
                                }}
                              >
                                {open ? 'Hide details' : 'Show details'}
                              </button>
                              {open ? (
                                <dl data-testid={`admin-audit-advanced-${event.id}`}>
                                  {Object.entries(event.advanced).map(([key, value]) => (
                                    <div key={key}>
                                      <dt data-testid={`admin-audit-advanced-key-${event.id}-${key}`}>{key}</dt>
                                      <dd data-testid={`admin-audit-advanced-value-${event.id}-${key}`}>{value}</dd>
                                    </div>
                                  ))}
                                </dl>
                              ) : null}
                            </>
                          ) : (
                            <span data-testid={`admin-audit-advanced-forbidden-${event.id}`} className="dp-muted">
                              {t('admin:retentionAuditPanel.advanced-details-hidden')}
                            </span>
                          )
                        ) : (
                          <span data-testid={`admin-audit-no-advanced-${event.id}`} className="dp-muted">
                            —
                          </span>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
            <div style={{ display: 'flex', gap: 'var(--space-2)', alignItems: 'center' }}>
              <button
                type="button"
                data-testid="admin-audit-prev"
                className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
                disabled={page <= 1 || auditQuery.isFetching}
                onClick={() => {
                  setPage((current) => Math.max(1, current - 1));
                }}
              >
                {t('admin:retentionAuditPanel.previous')}
              </button>
              <p data-testid="admin-audit-page" className="dp-muted">
                {t('admin:retentionAuditPanel.page')} {String(page)}
              </p>
              <button
                type="button"
                data-testid="admin-audit-next"
                className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
                disabled={!auditQuery.data?.hasMore || auditQuery.isFetching}
                onClick={() => {
                  setPage((current) => current + 1);
                }}
              >
                {t('admin:retentionAuditPanel.next')}
              </button>
            </div>
          </div>
        )}
      </div>
    </section>
  );
}
