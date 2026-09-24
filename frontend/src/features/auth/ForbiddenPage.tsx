import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';

/**
 * 403 page (Task 019). Pure render: no data fetching, no retries (a retry
 * loop against a denial is a defect). Reveals nothing about resource
 * existence beyond the denial itself.
 */
export function AuthForbiddenPage(): ReactNode {
  const { t } = useTranslation();
  return (
    <section data-testid="page-forbidden">
      <h1 className="text-xl font-semibold">{t('common:forbidden.title')}</h1>
      <p className="mt-2 text-sm text-slate-600">{t('common:forbidden.description')}</p>
      <p className="mt-2 text-sm text-slate-600">{t('common:forbidden.requestAccess')}</p>
      <Link to="/dashboard" className="mt-4 inline-block rounded border px-4 py-2">
        {t('common:forbidden.backToDashboard')}
      </Link>
    </section>
  );
}
