import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';

/** 403 target for `RequireAdmin`. Rendered, never a blank redirect loop. */
export default function ForbiddenPage(): ReactNode {
  const { t } = useTranslation();
  return (
    <section data-testid="page-forbidden">
      <h1 className="text-xl font-semibold">{t('common:forbidden.title')}</h1>
      <p className="mt-2 text-sm text-slate-600">{t('common:forbidden.description')}</p>
      <Link to="/dashboard" className="mt-4 inline-block rounded border px-4 py-2">
        {t('common:forbidden.backToDashboard')}
      </Link>
    </section>
  );
}
