import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Card } from '../../components/Card/Card.js';
import { formatNumber } from '../../i18n/format.js';
import { useAppStore } from '../../stores/index.js';
import type { BacklogSlice } from './api.js';

export interface BacklogCardProps {
  readonly backlog: BacklogSlice | undefined;
  readonly onRetry: () => void;
}

/**
 * Review + run backlog with drill-downs: pending reviews → `/review`,
 * running jobs → `/projects`. Missing section renders a per-card error (R3).
 */
export function BacklogCard({ backlog, onRetry }: BacklogCardProps): ReactNode {
  const { t } = useTranslation();
  const locale = useAppStore((s) => s.locale);
  if (backlog === undefined) {
    return (
      <Card title={t('dashboard:backlog.title')}>
        <div data-testid="dashboard-backlog" role="alert">
          <p>{t('dashboard:sectionError.message')}</p>
          <button type="button" data-testid="dashboard-backlog-retry" onClick={onRetry}>
            {t('common:retry')}
          </button>
        </div>
      </Card>
    );
  }
  return (
    <Card title={t('dashboard:backlog.title')}>
      <div data-testid="dashboard-backlog">
        <dl>
          <div>
            <dt>{t('dashboard:backlog.pendingReviews')}</dt>
            <dd data-testid="dashboard-backlog-reviews">{formatNumber(backlog.pendingReviews, { locale })}</dd>
          </div>
          <div>
            <dt>{t('dashboard:backlog.runningJobs')}</dt>
            <dd data-testid="dashboard-backlog-jobs">{formatNumber(backlog.runningJobs, { locale })}</dd>
          </div>
        </dl>
        <Link to="/review" data-testid="dashboard-backlog-reviews-link">
          {t('dashboard:backlog.viewReviews')}
        </Link>{' '}
        <Link to="/projects" data-testid="dashboard-backlog-projects-link">
          {t('dashboard:backlog.viewProjects')}
        </Link>
      </div>
    </Card>
  );
}
