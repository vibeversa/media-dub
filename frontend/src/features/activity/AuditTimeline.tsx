import { useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '../../api/queryKeys/index.js';
import { hasAdminPermission } from '../../app/session/permissions.js';
import { Alert } from '../../components/Alert/Alert.js';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { formatDate } from '../../i18n/format.js';
import { useAppStore } from '../../stores/index.js';
import { ActivityFilters } from './ActivityFilters.js';
import { useActivityFiltersFromUrl } from './useActivityFilters.js';
import { ACTIVITY_PAGE_SIZE, activityActionKey, filterActivityEvents } from './types.js';
import { useActivity } from './useActivity.js';
import { useTranslation } from 'react-i18next';

export interface AuditTimelineProps {
  readonly projectId: string;
}

/**
 * Audit timeline (Task 035A, R1/R2/R5).
 *
 * Columns are `timestamp/actor/action/summary` for every event from
 * `GET /projects/{id}/activity` on the activity factory scope (paginated,
 * `ACTIVITY_PAGE_SIZE`, newest-first server order preserved). Advanced fields
 * (`run/stage/attempt/provider/latency/cost/artifact/correlation`) stay hidden
 * until a per-row expander opens them, and the expander itself renders only
 * for `diagnostics.view` holders (see `hasAdminPermission`); without the
 * permission the advanced section stays hidden with a forbidden placeholder,
 * never an error. The expander never renders reservation ids or
 * provider-internal keys (the parser drops them). Beyond 20 rows the list
 * paginates by page cursor (page never loses the URL filter state), so large
 * histories never trigger a full fetch and the DOM stays bounded to one
 * page — equivalent to windowing at this page size without a virtualization
 * dependency. An empty inbox renders an `EmptyState` with the "events appear
 * as work progresses" copy, never a blank panel. Background refresh /
 * projection lag renders a `partial` notice with refetch, never an error
 * toast.
 */
export function AuditTimeline({ projectId }: AuditTimelineProps): ReactNode {
    const { t } = useTranslation();
const queryClient = useQueryClient();
  const locale = useAppStore((s) => s.locale);
  const tenantTimezone = useAppStore((s) => s.tenantTimezone);
  const permissions = useAppStore((s) => s.permissions);
  const canViewAdvanced = hasAdminPermission(permissions);
  const { filters, setFilters, resetFilters } = useActivityFiltersFromUrl();
  const [searchParams, setSearchParams] = useSearchParams();
  const pageParam = searchParams.get('page');
  const page = pageParam !== null && Number.isFinite(Number(pageParam)) && Number(pageParam) >= 1 ? Math.floor(Number(pageParam)) : 1;
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(() => new Set());

  const listQuery = useActivity(projectId, page, ACTIVITY_PAGE_SIZE);
  const pageData = listQuery.data;
  const rawItems = useMemo(() => pageData?.items ?? [], [pageData]);
  const items = useMemo(() => filterActivityEvents(rawItems, filters), [rawItems, filters]);
  const totalPages = pageData !== undefined ? Math.max(1, Math.ceil(pageData.total / pageData.pageSize)) : 1;
  const showPartial = !listQuery.isPending && listQuery.isFetching && pageData !== undefined && rawItems.length > 0;

  function setPage(next: number): void {
    const params = new URLSearchParams(searchParams.toString());
    if (next <= 1) {
      params.delete('page');
    } else {
      params.set('page', String(next));
    }
    setSearchParams(params, { replace: false });
  }

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

  let body: ReactNode;
  if (projectId === '') {
    body = (
      <div data-testid="activity-needs-project">
        <EmptyState title={t('activity:auditTimeline.select-a-project')} description={t('activity:auditTimeline.enter-a-project-id-to-load')} />
      </div>
    );
  } else if (listQuery.isPending && pageData === undefined) {
    body = (
      <div data-testid="activity-loading">
        <Skeleton lines={6} />
      </div>
    );
  } else if (listQuery.isError && pageData === undefined) {
    body = (
      <div data-testid="activity-error">
        <ErrorState
          title={t('activity:auditTimeline.activity-unavailable')}
          message={listQuery.error?.message ?? t('activity:auditTimeline.activity-could-not-be-loaded-no')}
          correlationId={listQuery.error?.correlationId}
          onRetry={() => {
            void listQuery.refetch();
          }}
        />
      </div>
    );
  } else if (rawItems.length === 0) {
    body = (
      <div data-testid="activity-empty">
        <EmptyState title={t('activity:auditTimeline.no-activity-yet')} description={t('activity:auditTimeline.events-appear-as-work-progresses')} />
      </div>
    );
  } else if (items.length === 0) {
    body = (
      <div data-testid="activity-filtered-empty">
        <EmptyState title={t('activity:auditTimeline.no-matching-events')} description={t('activity:auditTimeline.adjust-the-filters-or-reset-to')} />
      </div>
    );
  } else {
    body = (
      <div data-testid="activity-table-wrap">
        <table data-testid="activity-table">
          <thead>
            <tr>
              <th scope="col">{t('activity:auditTimeline.timestamp')}</th>
              <th scope="col">{t('activity:auditTimeline.actor')}</th>
              <th scope="col">{t('activity:auditTimeline.action')}</th>
              <th scope="col">{t('activity:auditTimeline.summary')}</th>
              <th scope="col">{t('activity:auditTimeline.details')}</th>
            </tr>
          </thead>
          <tbody>
            {items.map((item) => {
              const open = expanded.has(item.id);
              return (
                <tr key={item.id} data-testid={`activity-row-${item.id}`} data-action={activityActionKey(item.action)}>
                  <td data-testid={`activity-timestamp-${item.id}`}>
                    {item.timestamp !== '' ? formatDate(item.timestamp, { locale, timeZone: tenantTimezone }) : '—'}
                  </td>
                  <td data-testid={`activity-actor-${item.id}`}>{item.actor}</td>
                  <td data-testid={`activity-action-${item.id}`}>{item.action}</td>
                  <td data-testid={`activity-summary-${item.id}`}>{item.summary}</td>
                  <td>
                    {item.hasAdvanced ? (
                      canViewAdvanced ? (
                        <>
                          <button
                            type="button"
                            data-testid={`activity-row-${item.id}-toggle`}
                            aria-expanded={open}
                            className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
                            onClick={() => {
                              toggleRow(item.id);
                            }}
                          >
                            {open ? 'Hide details' : 'Show details'}
                          </button>
                          {open ? (
                            <dl data-testid={`activity-advanced-${item.id}`}>
                              {Object.entries(item.advanced).map(([key, value]) => (
                                <div key={key}>
                                  <dt data-testid={`activity-advanced-key-${item.id}-${key}`}>{key}</dt>
                                  <dd data-testid={`activity-advanced-value-${item.id}-${key}`}>{value}</dd>
                                </div>
                              ))}
                            </dl>
                          ) : null}
                        </>
                      ) : (
                        <span data-testid={`activity-advanced-forbidden-${item.id}`} className="dp-muted">
                          {t('activity:auditTimeline.advanced-details-hidden')}
                        </span>
                      )
                    ) : (
                      <span data-testid={`activity-no-advanced-${item.id}`} className="dp-muted">
                        —
                      </span>
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    );
  }

  return (
    <section data-testid="activity-timeline" aria-label={t('activity:auditTimeline.activity-timeline')} data-project={projectId}>
      <ActivityFilters filters={filters} onChange={setFilters} onReset={resetFilters} />
      {showPartial ? (
        <div data-testid="activity-partial">
          <Alert tone="warning" title={t('activity:auditTimeline.activity-may-be-incomplete')}>
            <p>{t('activity:auditTimeline.new-events-are-still-projecting-showing')}</p>
            <button
              type="button"
              data-testid="activity-partial-refresh"
              className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
              onClick={() => {
                void queryClient.invalidateQueries({ queryKey: queryKeys.activity.all(projectId) });
              }}
            >
              {t('activity:auditTimeline.refresh-activity')}
            </button>
          </Alert>
        </div>
      ) : null}
      {listQuery.isError && pageData !== undefined ? (
        <div data-testid="activity-stale">
          <Alert tone="warning" title={t('activity:auditTimeline.activity-refresh-failed')} details={listQuery.error?.correlationId}>
            <p>{t('activity:auditTimeline.showing-the-last-loaded-events-new')}</p>
            <button
              type="button"
              data-testid="activity-refresh"
              className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
              onClick={() => {
                void queryClient.invalidateQueries({ queryKey: queryKeys.activity.all(projectId) });
              }}
            >
              {t('activity:auditTimeline.refresh-activity2')}
            </button>
          </Alert>
        </div>
      ) : null}
      {body}
      {pageData !== undefined && rawItems.length > 0 ? (
        <div style={{ display: 'flex', gap: 'var(--space-2)', alignItems: 'center' }}>
          <button
            type="button"
            data-testid="activity-page-prev"
            className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
            disabled={page <= 1 || listQuery.isFetching}
            onClick={() => {
              setPage(Math.max(1, page - 1));
            }}
          >
            {t('activity:auditTimeline.previous')}
          </button>
          <p data-testid="activity-page-info" className="dp-muted">
            {`Page ${String(pageData.page)} of ${String(totalPages)}`}
          </p>
          <button
            type="button"
            data-testid="activity-page-next"
            className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
            disabled={!pageData.hasMore || listQuery.isFetching}
            onClick={() => {
              setPage(page + 1);
            }}
          >
            {t('activity:auditTimeline.next')}
          </button>
        </div>
      ) : null}
      <div hidden>
        <span data-testid="activity-list-key">{JSON.stringify(queryKeys.activity.list(projectId, { page, pageSize: ACTIVITY_PAGE_SIZE }))}</span>
        <span data-testid="activity-all-key">{JSON.stringify(queryKeys.activity.all(projectId))}</span>
      </div>
    </section>
  );
}
