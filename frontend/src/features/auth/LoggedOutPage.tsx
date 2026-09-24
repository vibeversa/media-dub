import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';

/** Post-logout confirmation (Task 019). Renders outside the auth gate. */
export function LoggedOutPage(): ReactNode {
  const { t } = useTranslation();
  return (
    <section data-testid="page-logged-out">
      <h1 className="text-xl font-semibold">{t('auth:loggedOut.title')}</h1>
      <p className="mt-2 text-sm text-slate-600">{t('auth:loggedOut.description')}</p>
      <Link to="/login" data-testid="logged-out-signin" className="mt-4 inline-block rounded border px-4 py-2">
        {t('auth:loggedOut.signInAgain')}
      </Link>
    </section>
  );
}
