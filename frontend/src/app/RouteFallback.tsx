import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';

/** Suspense fallback rendered while a lazy route chunk loads. */
export function RouteFallback(): ReactNode {
    const { t } = useTranslation();
return (
    <p role="status" aria-live="polite" data-testid="route-fallback" className="py-8 text-center">
      {t('common:routeFallback.loading')}
    </p>
  );
}
