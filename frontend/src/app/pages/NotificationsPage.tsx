import type { ReactNode } from 'react';

/** Notification center. Feature content lands in later tasks. */
export default function NotificationsPage(): ReactNode {
  return (
    <section data-testid="page-notifications">
      <h1 className="text-xl font-semibold">Notifications</h1>
      <p className="mt-2 text-sm text-slate-600">Activity and alerts for your account.</p>
    </section>
  );
}
