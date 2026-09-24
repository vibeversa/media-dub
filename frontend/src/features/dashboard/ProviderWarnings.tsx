import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Card } from '../../components/Card/Card.js';
import type { WarningView } from './api.js';

export interface ProviderWarningsProps {
  readonly warnings: WarningView[] | undefined;
  readonly onRetry: () => void;
}

/**
 * Tenant warnings (failed/review/near-quota codes from the summary). Empty
 * renders nothing (hidden, not an empty card); a non-array section renders a
 * per-card error with retry (R3). Project-scoped warnings deep-link to the
 * owning project; tenant-wide warnings render as plain text.
 */
export function ProviderWarnings({ warnings, onRetry }: ProviderWarningsProps): ReactNode {
  const { t } = useTranslation();
  if (warnings === undefined) {
    return (
      <Card title={t('dashboard:warnings.title')}>
        <div data-testid="dashboard-warnings" role="alert">
          <p>{t('dashboard:sectionError.message')}</p>
          <button type="button" data-testid="dashboard-warnings-retry" onClick={onRetry}>
            {t('common:retry')}
          </button>
        </div>
      </Card>
    );
  }
  if (warnings.length === 0) {
    return null;
  }
  return (
    <Card title={t('dashboard:warnings.title')}>
      <div data-testid="dashboard-warnings">
        <ul>
          {warnings.map((warning) => (
            <li key={`${warning.code}-${warning.projectId ?? 'tenant'}`} data-testid={`dashboard-warning-${warning.code}`}>
              <span>
                {warning.code}: {warning.message}
              </span>{' '}
              {warning.projectId !== undefined ? (
                <Link
                  to={`/projects/${warning.projectId}`}
                  data-testid={`dashboard-warning-link-${warning.code}`}
                >
                  {t('dashboard:warnings.openProject')}
                </Link>
              ) : null}
            </li>
          ))}
        </ul>
        <Link to="/projects" data-testid="dashboard-warnings-link">
          {t('dashboard:warnings.viewAll')}
        </Link>
      </div>
    </Card>
  );
}
