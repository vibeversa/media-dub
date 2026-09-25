import type { ReactNode } from 'react';
import { SettingsPage as FeatureSettingsPage } from '../../features/settings/SettingsPage.js';

/** Settings route: renders the Task 035 preferences screen (one lazy chunk). */
export default function SettingsPage(): ReactNode {
  return (
    <section data-testid="page-settings">
      <h1 className="text-xl font-semibold">Settings</h1>
      <p className="mt-2 text-sm text-slate-600">Preferences for your account and workspace.</p>
      <div className="mt-4">
        <FeatureSettingsPage />
      </div>
    </section>
  );
}
