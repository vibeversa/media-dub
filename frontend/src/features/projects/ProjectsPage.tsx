import type { ReactNode } from 'react';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useAppMutation } from '../../api/hooks.js';
import { apiClient } from '../../api/client/index.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { ConfirmDialog } from '../../components/ConfirmDialog/ConfirmDialog.js';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Input } from '../../components/Input/Input.js';
import { Modal } from '../../components/Modal/Modal.js';
import { Pagination } from '../../components/Pagination/Pagination.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { useToast } from '../../components/Toast/useToast.js';
import { queryClient } from '../../app/providers/queryClient.js';
import { useAppStore } from '../../stores/index.js';
import { ProjectFilters } from './ProjectFilters.js';
import { ProjectTable } from './ProjectTable.js';
import { useProjectsQuery } from './useProjectsQuery.js';
import {
  DEFAULT_FILTERS,
  applyClientFilters,
  getProjectDisplayName,
  hasActiveFilters,
  parseFilters,
  serializeFilters,
} from './api.js';
import type { Project, ProjectAction, ProjectFilters as FilterState } from './api.js';

/**
 * Filterable, paginated project list (Task 021), mounted in the Task 018
 * shell at `/projects`.
 *
 * - R1: filters/sort/page live in URL search params (deep-linkable,
 *   back-button safe). Filter edits reset to page 1; page changes use
 *   `preventScrollReset` so scroll is preserved.
 * - R2: pagination/sorting server-side via `useProjectsQuery`; the client
 *   never slices server pages (target-language/date-range refine the
 *   fetched page in memory only).
 * - R3: row actions come strictly from `getAllowedActions`; the server
 *   stays authoritative — 403/409 surfaces as toast + list refresh.
 * - R4: archived rows excluded by default (no `archived` param sent).
 * - Creation belongs to Task 022: the tenant-empty state carries no dead
 *   CTA (a link to a nonexistent wizard would 404).
 */
export function ProjectsPage(): ReactNode {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const permissions = useAppStore((s) => s.permissions);
  const { push } = useToast();
  const [searchParams, setSearchParams] = useSearchParams();
  const filters = useMemo(() => parseFilters(searchParams), [searchParams]);
  const [cancelTarget, setCancelTarget] = useState<Project | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<Project | null>(null);
  const [deleteName, setDeleteName] = useState('');
  const [busy, setBusy] = useState<{ action: ProjectAction; id: string } | null>(null);

  const summary = useProjectsQuery(filters);
  const { data, isError, isFetching, isPending, error, refetch } = summary;
  const rows = useMemo(() => applyClientFilters(data?.items, filters), [data, filters]);
  const total = data?.total ?? 0;
  const showStale = data !== undefined && isFetching;

  function writeFilters(next: FilterState): void {
    setSearchParams(serializeFilters(next), { preventScrollReset: true });
  }

  function updateFilters(patch: Partial<FilterState>): void {
    const resetPage = !('page' in patch);
    writeFilters({ ...filters, ...patch, page: resetPage ? 1 : (patch.page ?? filters.page) });
  }

  function clearFilters(): void {
    writeFilters({ ...DEFAULT_FILTERS });
  }

  function refreshList(): void {
    void queryClient.invalidateQueries({ queryKey: queryKeys.projects.lists() });
  }

  function failFeedback(prefix: string, code: string | undefined, message: string): void {
    if (code === 'PROJECT_NOT_FOUND') {
      push('info', t('projects:toasts.deletedGone'));
    } else {
      push('error', `${prefix} ${message}`);
    }
    refreshList();
  }

  const archiveMutation = useAppMutation({
    mutationFn: (projectId: string, ctx) =>
      apiClient.archiveProject({ path: { projectId } }, { idempotencyKey: ctx.idempotencyKey }),
  });
  const unarchiveMutation = useAppMutation({
    mutationFn: (projectId: string, ctx) =>
      apiClient.unarchiveProject({ path: { projectId } }, { idempotencyKey: ctx.idempotencyKey }),
  });
  const deleteMutation = useAppMutation({
    mutationFn: (projectId: string, ctx) =>
      apiClient.deleteProject({ path: { projectId } }, { idempotencyKey: ctx.idempotencyKey }),
  });
  const cancelMutation = useAppMutation({
    mutationFn: (projectId: string, ctx) =>
      apiClient.cancelActiveProcessingRun({ path: { projectId } }, { idempotencyKey: ctx.idempotencyKey }),
  });
  const retryMutation = useAppMutation({
    mutationFn: (projectId: string, ctx) =>
      apiClient.startProcessing({ path: { projectId } }, undefined, { idempotencyKey: ctx.idempotencyKey }),
  });

  function clearBusy(action: ProjectAction, id: string): void {
    setBusy((prev) => (prev !== null && prev.action === action && prev.id === id ? null : prev));
  }

  function runArchive(project: Project): void {
    setBusy({ action: 'archive', id: project.id });
    archiveMutation.mutate(project.id, {
      onSuccess: () => {
        push('success', t('projects:toasts.archived'));
        refreshList();
      },
      onError: (err) => {
        failFeedback(t('projects:toasts.archived'), err.code, err.message);
      },
      onSettled: () => {
        clearBusy('archive', project.id);
      },
    });
  }

  function runUnarchive(project: Project): void {
    setBusy({ action: 'unarchive', id: project.id });
    unarchiveMutation.mutate(project.id, {
      onSuccess: () => {
        push('success', t('projects:toasts.unarchived'));
        refreshList();
      },
      onError: (err) => {
        failFeedback(t('projects:toasts.unarchived'), err.code, err.message);
      },
      onSettled: () => {
        clearBusy('unarchive', project.id);
      },
    });
  }

  function runDelete(project: Project): void {
    setBusy({ action: 'delete', id: project.id });
    deleteMutation.mutate(project.id, {
      onSuccess: () => {
        push('success', t('projects:toasts.deleted'));
        setDeleteTarget(null);
        setDeleteName('');
        refreshList();
      },
      onError: (err) => {
        failFeedback(t('projects:toasts.deleted'), err.code, err.message);
      },
      onSettled: () => {
        clearBusy('delete', project.id);
      },
    });
  }

  function runCancel(project: Project): void {
    setBusy({ action: 'cancel', id: project.id });
    cancelMutation.mutate(project.id, {
      onSuccess: () => {
        push('success', t('projects:toasts.cancelled'));
        setCancelTarget(null);
        refreshList();
      },
      onError: (err) => {
        failFeedback(t('projects:toasts.cancelled'), err.code, err.message);
      },
      onSettled: () => {
        clearBusy('cancel', project.id);
      },
    });
  }

  function runRetry(project: Project): void {
    setBusy({ action: 'retry', id: project.id });
    retryMutation.mutate(project.id, {
      onSuccess: () => {
        push('success', t('projects:toasts.retried'));
        refreshList();
      },
      onError: (err) => {
        failFeedback(t('projects:toasts.retried'), err.code, err.message);
      },
      onSettled: () => {
        clearBusy('retry', project.id);
      },
    });
  }

  function handleAction(action: ProjectAction, project: Project): void {
    switch (action) {
      case 'open':
        navigate(`/projects/${project.id}`);
        break;
      case 'export':
        navigate(`/projects/${project.id}/exports`);
        break;
      case 'archive':
        runArchive(project);
        break;
      case 'unarchive':
        runUnarchive(project);
        break;
      case 'delete':
        setDeleteTarget(project);
        setDeleteName('');
        break;
      case 'cancel':
        setCancelTarget(project);
        break;
      case 'retry':
        runRetry(project);
        break;
    }
  }

  const deleteDisplayName = deleteTarget === null ? '' : getProjectDisplayName(deleteTarget);
  const deleteReady = deleteTarget !== null && deleteName.trim() === deleteDisplayName;

  let body: ReactNode;
  if (isPending && data === undefined) {
    body = (
      <div data-testid="projects-loading">
        <Skeleton lines={8} />
      </div>
    );
  } else if (isError && data === undefined) {
    body = (
      <div data-testid="projects-error">
        <ErrorState
          title={t('projects:loadError.title')}
          message={error?.message ?? t('projects:loadError.message')}
          correlationId={error?.correlationId}
          onRetry={() => {
            void refetch();
          }}
        />
      </div>
    );
  } else if (rows.length === 0 && hasActiveFilters(filters)) {
    body = (
      <div data-testid="projects-empty-filtered">
        <EmptyState
          title={t('projects:empty.filteredTitle')}
          description={t('projects:empty.filteredDescription')}
          action={
            <button
              type="button"
              data-testid="projects-clear-filters"
              className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
              onClick={clearFilters}
            >
              {t('projects:filters.clear')}
            </button>
          }
        />
      </div>
    );
  } else if (rows.length === 0 && total === 0) {
    body = (
      <div data-testid="projects-empty">
        <EmptyState title={t('projects:empty.tenantTitle')} description={t('projects:empty.tenantDescription')} />
      </div>
    );
  } else {
    body = (
      <div data-testid="projects-grid">
        <ProjectTable
          rows={rows}
          permissions={permissions}
          onOpen={(project) => {
            navigate(`/projects/${project.id}`);
          }}
          onAction={handleAction}
          busyKey={busy === null ? undefined : `${busy.action}:${busy.id}`}
        />
        <div data-testid="projects-pagination">
          <Pagination
            page={filters.page}
            pageSize={data?.pageSize ?? filters.pageSize}
            total={total}
            onPageChange={(page) => {
              updateFilters({ page });
            }}
          />
        </div>
      </div>
    );
  }

  return (
    <section data-testid="page-projects">
      <h1 className="text-xl font-semibold">{t('projects:title')}</h1>
      <p className="mt-2 text-sm text-slate-600">{t('projects:subtitle')}</p>
      {showStale ? (
        <p role="status" data-testid="projects-stale-indicator">
          {t('projects:stale')}
        </p>
      ) : null}
      <ProjectFilters filters={filters} onChange={updateFilters} onClear={clearFilters} />
      {body}
      <ConfirmDialog
        open={cancelTarget !== null}
        title={t('projects:cancelDialog.title')}
        description={t('projects:cancelDialog.description')}
        confirmLabel={t('projects:cancelDialog.confirm')}
        onCancel={() => {
          setCancelTarget(null);
        }}
        onConfirm={() => {
          if (cancelTarget !== null) {
            runCancel(cancelTarget);
          }
        }}
      />
      <Modal
        open={deleteTarget !== null}
        title={t('projects:deleteDialog.title')}
        onClose={() => {
          setDeleteTarget(null);
          setDeleteName('');
        }}
      >
        <div data-testid="projects-delete-dialog">
          <p className="dp-muted">{t('projects:deleteDialog.description')}</p>
          <Input
            label={t('projects:deleteDialog.nameLabel')}
            data-testid="delete-confirm-name"
            value={deleteName}
            autoComplete="off"
            onChange={(e) => {
              setDeleteName(e.target.value);
            }}
          />
          {!deleteReady && deleteName !== '' ? (
            <p className="dp-muted" data-testid="delete-confirm-hint">
              {t('projects:deleteDialog.mismatch')}
            </p>
          ) : null}
          <div
            style={{
              display: 'flex',
              gap: 'var(--space-2)',
              justifyContent: 'flex-end',
              marginBlockStart: 'var(--space-4)',
            }}
          >
            <button
              type="button"
              data-testid="delete-cancel-button"
              className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
              onClick={() => {
                setDeleteTarget(null);
                setDeleteName('');
              }}
            >
              Cancel
            </button>
            <button
              type="button"
              data-testid="delete-confirm-button"
              className="dp-btn dp-btn-danger dp-btn-md dp-focus-ring"
              disabled={!deleteReady}
              onClick={() => {
                if (deleteTarget !== null) {
                  runDelete(deleteTarget);
                }
              }}
            >
              {t('projects:deleteDialog.confirm')}
            </button>
          </div>
        </div>
      </Modal>
    </section>
  );
}
