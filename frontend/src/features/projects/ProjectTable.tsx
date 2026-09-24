import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Badge } from '../../components/Badge/Badge.js';
import { DataGrid } from '../../components/DataGrid/DataGrid.js';
import { StatusBadge } from '../../components/StatusBadge/StatusBadge.js';
import { RelativeTime } from '../../components/product/RelativeTime/RelativeTime.js';
import { formatDate } from '../../i18n/format.js';
import { useAppStore } from '../../stores/index.js';
import { getAllowedActions, getProjectDisplayName } from './api.js';
import type { Project, ProjectAction } from './api.js';

export interface ProjectTableProps {
  readonly rows: readonly Project[];
  readonly permissions: readonly string[];
  /** Row activation (click/Enter) opens the workspace. */
  readonly onOpen: (project: Project) => void;
  readonly onAction: (action: ProjectAction, project: Project) => void;
  /** `action:id` of the in-flight row mutation; that row's buttons disable. */
  readonly busyKey: string | undefined;
}

function actionTestId(action: ProjectAction, projectId: string): string {
  return `project-action-${action}-${projectId}`;
}

/**
 * Project grid (Task 021) on the Task 016 `DataGrid`.
 *
 * Columns render generated `Project` fields only. Duration, approximate
 * progress %, and open review counts are not part of the bundle `Project`
 * schema, so those cells render an em-dash with an explanatory tooltip —
 * honest degradation, never fabricated values (no-fake-precision rule).
 * Row actions come strictly from `getAllowedActions` (R3): disallowed
 * actions are absent from the DOM, never shown-disabled. Action clicks stop
 * propagation so they never trigger row activation.
 */
export function ProjectTable({ rows, permissions, onOpen, onAction, busyKey }: ProjectTableProps): ReactNode {
  const { t } = useTranslation();
  const locale = useAppStore((s) => s.locale);
  const tenantTimezone = useAppStore((s) => s.tenantTimezone);
  const missing = t('projects:table.missingValue');
  return (
    <DataGrid<Project>
      caption={t('projects:table.caption')}
      getRowId={(row) => row.id}
      rows={rows}
      onRowActivate={(row) => {
        if (getAllowedActions(row, permissions).includes('open')) {
          onOpen(row);
        }
      }}
      columns={[
        {
          key: 'name',
          header: t('projects:table.name'),
          render: (row) => {
            const canOpen = getAllowedActions(row, permissions).includes('open');
            const label = getProjectDisplayName(row);
            return canOpen ? (
              <Link to={`/projects/${row.id}`} data-testid={`project-open-${row.id}`}>
                {label}
              </Link>
            ) : (
              <span data-testid={`project-open-${row.id}`}>{label}</span>
            );
          },
        },
        {
          key: 'media',
          header: t('projects:table.media'),
          render: (row) => (
            <span data-testid={`project-media-${row.id}`}>
              <Badge tone="info">{row.sourceLanguage ?? missing}</Badge>{' '}
              <span title={t('projects:table.mediaDurationMissing')}>{missing}</span>
            </span>
          ),
        },
        {
          key: 'targetLanguage',
          header: t('projects:table.targetLanguage'),
          render: (row) => <span data-testid={`project-target-${row.id}`}>{row.targetLanguage ?? missing}</span>,
        },
        {
          key: 'status',
          header: t('projects:table.status'),
          render: (row) => (
            <span data-testid={`project-status-${row.id}`}>
              <StatusBadge status={row.status} />
            </span>
          ),
        },
        {
          key: 'progress',
          header: t('projects:table.progress'),
          headerTitle: t('projects:table.progressSortNote'),
          render: (row) => (
            <span data-testid={`project-progress-${row.id}`} title={t('projects:table.progressMissing')}>
              {missing}
            </span>
          ),
        },
        {
          key: 'review',
          header: t('projects:table.review'),
          render: (row) => (
            <span data-testid={`project-review-${row.id}`} title={t('projects:table.reviewMissing')}>
              {missing}
            </span>
          ),
        },
        {
          key: 'created',
          header: t('projects:table.created'),
          render: (row) => (
            <span data-testid={`project-created-${row.id}`}>
              {row.createdAt === undefined || row.createdAt === ''
                ? missing
                : formatDate(row.createdAt, { locale, timeZone: tenantTimezone })}
            </span>
          ),
        },
        {
          key: 'activity',
          header: t('projects:table.activity'),
          render: (row) => (
            <span data-testid={`project-activity-${row.id}`}>
              {row.updatedAt === undefined || row.updatedAt === '' ? (
                missing
              ) : (
                <RelativeTime value={row.updatedAt} locale={locale} />
              )}
            </span>
          ),
        },
        {
          key: 'actions',
          header: t('projects:table.actions'),
          render: (row) => {
            const allowed = getAllowedActions(row, permissions);
            return (
              <span
                data-testid={`project-actions-${row.id}`}
                onClick={(e) => {
                  e.stopPropagation();
                }}
              >
                {allowed.map((action) => (
                  <button
                    key={action}
                    type="button"
                    data-testid={actionTestId(action, row.id)}
                    className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
                    disabled={busyKey === `${action}:${row.id}`}
                    onClick={() => {
                      onAction(action, row);
                    }}
                  >
                    {t(`projects:actions.${action}`)}
                  </button>
                ))}
              </span>
            );
          },
        },
      ]}
    />
  );
}
