import { useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '../../api/queryKeys/index.js';
import { apiClient, newIdempotencyKey } from '../../api/client/index.js';
import { useAppMutation } from '../../api/hooks.js';
import { hasAdminPermission } from '../../app/session/permissions.js';
import { Alert } from '../../components/Alert/Alert.js';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { ProgressBar } from '../../components/ProgressBar/ProgressBar.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { StatusBadge } from '../../components/StatusBadge/StatusBadge.js';
import { CostDisplay } from '../../components/product/CostDisplay/CostDisplay.js';
import { useToast } from '../../components/Toast/useToast.js';
import { formatDate, formatNumber } from '../../i18n/format.js';
import { useAppStore } from '../../stores/index.js';
import { useWorkspaceStore } from './workspaceStore.js';
import {
  getWorkspaceActions,
  isTerminalWorkspace,
  isVersionSkewed,
  projectPhaseStates,
  shouldShowProgress,
  shouldShowRecovery,
} from './useWorkspace.js';
import { useWorkspace } from './useWorkspace.js';
import type { WorkspaceActionId, WorkspaceView } from './useWorkspace.js';

export interface WorkspacePageProps {
  readonly projectId: string;
}

function formatBytes(value: number): string {
  if (!Number.isFinite(value) || value <= 0) {
    return '0 B';
  }
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let size = value;
  let unit = 0;
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024;
    unit += 1;
  }
  const rounded = Math.round(size * 10) / 10;
  return `${formatNumber(rounded)} ${units[unit] ?? 'B'}`;
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
 * Project workspace shell (Task 025).
 *
 * Single-aggregate view over `useWorkspace(projectId)`: header, pipeline
 * stepper + stage progress, review/warnings/output main column, and
 * media/config/run/cost/activity secondary column. Children receive slices
 * via props; no panel fetches on its own. Phase projection is display-only
 * (fixed order marked from aggregate phase/stage/run) with a parallel note
 * on the two branch phases. No time predictions are shown; the backend
 * `updatedAt` stamp is the only clock rendered.
 */
export function WorkspacePage({ projectId }: WorkspacePageProps): ReactNode {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { push } = useToast();
  const queryClient = useQueryClient();
  const sessionPermissions = useAppStore((s) => s.permissions);
  const mainWidth = useWorkspaceStore((s) => s.mainWidth);
  const secondaryWidth = useWorkspaceStore((s) => s.secondaryWidth);
  const setSelectedTab = useWorkspaceStore((s) => s.setSelectedTab);
  const workspaceQuery = useWorkspace(projectId);
  const { data, isPending, isError, error, refetch, isFetching } = workspaceQuery;
  const [actionError, setActionError] = useState<string | null>(null);
  const redirectedRef = useRef(false);

  useEffect(() => {
    if (
      isError &&
      (error?.code === 'NOT_FOUND' || error?.code === 'PROJECT_NOT_FOUND') &&
      !redirectedRef.current
    ) {
      redirectedRef.current = true;
      push('error', t('workspace:deletedToast'));
      void navigate('/projects', { replace: true });
    }
  }, [isError, error, navigate, push, t]);

  const cancelMutation = useAppMutation({
    mutationFn: (variables: void, ctx) => {
      void variables;
      const runId = data?.run.id ?? '';
      return apiClient.cancelProcessingRun({ path: { projectId, runId } }, { idempotencyKey: ctx.idempotencyKey });
    },
    idempotencyKey: newIdempotencyKey(),
  });

  const retryMutation = useAppMutation({
    mutationFn: (variables: void, ctx) => {
      void variables;
      const runId = data?.run.id ?? '';
      return apiClient.retryProcessingRun({ path: { projectId, runId } }, { idempotencyKey: ctx.idempotencyKey });
    },
    idempotencyKey: newIdempotencyKey(),
  });

  const deleteMutation = useAppMutation({
    mutationFn: (variables: void) => {
      void variables;
      return apiClient.deleteProject({ path: { projectId } });
    },
    idempotencyKey: newIdempotencyKey(),
  });

  const archiveMutation = useAppMutation({
    mutationFn: (variables: void, ctx) => {
      void variables;
      const archived = data?.project.isArchived === true;
      if (archived) {
        return apiClient.unarchiveProject({ path: { projectId } }, { idempotencyKey: ctx.idempotencyKey });
      }
      return apiClient.archiveProject({ path: { projectId } }, { idempotencyKey: ctx.idempotencyKey });
    },
    idempotencyKey: newIdempotencyKey(),
  });

  if (isPending && data === undefined) {
    return (
      <section data-testid="workspace-page" aria-label={t('workspace:title')}>
        <div data-testid="workspace-loading">
          <Skeleton lines={8} />
        </div>
      </section>
    );
  }

  if ((isError || data === undefined) && data === undefined) {
    if (error?.code === 'NOT_FOUND' || error?.code === 'PROJECT_NOT_FOUND') {
      return (
        <section data-testid="workspace-page" aria-label={t('workspace:title')}>
          <div data-testid="workspace-loading">
            <Skeleton lines={2} />
          </div>
        </section>
      );
    }
    return (
      <section data-testid="workspace-page" aria-label={t('workspace:title')}>
        <div data-testid="workspace-error">
          <ErrorState
            title={t('workspace:loadError.title')}
            message={error?.message ?? t('workspace:loadError.message')}
            correlationId={error?.correlationId}
            onRetry={() => {
              void refetch();
            }}
          />
        </div>
      </section>
    );
  }

  const workspace = data as WorkspaceView;
  const skewed = isVersionSkewed(workspace);
  const showStale = skewed || (isFetching && !isPending);
  const showProgress = shouldShowProgress(workspace);
  const showRecovery = shouldShowRecovery(workspace);
  const terminal = isTerminalWorkspace(workspace);
  const stages = projectPhaseStates(workspace.phase, workspace.stage, workspace.run.status);
  const actions = getWorkspaceActions(workspace.allowedActions, workspace);
  const canViewCost =
    hasAdminPermission(sessionPermissions) ||
    workspace.allowedActions.includes('admin.manage') ||
    workspace.allowedActions.includes('diagnostics.view');
  const archived = workspace.project.isArchived;
  const runId = workspace.run.id;

  function handleAction(action: WorkspaceActionId): void {
    setActionError(null);
    if (action === 'cancel' && runId !== undefined) {
      cancelMutation.mutate(undefined, {
        onSuccess: () => {
          push('success', t('workspace:header.actionsTitle'));
          void queryClient.invalidateQueries({ queryKey: queryKeys.workspace.detail(projectId) });
        },
        onError: (mutationError) => {
          setActionError(
            mutationError.correlationId !== ''
              ? `${mutationError.message} (Ref: ${mutationError.correlationId})`
              : mutationError.message,
          );
        },
      });
      return;
    }
    if (action === 'retry' && runId !== undefined) {
      retryMutation.mutate(undefined, {
        onSuccess: () => {
          push('success', t('workspace:header.actionsTitle'));
          void queryClient.invalidateQueries({ queryKey: queryKeys.workspace.detail(projectId) });
        },
        onError: (mutationError) => {
          setActionError(
            mutationError.correlationId !== ''
              ? `${mutationError.message} (Ref: ${mutationError.correlationId})`
              : mutationError.message,
          );
        },
      });
      return;
    }
    if (action === 'delete') {
      deleteMutation.mutate(undefined, {
        onSuccess: () => {
          push('success', t('workspace:header.actionsTitle'));
          void queryClient.invalidateQueries({ queryKey: queryKeys.projects.lists() });
          void navigate('/projects', { replace: true });
        },
        onError: (mutationError) => {
          setActionError(
            mutationError.correlationId !== ''
              ? `${mutationError.message} (Ref: ${mutationError.correlationId})`
              : mutationError.message,
          );
        },
      });
      return;
    }
    if (action === 'archive') {
      archiveMutation.mutate(undefined, {
        onSuccess: () => {
          push('success', t('workspace:header.actionsTitle'));
          void queryClient.invalidateQueries({ queryKey: queryKeys.workspace.detail(projectId) });
        },
        onError: (mutationError) => {
          setActionError(
            mutationError.correlationId !== ''
              ? `${mutationError.message} (Ref: ${mutationError.correlationId})`
              : mutationError.message,
          );
        },
      });
    }
  }

  return (
    <section data-testid="workspace-page" aria-label={t('workspace:title')}>
      <div data-testid="workspace-header">
        <h1 data-testid="workspace-title">{workspace.project.name}</h1>
        <p className="dp-muted">{t('workspace:subtitle')}</p>
        <div data-testid="workspace-status">
          <StatusBadge status={workspace.project.status} />
        </div>
        {showProgress ? (
          <div data-testid="workspace-progress">
            <span>{t('workspace:header.progressLabel')}</span>{' '}
            <span data-testid="workspace-progress-value">
              {t('workspace:header.progressValue', { percent: String(workspace.progress.percentApproximate) })}
            </span>
            <ProgressBar value={workspace.progress.percentApproximate} label={t('workspace:header.progressLabel')} />
          </div>
        ) : null}
        <div data-testid="workspace-actions" aria-label={t('workspace:header.actionsTitle')}>
          {actions.map((action) => {
            const testid = `workspace-action-${action}`;
            if (action === 'open') {
              return (
                <Link
                  key={action}
                  to={`/projects/${projectId}/media`}
                  data-testid={testid}
                  className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
                  onClick={() => {
                    setSelectedTab('media');
                  }}
                >
                  {t('workspace:actions.open')}
                </Link>
              );
            }
            if (action === 'export') {
              return (
                <Link
                  key={action}
                  to={`/projects/${projectId}/exports`}
                  data-testid={testid}
                  className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
                  onClick={() => {
                    setSelectedTab('exports');
                  }}
                >
                  {t('workspace:actions.export')}
                </Link>
              );
            }
            const label =
              action === 'archive' && archived ? t('workspace:actions.unarchive') : t(`workspace:actions.${action}`);
            const busy =
              (action === 'cancel' && cancelMutation.isPending) ||
              (action === 'retry' && retryMutation.isPending) ||
              (action === 'delete' && deleteMutation.isPending) ||
              (action === 'archive' && archiveMutation.isPending);
            return (
              <button
                key={action}
                type="button"
                data-testid={testid}
                className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
                disabled={busy}
                onClick={() => {
                  handleAction(action);
                }}
              >
                {label}
              </button>
            );
          })}
        </div>
        {actionError !== null ? (
          <div data-testid="workspace-action-error">
            <Alert tone="error" title={t('workspace:loadError.title')} details={actionError} />
          </div>
        ) : null}
      </div>

      {archived ? (
        <div data-testid="workspace-archived">
          <Alert tone="warning" title={t('workspace:archived.title')}>
            <p>{t('workspace:archived.message')}</p>
          </Alert>
        </div>
      ) : null}

      {showStale ? (
        <div data-testid="workspace-stale">
          <Alert tone="warning" title={t('workspace:stale.title')}>
            <p>{t('workspace:stale.message')}</p>
            <button
              type="button"
              data-testid="workspace-refresh"
              className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
              onClick={() => {
                void refetch();
              }}
            >
              {t('workspace:stale.refresh')}
            </button>
          </Alert>
        </div>
      ) : null}

      {terminal ? <span data-testid="workspace-terminal" hidden /> : null}

      {!showProgress ? (
        <div data-testid="workspace-prerun">
          <EmptyState title={t('workspace:prerun.title')} description={t('workspace:prerun.description')} />
        </div>
      ) : (
        <div data-testid="workspace-pipeline">
          <h2>{t('workspace:stepper.title')}</h2>
          <ol data-testid="workspace-stepper">
            {stages.map((stageView) => (
              <li key={stageView.id} data-testid={`workspace-stage-${stageView.id}`}>
                <span data-testid={`workspace-stage-${stageView.id}-state`}>
                  {t(`workspace:stepper.state${capitalize(stageView.state)}`)}
                </span>{' '}
                <span>{stageView.id}</span>
                {stageView.parallel ? (
                  <span data-testid={`workspace-stage-${stageView.id}-parallel`}>
                    {t('workspace:stepper.parallelNote')}
                  </span>
                ) : null}
              </li>
            ))}
          </ol>
          <div data-testid="workspace-stage-progress">
            <ProgressBar value={workspace.progress.percentApproximate} label={t('workspace:stepper.title')} />
            {workspace.progress.currentStage !== undefined ? (
              <p data-testid="workspace-progress-current">{workspace.progress.currentStage}</p>
            ) : null}
            {workspace.progress.updatedAt !== undefined ? (
              <p className="dp-muted" data-testid="workspace-progress-updated">
                {t('workspace:stepper.updated', { at: formatDate(workspace.progress.updatedAt) })}
              </p>
            ) : null}
          </div>
        </div>
      )}

      {showRecovery ? (
        <div data-testid="workspace-recovery">
          <Alert tone="error" title={t('workspace:recovery.title')}>
            <p>{t('workspace:recovery.description', { status: workspace.run.status ?? workspace.phase })}</p>
            {workspace.allowedActions.includes('processing.retry') && runId !== undefined ? (
              <button
                type="button"
                data-testid="workspace-recovery-retry"
                className="dp-btn dp-btn-primary dp-btn-md dp-focus-ring"
                disabled={retryMutation.isPending}
                onClick={() => {
                  handleAction('retry');
                }}
              >
                {t('workspace:recovery.action')}
              </button>
            ) : null}
          </Alert>
        </div>
      ) : null}

      <div style={{ display: 'flex', gap: '1rem', flexWrap: 'wrap' }}>
        <div data-testid="workspace-main" style={{ flexGrow: 1, flexBasis: `${String(mainWidth)}%` }}>
          <section data-testid="workspace-review" aria-label={t('workspace:review.title')}>
            <h3>{t('workspace:review.title')}</h3>
            {workspace.review.pendingCount > 0 ? (
              <p data-testid="workspace-review-count">
                {t('workspace:review.openCount', { count: String(workspace.review.pendingCount) })}
              </p>
            ) : (
              <p data-testid="workspace-review-empty">{t('workspace:review.empty')}</p>
            )}
            <Link
              to="/review"
              data-testid="workspace-review-link"
              onClick={() => {
                setSelectedTab('quality');
              }}
            >
              {t('workspace:review.link')}
            </Link>
          </section>

          <section data-testid="workspace-warnings" aria-label={t('workspace:warnings.title')}>
            <h3>{t('workspace:warnings.title')}</h3>
            {workspace.warnings.length > 0 ? (
              <ul>
                {workspace.warnings.map((warning, index) => (
                  <li key={`${String(index)}:${warning}`} data-testid={`workspace-warning-${String(index)}`}>
                    {warning}
                  </li>
                ))}
              </ul>
            ) : (
              <p data-testid="workspace-warnings-empty">{t('workspace:warnings.empty')}</p>
            )}
            <Link
              to={`/projects/${projectId}/quality`}
              data-testid="workspace-quality-link"
              onClick={() => {
                setSelectedTab('quality');
              }}
            >
              {t('workspace:warnings.link')}
            </Link>
          </section>

          <section data-testid="workspace-output" aria-label={t('workspace:output.title')}>
            <h3>{t('workspace:output.title')}</h3>
            <p data-testid="workspace-output-state">
              {workspace.output.state.toLowerCase() === 'ready'
                ? t('workspace:output.ready')
                : t('workspace:output.pending')}
            </p>
            <p data-testid="workspace-output-completeness">
              {t('workspace:output.completeness', { percent: String(workspace.output.completeness) })}
            </p>
            <Link
              to={`/projects/${projectId}/exports`}
              data-testid="workspace-output-link"
              onClick={() => {
                setSelectedTab('exports');
              }}
            >
              {t('workspace:output.link')}
            </Link>
          </section>
        </div>

        <div data-testid="workspace-secondary" style={{ flexGrow: 1, flexBasis: `${String(secondaryWidth)}%` }}>
          <section data-testid="workspace-media" aria-label={t('workspace:media.title')}>
            <h3>{t('workspace:media.title')}</h3>
            {workspace.media.id === undefined ? (
              <p data-testid="workspace-media-empty">{t('workspace:media.empty')}</p>
            ) : (
              <dl>
                <div>
                  <dt>{t('workspace:media.status')}</dt>
                  <dd data-testid="workspace-media-status">{workspace.media.status}</dd>
                </div>
                <div>
                  <dt>{t('workspace:media.container')}</dt>
                  <dd data-testid="workspace-media-container">{workspace.media.container ?? '—'}</dd>
                </div>
                <div>
                  <dt>{t('workspace:media.size')}</dt>
                  <dd data-testid="workspace-media-size">{formatBytes(workspace.media.sizeBytes)}</dd>
                </div>
                <div>
                  <dt>{t('workspace:media.duration')}</dt>
                  <dd data-testid="workspace-media-duration">{formatDurationMs(workspace.media.durationMs)}</dd>
                </div>
              </dl>
            )}
          </section>

          <section data-testid="workspace-config" aria-label={t('workspace:config.title')}>
            <h3>{t('workspace:config.title')}</h3>
            <dl>
              <div>
                <dt>{t('workspace:config.hash')}</dt>
                <dd data-testid="workspace-config-hash">
                  {workspace.project.configHash ?? t('workspace:config.hashMissing')}
                </dd>
              </div>
              <div>
                <dt>{t('workspace:config.languages')}</dt>
                <dd data-testid="workspace-config-languages">
                  {`${workspace.project.sourceLanguage ?? '—'} → ${workspace.project.targetLanguage ?? '—'}`}
                </dd>
              </div>
              <div>
                <dt>{t('workspace:config.settingsVersion')}</dt>
                <dd data-testid="workspace-config-version">{String(workspace.project.settingsVersion)}</dd>
              </div>
            </dl>
          </section>

          <section data-testid="workspace-run-history" aria-label={t('workspace:run.title')}>
            <h3>{t('workspace:run.title')}</h3>
            {runId === undefined ? (
              <div data-testid="workspace-run-empty">
                <EmptyState
                  title={t('workspace:run.emptyTitle')}
                  description={t('workspace:run.emptyDescription')}
                />
              </div>
            ) : (
              <dl data-testid="workspace-run-row">
                <div>
                  <dt>{t('workspace:run.status')}</dt>
                  <dd data-testid="workspace-run-status">{workspace.run.status ?? '—'}</dd>
                </div>
                <div>
                  <dt>{t('workspace:run.attempt')}</dt>
                  <dd data-testid="workspace-run-attempt">{String(workspace.run.attempt)}</dd>
                </div>
              </dl>
            )}
          </section>

          {canViewCost ? (
            <section data-testid="workspace-cost" aria-label={t('workspace:cost.title')}>
              <h3>{t('workspace:cost.title')}</h3>
              <dl>
                <div>
                  <dt>{t('workspace:cost.runCost')}</dt>
                  <dd data-testid="workspace-cost-run">
                    <CostDisplay amountUsd={workspace.cost.runCost} />
                  </dd>
                </div>
                <div>
                  <dt>{t('workspace:cost.monthToDate')}</dt>
                  <dd data-testid="workspace-cost-month">
                    <CostDisplay amountUsd={workspace.cost.monthToDate} />
                  </dd>
                </div>
              </dl>
            </section>
          ) : null}

          <section data-testid="workspace-activity" aria-label={t('workspace:activity.title')}>
            <h3>{t('workspace:activity.title')}</h3>
            {workspace.activity.length > 0 ? (
              <ul>
                {workspace.activity.map((row) => (
                  <li key={row.id} data-testid={`workspace-activity-row-${row.id}`}>
                    <span>{row.summary}</span>
                    {row.occurredAt !== '' ? (
                      <span className="dp-muted"> · {formatDate(row.occurredAt)}</span>
                    ) : null}
                  </li>
                ))}
              </ul>
            ) : (
              <p data-testid="workspace-activity-empty">{t('workspace:activity.empty')}</p>
            )}
            <Link
              to={`/projects/${projectId}/activity`}
              data-testid="workspace-activity-link"
              onClick={() => {
                setSelectedTab('activity');
              }}
            >
              {t('workspace:activity.viewAll')}
            </Link>
          </section>
        </div>
      </div>
    </section>
  );
}

function capitalize(value: string): string {
  if (value === '') {
    return value;
  }
  return value.slice(0, 1).toUpperCase() + value.slice(1);
}
