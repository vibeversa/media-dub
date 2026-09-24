import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Card } from '../../components/Card/Card.js';
import { formatNumber } from '../../i18n/format.js';
import { useAppStore } from '../../stores/index.js';
import type { CountsSlice } from './api.js';

export interface StatCardProps {
  readonly counts: CountsSlice | undefined;
  readonly onRetry: () => void;
}

/**
 * Project-count card. Drill-down links to the filtered project list
 * (`/projects`; Task 021 owns list filters). A missing section renders a
 * per-card error with retry (R3) — never blanks the page.
 */
export function StatCard({ counts, onRetry }: StatCardProps): ReactNode {
  const { t } = useTranslation();
  const locale = useAppStore((s) => s.locale);
  if (counts === undefined) {
    return (
      <Card title={t('dashboard:stats.title')}>
        <div data-testid="dashboard-stat-card" role="alert">
          <p>{t('dashboard:sectionError.message')}</p>
          <button type="button" data-testid="dashboard-stat-retry" onClick={onRetry}>
            {t('common:retry')}
          </button>
        </div>
      </Card>
    );
  }
  return (
    <Card title={t('dashboard:stats.title')}>
      <div data-testid="dashboard-stat-card">
        <dl>
          <div>
            <dt>{t('dashboard:stats.total')}</dt>
            <dd data-testid="dashboard-stat-total">{formatNumber(counts.total, { locale })}</dd>
          </div>
          <div>
            <dt>{t('dashboard:stats.active')}</dt>
            <dd data-testid="dashboard-stat-active">{formatNumber(counts.active, { locale })}</dd>
          </div>
          <div>
            <dt>{t('dashboard:stats.archived')}</dt>
            <dd data-testid="dashboard-stat-archived">{formatNumber(counts.archived, { locale })}</dd>
          </div>
        </dl>
        <Link to="/projects" data-testid="dashboard-stat-link">
          {t('dashboard:stats.viewAll')}
        </Link>
      </div>
    </Card>
  );
}
