import type { ReactNode } from 'react';

/** User and workspace settings. Feature content lands in later tasks. */
export default function SettingsPage(): ReactNode {
  return (
    <section data-testid="page-settings">
      <h1 className="text-xl font-semibold">Settings</h1>
      <p className="mt-2 text-sm text-slate-600">Preferences for your account and workspace.</p>
    </section>
  );
}
