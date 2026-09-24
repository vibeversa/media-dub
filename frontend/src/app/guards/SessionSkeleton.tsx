import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';

/**
 * Shell skeleton rendered while the pre-shell /me resolution (Task 019) is
 * in flight. Text comes from i18n so the loading state is never blank.
 */
export function SessionSkeleton(): ReactNode {
  const { t } = useTranslation();
  return (
    <div data-testid="session-skeleton" className="dp-container py-8">
      <p role="status" aria-live="polite" className="dp-muted text-sm">
        {t('common:loading')}
      </p>
    </div>
  );
}
