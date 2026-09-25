import { useEffect, useRef } from 'react';
import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import { Alert } from '../../components/Alert/Alert.js';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { useToast } from '../../components/Toast/useToast.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { ExportCard } from './ExportCard.js';
import { ExportRow } from './ExportRow.js';
import {
  iconForOutputState,
  labelForOutputState,
  outputStateKey,
  partialExplanationFor,
  partialExplanationForItem,
  patternForOutputState,
} from './types.js';
import type { OutputItemView, OutputView } from './types.js';
import { invalidateOutputs, useExports, useOutputs } from './useOutputs.js';

export interface OutputsPageProps {
  readonly projectId: string;
}

function itemTestId(item: OutputItemView, index: number): string {
  if (item.kind === 'subtitles') {
    return `subtitles-${String(index)}`;
  }
  return item.kind;
}

/**
 * Output + export experience (Task 033).
 *
 * Fed by `useOutputs(projectId)` on `queryKeys.outputs.detail` (readiness
 * aggregate) plus `useExports(projectId)` on `queryKeys.exports.list`
 * (generation rows). Every output item renders its state distinctly —
 * ready / generating / failed / partial / unavailable — with reasons, never
 * a bare disabled button. Partial states quantify availability
 * (`ready/total` plus the QC deep link to Task 032); unavailable states
 * show the backend reason (`NO_RUNS_YET` before any run). Ready output
 * assets link their aggregate signed URL directly (short-lived; refresh
 * refetches once); ready exports download via click-time 302 URLs in
 * `ExportRow` (never pre-fetched, never cached). Export requests flow
 * through `ExportCard` (allowlisted only, 409 collapses to the existing
 * row). Deleted export rows disappear on refetch with a toast. Internal
 * paths never render; errors show the backend `message` only.
 */
export function OutputsPage({ projectId }: OutputsPageProps): ReactNode {
  const { push } = useToast();
  const queryClient = useQueryClient();
  const outputsQuery = useOutputs(projectId);
  const exportsQuery = useExports(projectId);
  const knownExportIds = useRef<readonly string[]>([]);

  const output: OutputView | undefined = outputsQuery.data;
  const exportJobs = exportsQuery.data ?? [];

  useEffect(() => {
    if (exportsQuery.data === undefined) {
      return;
    }
    const current = exportsQuery.data.map((job) => job.id);
    const previous = knownExportIds.current;
    if (previous.length > 0) {
      const removed = previous.filter((id) => !current.includes(id));
      if (removed.length > 0) {
        push('error', 'An export was removed. List refreshed.');
      }
    }
    knownExportIds.current = current;
  }, [exportsQuery.data, push]);

  useEffect(() => {
    knownExportIds.current = [];
  }, [projectId]);

  let outputBody: ReactNode;
  if (projectId === '') {
    outputBody = (
      <div data-testid="outputs-needs-project">
        <EmptyState title="Select a project" description="Enter a project id to load its outputs." />
      </div>
    );
  } else if (outputsQuery.isPending && output === undefined) {
    outputBody = (
      <div data-testid="outputs-loading">
        <Skeleton lines={6} />
      </div>
    );
  } else if (outputsQuery.isError && output === undefined) {
    outputBody = (
      <div data-testid="outputs-error">
        <ErrorState
          title="Outputs unavailable"
          message={outputsQuery.error?.message ?? 'Outputs could not be loaded. No data was changed.'}
          correlationId={outputsQuery.error?.correlationId}
          onRetry={() => {
            void outputsQuery.refetch();
          }}
        />
      </div>
    );
  } else if (output !== undefined) {
    const stateKey = outputStateKey(output.state);
    const icon = iconForOutputState(output.state);
    const pattern = patternForOutputState(output.state);
    const label = labelForOutputState(output.state);
    outputBody = (
      <div data-testid="outputs-detail">
        <section data-testid="outputs-summary" aria-label="Output summary" data-state={output.state} data-pattern={pattern}>
          <h3>Output summary</h3>
          <p data-testid="outputs-state">
            <span data-testid="outputs-state-icon" aria-hidden="true">
              {icon}
            </span>{' '}
            <span data-testid={`outputs-state-${stateKey}`}>{output.state}</span>{' '}
            <span data-testid="outputs-state-label">{label}</span>{' '}
            <span data-testid="outputs-state-pattern">{pattern}</span>
          </p>
          <p data-testid="outputs-completeness" className="dp-muted">
            {`${String(output.completeness.ready)}/${String(output.completeness.total)} segments ready`}
          </p>
          {output.state === 'Generating' && output.progressApproximate !== undefined ? (
            <p data-testid="outputs-progress" className="dp-muted">
              {`${String(output.progressApproximate)}% approximate`}
            </p>
          ) : null}
          {output.state === 'Failed' && output.errorCode !== undefined ? (
            <p data-testid="outputs-error-code">{output.errorCode}</p>
          ) : null}
          {output.state === 'Unavailable' && output.reason !== undefined ? (
            <p data-testid="outputs-reason">{output.reason}</p>
          ) : null}
          {output.state === 'Partial' ? (
            <div data-testid="outputs-partial">
              <p data-testid="outputs-partial-explanation">{partialExplanationFor(output)}</p>
              <Link data-testid="outputs-partial-quality-link" to={`/projects/${projectId}/quality`}>
                See Quality
              </Link>
            </div>
          ) : null}
          {output.warnings.length > 0 ? (
            <ul data-testid="outputs-warnings">
              {output.warnings.map((warning) => (
                <li key={warning} data-testid={`outputs-warning-${warning}`}>
                  {warning}
                </li>
              ))}
            </ul>
          ) : null}
        </section>
        <section data-testid="outputs-items" aria-label="Output items">
          <h3>Output items</h3>
          <ul style={{ listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: 'var(--space-3)' }}>
            {output.items.map((item, index) => {
              const id = itemTestId(item, index);
              const itemKey = outputStateKey(item.state);
              const itemIcon = iconForOutputState(item.state);
              const itemPattern = patternForOutputState(item.state);
              const itemLabel = labelForOutputState(item.state);
              return (
                <li key={`${item.kind}-${String(index)}`} data-testid={`output-item-${id}`} data-kind={item.kind} data-state={item.state} data-pattern={itemPattern}>
                  <h4 data-testid={`output-kind-${id}`}>{item.label}</h4>
                  <p data-testid={`output-state-${id}`} data-pattern={itemPattern}>
                    <span data-testid={`output-state-icon-${id}`} aria-hidden="true">
                      {itemIcon}
                    </span>{' '}
                    <span data-testid={`output-state-label-${id}`}>{item.state}</span>{' '}
                    <span data-testid={`output-state-pattern-${id}`}>{itemPattern}</span>{' '}
                    <span data-testid={`output-state-key-${id}`}>{itemKey}</span>{' '}
                    <span data-testid={`output-state-text-${id}`}>{itemLabel}</span>
                  </p>
                  {item.reason !== undefined ? <p data-testid={`output-reason-${id}`}>{item.reason}</p> : null}
                  {item.missing.length > 0 ? (
                    <p data-testid={`output-missing-${id}`} className="dp-muted">
                      {item.missing.join(', ')}
                    </p>
                  ) : null}
                  {item.detail !== undefined ? <p data-testid={`output-detail-${id}`}>{item.detail}</p> : null}
                  {item.state === 'Partial' ? (
                    <div data-testid={`output-partial-${id}`}>
                      <p data-testid={`output-partial-explanation-${id}`}>{partialExplanationForItem(item, output.completeness)}</p>
                      <Link data-testid={`output-quality-link-${id}`} to={`/projects/${projectId}/quality`}>
                        See Quality
                      </Link>
                    </div>
                  ) : null}
                  {item.state === 'Unavailable' ? (
                    <p data-testid={`output-unavailable-${id}`} className="dp-muted">
                      {item.missing.length > 0 ? `Unavailable: ${item.missing.join(', ')}` : 'Unavailable for this project.'}
                    </p>
                  ) : null}
                  {item.state === 'Failed' ? (
                    <p data-testid={`output-failed-${id}`}>Failed — refresh for the current state.</p>
                  ) : null}
                  {item.state === 'Generating' ? (
                    <p data-testid={`output-generating-${id}`} className="dp-muted">
                      Generating — live updates apply automatically.
                    </p>
                  ) : null}
                  {item.state === 'Ready' && item.downloadUrl !== undefined ? (
                    <a data-testid={`output-download-${id}`} download href={item.downloadUrl}>
                      Download {item.label}
                    </a>
                  ) : null}
                </li>
              );
            })}
          </ul>
        </section>
      </div>
    );
  } else {
    outputBody = (
      <div data-testid="outputs-loading">
        <Skeleton lines={6} />
      </div>
    );
  }

  let exportsBody: ReactNode;
  if (projectId === '') {
    exportsBody = null;
  } else if (exportsQuery.isPending && exportsQuery.data === undefined) {
    exportsBody = (
      <div data-testid="exports-loading">
        <Skeleton lines={4} />
      </div>
    );
  } else if (exportsQuery.isError && exportsQuery.data === undefined) {
    exportsBody = (
      <div data-testid="exports-error">
        <ErrorState
          title="Exports unavailable"
          message={exportsQuery.error?.message ?? 'Exports could not be loaded. No data was changed.'}
          correlationId={exportsQuery.error?.correlationId}
          onRetry={() => {
            void exportsQuery.refetch();
          }}
        />
      </div>
    );
  } else if (exportJobs.length === 0) {
    exportsBody = (
      <div data-testid="exports-empty">
        <EmptyState title="No exports yet" description="Request an export to generate a downloadable file." />
      </div>
    );
  } else {
    exportsBody = (
      <div data-testid="exports-list" data-total={String(exportJobs.length)} data-rendered={String(exportJobs.length)}>
        <ul style={{ listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: 'var(--space-3)' }}>
          {exportJobs.map((job) => (
            <li key={job.id}>
              <ExportRow projectId={projectId} job={job} />
            </li>
          ))}
        </ul>
      </div>
    );
  }

  return (
    <section data-testid="outputs-workspace" aria-label="Outputs and exports" data-project={projectId}>
      {output !== undefined && output.state === 'Failed' ? (
        <div data-testid="outputs-failed-banner">
          <Alert tone="error" title="Output generation failed">
            <p data-testid="outputs-failed-message">{output.errorCode ?? 'The output failed. No file was produced.'}</p>
          </Alert>
        </div>
      ) : null}
      {outputBody}
      {outputsQuery.isError && output !== undefined ? (
        <div data-testid="outputs-stale">
          <Alert tone="warning" title="Output refresh failed" details={outputsQuery.error?.correlationId}>
            <p>Showing the last loaded outputs. New states may be missing.</p>
            <button
              type="button"
              data-testid="outputs-refresh"
              className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
              onClick={() => {
                void invalidateOutputs(queryClient, projectId);
              }}
            >
              Refresh outputs
            </button>
          </Alert>
        </div>
      ) : null}
      <section data-testid="exports-section" aria-label="Export jobs">
        <h3>Exports</h3>
        <ExportCard projectId={projectId} output={output} />
        {exportsBody}
        {exportsQuery.isError && exportsQuery.data !== undefined ? (
          <div data-testid="exports-stale">
            <Alert tone="warning" title="Export refresh failed" details={exportsQuery.error?.correlationId}>
              <p>Showing the last loaded exports.</p>
            </Alert>
          </div>
        ) : null}
      </section>
      <div hidden>
        <span data-testid="outputs-workspace-key">{JSON.stringify(queryKeys.outputs.detail(projectId))}</span>
        <span data-testid="exports-list-key">{JSON.stringify(queryKeys.exports.list(projectId))}</span>
      </div>
    </section>
  );
}
