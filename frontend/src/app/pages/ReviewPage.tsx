import type { ReactNode } from 'react';
import { ReviewStudio } from '../../features/review/ReviewStudio.js';
import { useTranslation } from 'react-i18next';

/** Manual review studio (Task 031): queue + single-screen context at `/review`. */
export default function ReviewPage(): ReactNode {
    const { t } = useTranslation();
return (
    <section data-testid="page-review">
      <h1 className="text-xl font-semibold">{t('common:reviewPage.review')}</h1>
      <p className="mt-2 text-sm dp-muted">{t('common:reviewPage.items-awaiting-human-review')}</p>
      <div className="mt-4">
        <ReviewStudio />
      </div>
    </section>
  );
}
