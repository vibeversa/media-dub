import type { ReactNode } from 'react';
import { NotificationCenter } from '../../features/notifications/NotificationCenter.js';
import { useTranslation } from 'react-i18next';

/** Notification center page (Task 034): durable inbox at `/notifications`. */
export default function NotificationsPage(): ReactNode {
    const { t } = useTranslation();
return (
    <section data-testid="page-notifications">
      <h1 className="text-xl font-semibold">{t('common:notificationsPage.notifications')}</h1>
      <p className="mt-2 text-sm dp-muted">{t('common:notificationsPage.activity-and-alerts-for-your-account')}</p>
      <div className="mt-4">
        <NotificationCenter />
      </div>
    </section>
  );
}
