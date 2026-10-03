import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';

/** Unknown route: link home, never a redirect loop. */
export default function NotFoundPage(): ReactNode {
    const { t } = useTranslation();
return (
    <section data-testid="page-not-found" className="py-12 text-center">
      <h1 className="text-xl font-semibold">{t('common:notFoundPage.page-not-found')}</h1>
      <p className="mt-2 text-sm dp-muted">{t('common:notFoundPage.the-page-you-asked-for-does')}</p>
      <Link
        to="/"
        className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring mt-4 inline-block"
      >
        {t('common:notFoundPage.go-home')}
      </Link>
    </section>
  );
}
