import type { ReactNode } from 'react';
import { NotificationCenter } from '../../features/notifications/NotificationCenter.js';

/** Notification center page (Task 034): durable inbox at `/notifications`. */
export default function NotificationsPage(): ReactNode {
  return (
    <section data-testid="page-notifications">
      <h1 className="text-xl font-semibold">Notifications</h1>
      <p className="mt-2 text-sm text-slate-600">Activity and alerts for your account.</p>
      <div className="mt-4">
        <NotificationCenter />
      </div>
    </section>
  );
}
