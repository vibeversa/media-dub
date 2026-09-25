import { useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Alert } from '../../components/Alert/Alert.js';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { QualityIssue } from './QualityIssue.js';
import { QualitySummary } from './QualitySummary.js';
import {
  EMPTY_QUALITY_FILTERS,
  QUALITY_RENDER_LIMIT,
  filterQualityIssues,
  groupQualityIssues,
  hasActiveQualityFilters,
  qualityFiltersFromSearchParams,
  qualityFiltersToSearchParams,
} from './types.js';
import type { QualityFilters } from './types.js';
import { useQuality } from './useQuality.js';

export interface QualityWorkspaceProps {
  readonly projectId: string;
}

/**
 * QC workspace (Task 032).
 *
 * Composes `QualitySummary` (five-state counts matching the issue list) plus
 * the evidence-backed issue list. Blocked issues are pinned to the top with
 * a prominent banner + count above all other content. Filters (severity /
 * status / scope) and the group toggle (by segment / by code) live in URL
 * search params (shareable links; signed URLs never enter the URL). Empty
 * states distinguish "no issues" (pass celebration) from "filters exclude
 * everything" (clear action). Pending runs show an in-progress skeleton with
 * live updates via the Task 026 registry, never zeros.
 */
export function QualityWorkspace({ projectId }: QualityWorkspaceProps): ReactNode {
  const [searchParams, setSearchParams] = useSearchParams();
  const urlFilters = useMemo(() => qualityFiltersFromSearchParams(searchParams), [searchParams]);
  const [draft, setDraft] = useState<QualityFilters>(urlFilters);
  const [renderLimit, setRenderLimit] = useState(QUALITY_RENDER_LIMIT);

  useEffect(() => {
    setDraft(urlFilters);
  }, [urlFilters]);

  useEffect(() => {
    setRenderLimit(QUALITY_RENDER_LIMIT);
  }, [projectId, searchParams]);

  const qualityQuery = useQuality(projectId);
  const issues = useMemo(() => qualityQuery.data?.issues ?? [], [qualityQuery.data]);
  const summary = qualityQuery.data?.summary;
  const isQcPending = qualityQuery.data?.isQcPending === true;

  const filtered = useMemo(() => filterQualityIssues(issues, urlFilters), [issues, urlFilters]);
  const groups = useMemo(() => groupQualityIssues(filtered, urlFilters.group), [filtered, urlFilters]);
  const blockedTotal = useMemo(() => issues.filter((issue) => issue.status === 'Blocked').length, [issues]);
  const visibleGroups = useMemo(() => {
    let remaining = renderLimit;
    const out: typeof groups = [];
    for (const group of groups) {
      if (remaining <= 0) {
        break;
      }
      const slice = group.issues.slice(0, remaining);
      remaining -= slice.length;
      out.push({ ...group, issues: slice });
    }
    return out;
  }, [groups, renderLimit]);
  const visibleCount = visibleGroups.reduce((sum, group) => sum + group.issues.length, 0);

  function writeFilters(next: QualityFilters): void {
    setRenderLimit(QUALITY_RENDER_LIMIT);
    setSearchParams(qualityFiltersToSearchParams(next), { preventScrollReset: true });
  }

  function applyDraft(): void {
    writeFilters(draft);
  }

  function clearFilters(): void {
    const cleared: QualityFilters = { ...EMPTY_QUALITY_FILTERS, group: draft.group };
    setDraft(cleared);
    writeFilters(cleared);
  }

  let body: ReactNode;
  if (projectId === '') {
    body = (
      <div data-testid="quality-needs-project">
        <EmptyState title="Select a project" description="Enter a project id to load its quality checks." />
      </div>
    );
  } else if (qualityQuery.isPending && qualityQuery.data === undefined) {
    body = (
      <div data-testid="quality-loading">
        <Skeleton lines={6} />
      </div>
    );
  } else if (qualityQuery.isError && qualityQuery.data === undefined) {
    body = (
      <div data-testid="quality-error">
        <ErrorState
          title="Quality checks unavailable"
          message={qualityQuery.error?.message ?? 'Quality checks could not be loaded. No data was changed.'}
          correlationId={qualityQuery.error?.correlationId}
          onRetry={() => {
            void qualityQuery.refetch();
          }}
        />
      </div>
    );
  } else if (isQcPending) {
    body = (
      <div data-testid="quality-pending">
        <Skeleton lines={6} />
        <p className="dp-muted">Quality checks are still running — live updates apply automatically.</p>
      </div>
    );
  } else if (issues.length === 0) {
    body = (
      <div data-testid="quality-empty-passed">
        <EmptyState
          title="Quality passed"
          description="No quality issues for this project. All segments are clean."
        />
      </div>
    );
  } else if (filtered.length === 0 && hasActiveQualityFilters(urlFilters)) {
    body = (
      <div data-testid="quality-empty-filtered">
        <EmptyState
          title="No issues match these filters"
          description="Filters exclude every quality issue. Clear them to see the full list."
          action={
            <button
              type="button"
              data-testid="quality-clear-filters"
              className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
              onClick={clearFilters}
            >
              Clear filters
            </button>
          }
        />
      </div>
    );
  } else {
    body = (
      <div data-testid="quality-list" data-total={String(filtered.length)} data-rendered={String(visibleCount)}>
        {visibleGroups.map((group) => (
          <section
            key={group.key}
            data-testid={`quality-group-${urlFilters.group}-${group.key}`}
            aria-label={group.label}
          >
            <h4 data-testid={`quality-group-label-${group.key}`}>{group.label}</h4>
            <ul style={{ listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: 'var(--space-3)' }}>
              {group.issues.map((issue) => (
                <li key={issue.id}>
                  <QualityIssue projectId={projectId} issue={issue} />
                </li>
              ))}
            </ul>
          </section>
        ))}
        {visibleCount < filtered.length ? (
          <button
            type="button"
            data-testid="quality-more"
            className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
            onClick={() => {
              setRenderLimit((prev) => prev + QUALITY_RENDER_LIMIT);
            }}
          >
            {`Show more (${String(filtered.length - visibleCount)} remaining)`}
          </button>
        ) : null}
      </div>
    );
  }

  return (
    <section data-testid="quality-workspace" aria-label="Quality control" data-project={projectId}>
      {blockedTotal > 0 ? (
        <div data-testid="quality-blocked-banner">
          <Alert tone="error" title="Blocking quality issues need attention">
            <p data-testid="quality-blocked-count">
              <span aria-hidden="true">■</span> blocked · pattern hatched-block — {String(blockedTotal)} blocking
              issue(s) pinned above all other content.
            </p>
          </Alert>
        </div>
      ) : null}

      <QualitySummary
        projectId={projectId}
        summary={summary}
        isPending={qualityQuery.isPending && qualityQuery.data === undefined}
        isQcPending={isQcPending}
      />

      <form
        data-testid="quality-filters"
        onSubmit={(event) => {
          event.preventDefault();
          applyDraft();
        }}
      >
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 'var(--space-2)' }}>
          <label>
            Severity
            <input
              data-testid="quality-filter-severity"
              value={draft.severity}
              autoComplete="off"
              placeholder="e.g. Blocking"
              onChange={(event) => {
                const next = event.target.value;
                setDraft((prev) => ({ ...prev, severity: next }));
              }}
            />
          </label>
          <label>
            Status
            <input
              data-testid="quality-filter-status"
              value={draft.status}
              autoComplete="off"
              placeholder="e.g. Blocked"
              onChange={(event) => {
                const next = event.target.value;
                setDraft((prev) => ({ ...prev, status: next }));
              }}
            />
          </label>
          <label>
            Scope
            <input
              data-testid="quality-filter-scope"
              value={draft.scope}
              autoComplete="off"
              placeholder="e.g. segment"
              onChange={(event) => {
                const next = event.target.value;
                setDraft((prev) => ({ ...prev, scope: next }));
              }}
            />
          </label>
          <label>
            Group by
            <select
              data-testid="quality-group"
              value={draft.group}
              onChange={(event) => {
                const next = event.target.value === 'code' ? 'code' : 'segment';
                setDraft((prev) => ({ ...prev, group: next }));
                writeFilters({ ...draft, group: next });
              }}
            >
              <option value="segment">By segment</option>
              <option value="code">By code</option>
            </select>
          </label>
          <button
            type="submit"
            data-testid="quality-apply-filters"
            className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
          >
            Apply filters
          </button>
          <button
            type="button"
            data-testid="quality-clear-filters-top"
            className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
            onClick={clearFilters}
          >
            Clear filters
          </button>
        </div>
      </form>

      {qualityQuery.isError && qualityQuery.data !== undefined ? (
        <div data-testid="quality-stale">
          <Alert tone="warning" title="Quality refresh failed" details={qualityQuery.error?.correlationId}>
            <p>Showing the last loaded checks. New flags may be missing.</p>
          </Alert>
        </div>
      ) : null}

      {body}

      <div hidden>
        <span data-testid="quality-workspace-key">{JSON.stringify(queryKeys.quality.detail(projectId))}</span>
      </div>
    </section>
  );
}
