import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Card } from '../../components/Card/Card.js';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { formatDate } from '../../i18n/format.js';
import { useAppStore } from '../../stores/index.js';
import type { RecentOutputView } from './api.js';

export interface RecentOutputsProps {
  readonly outputs: RecentOutputView[] | undefined;
  readonly onRetry: () => void;
}

/**
 * Recent-output rows with deep links to the owning project workspace
 * (`/projects/:id`; output detail lives there until Tasks 025/030). Empty
 * (but non-tenant-empty) renders an inline empty message, not an error.
 */
export function RecentOutputs({ outputs, onRetry }: RecentOutputsProps): ReactNode {
  const { t } = useTranslation();
  const locale = useAppStore((s) => s.locale);
  const tenantTimezone = useAppStore((s) => s.tenantTimezone);
  if (outputs === undefined) {
    return (
      <Card title={t('dashboard:recentOutputs.title')}>
        <div data-testid="dashboard-recent-outputs" role="alert">
          <p>{t('dashboard:sectionError.message')}</p>
          <button type="button" data-testid="dashboard-outputs-retry" onClick={onRetry}>
            {t('common:retry')}
          </button>
        </div>
      </Card>
    );
  }
  if (outputs.length === 0) {
    return (
      <Card title={t('dashboard:recentOutputs.title')}>
        <div data-testid="dashboard-recent-outputs">
          <EmptyState title={t('dashboard:recentOutputs.title')} description={t('dashboard:recentOutputs.empty')} />
        </div>
      </Card>
    );
  }
  return (
    <Card title={t('dashboard:recentOutputs.title')}>
      <ul data-testid="dashboard-recent-outputs">
        {outputs.map((output) => (
          <li key={output.id} data-testid={`dashboard-output-${output.id}`}>
            <span>
              {output.mediaKind} · {output.container}
            </span>{' '}
            {output.createdAt !== '' ? (
              <time dateTime={output.createdAt}>
                {formatDate(output.createdAt, { locale, timeZone: tenantTimezone })}
              </time>
            ) : null}{' '}
            {output.projectId !== '' ? (
              <Link to={`/projects/${output.projectId}`} data-testid={`dashboard-output-link-${output.id}`}>
                {t('dashboard:recentOutputs.viewProject')}
              </Link>
            ) : (
              <span>{t('dashboard:recentOutputs.unknownProject')}</span>
            )}
          </li>
        ))}
      </ul>
    </Card>
  );
}
