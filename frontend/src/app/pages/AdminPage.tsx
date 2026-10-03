import type { ReactNode } from 'react';
import { AdminPage as FeatureAdminPage } from '../../features/admin/AdminPage.js';
import { useTranslation } from 'react-i18next';

/** Admin route: renders the Task 036 role-gated admin area (one lazy chunk). */
export default function AdminPage(): ReactNode {
    const { t } = useTranslation();
return (
    <section data-testid="page-admin">
      <h1 className="text-xl font-semibold">{t('common:adminPage.admin')}</h1>
      <p className="mt-2 text-sm dp-muted">{t('common:adminPage.usage-quotas-queues-and-provider-health')}</p>
      <div className="mt-4">
        <FeatureAdminPage />
      </div>
    </section>
  );
}
