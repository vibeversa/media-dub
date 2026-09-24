import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { buildLoginPath } from './destination.js';

export interface SessionExpiredDialogProps {
  /** Destination restored after re-login. Defaults to `/dashboard`. */
  readonly destination?: string;
}

/**
 * Expiry notice rendered on the login page while the session status is
 * `expired`. Links back into the login flow with the destination preserved;
 * plain text only, no tokens or URLs from the failed session.
 */
export function SessionExpiredDialog({ destination }: SessionExpiredDialogProps): ReactNode {
  const { t } = useTranslation();
  return (
    <section role="alert" aria-live="assertive" data-testid="session-expired-dialog" className="mb-4">
      <h2 className="text-base font-semibold">{t('auth:expired.title')}</h2>
      <p className="mt-1 text-sm text-slate-600">{t('auth:expired.description')}</p>
      <Link to={buildLoginPath(destination ?? '/dashboard')} className="mt-2 inline-block rounded border px-4 py-2">
        {t('auth:expired.signInAgain')}
      </Link>
    </section>
  );
}
