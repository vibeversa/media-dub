import type { ReactNode } from 'react';
import { SettingsPage as FeatureSettingsPage } from '../../features/settings/SettingsPage.js';
import { useTranslation } from 'react-i18next';

/** Settings route: renders the Task 035 preferences screen (one lazy chunk). */
export default function SettingsPage(): ReactNode {
    const { t } = useTranslation();
return (
    <section data-testid="page-settings">
      <h1 className="text-xl font-semibold">{t('common:settingsPage.settings')}</h1>
      <p className="mt-2 text-sm dp-muted">{t('common:settingsPage.preferences-for-your-account-and-workspace')}</p>
      <div className="mt-4">
        <FeatureSettingsPage />
      </div>
    </section>
  );
}
