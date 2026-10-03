import { useState } from 'react';
import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Alert } from '../../components/Alert/Alert.js';
import { formatDate } from '../../i18n/format.js';
import { useAppStore } from '../../stores/index.js';
import { isQuotaBannerDismissed, markQuotaBannerDismissed } from './quotaDismiss.js';
import { iconForQuotaState, isQuotaBlocking, toneForQuotaState } from './types.js';
import type { QuotaState } from './types.js';
import { useTranslation } from 'react-i18next';

export interface QuotaBannerProps {
  readonly state: QuotaState;
  readonly resetsAt?: string;
  readonly remaining?: number;
  /** Allowed actions for valid-actions-only links (Task 021). */
  readonly allowedActions?: readonly string[];
}

/**
 * Quota banner (Task 035A).
 *
 * - `exceeded` blocks costly actions: persistent error treatment with an
 *   explanation plus a settings link and a support/runbook hint, including
 *   the `contact admin` recovery action per error UX 011.6. Parents read
 *   `isQuotaBlocking(state)` to disable submit buttons.
 * - `near` shows a warning banner dismissible per session (sessionStorage
 *   so a reload within the session stays dismissed, a new session restores
 *   it). `available`/`reserved` render distinctly but never block.
 * - Only valid actions link out: the manage link always targets `/settings`
 *   (no admin-only deep links leak here). Never renders reservation ids.
 */
export function QuotaBanner({ state, resetsAt, remaining, allowedActions = [] }: QuotaBannerProps): ReactNode {
    const { t } = useTranslation();
const locale = useAppStore((s) => s.locale);
  const tenantTimezone = useAppStore((s) => s.tenantTimezone);
  const [dismissed, setDismissed] = useState<boolean>(() => isQuotaBannerDismissed());

  void allowedActions;
  const tone = toneForQuotaState(state);
  const icon = iconForQuotaState(state);
  const blocking = isQuotaBlocking(state);

  if (state === 'near' && dismissed) {
    return null;
  }

  function dismiss(): void {
    markQuotaBannerDismissed();
    setDismissed(true);
  }

  const resetsText = resetsAt !== undefined ? formatDate(resetsAt, { locale, timeZone: tenantTimezone }) : undefined;

  if (state === 'available') {
    return (
      <div data-testid="quota-banner-available" data-tone={tone} data-blocked="false">
        <Alert tone={tone} title={t('cost:quotaBanner.quota-available')}>
          <p>
            <span data-testid="quota-banner-icon" aria-hidden="true">
              {icon}
            </span>{' '}
            <span data-testid="quota-banner-state">{t('cost:quotaBanner.available')}</span>
          </p>
        </Alert>
      </div>
    );
  }

  if (state === 'reserved') {
    return (
      <div data-testid="quota-banner-reserved" data-tone={tone} data-blocked="false">
        <Alert tone={tone} title={t('cost:quotaBanner.quota-reserved')}>
          <p>
            <span data-testid="quota-banner-icon" aria-hidden="true">
              {icon}
            </span>{' '}
            <span data-testid="quota-banner-state">{t('cost:quotaBanner.reserved')}</span>{' '}
            <span className="dp-muted" data-testid="quota-banner-reserved-note">
              {t('cost:quotaBanner.a-cost-hold-is-reserved-for')}
            </span>
          </p>
        </Alert>
      </div>
    );
  }

  if (state === 'near') {
    return (
      <div data-testid="quota-banner-near" data-tone={tone} data-blocked="false">
        <Alert tone={tone} title={t('cost:quotaBanner.quota-nearly-exhausted')}>
          <p>
            <span data-testid="quota-banner-icon" aria-hidden="true">
              {icon}
            </span>{' '}
            <span data-testid="quota-banner-state">{t('cost:quotaBanner.near')}</span>
            {remaining !== undefined ? <span data-testid="quota-banner-remaining"> · {String(remaining)} {t('cost:quotaBanner.remaining')}</span> : null}
            {resetsText !== undefined ? <span data-testid="quota-banner-resets"> {t('cost:quotaBanner.resets')} {resetsText}</span> : null}
          </p>
          <p>
            <Link data-testid="quota-banner-manage" to="/settings">
              {t('cost:quotaBanner.manage-in-settings')}
            </Link>
          </p>
          <button
            type="button"
            data-testid="quota-banner-dismiss"
            className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
            onClick={dismiss}
          >
            {t('cost:quotaBanner.dismiss')}
          </button>
        </Alert>
      </div>
    );
  }

  return (
    <div data-testid="quota-banner-exceeded" data-tone={tone} data-blocked={blocking ? 'true' : 'false'}>
      <Alert tone={tone} title={t('cost:quotaBanner.quota-exceeded-costly-actions-paused')}>
        <p>
          <span data-testid="quota-banner-icon" aria-hidden="true">
            {icon}
          </span>{' '}
          <span data-testid="quota-banner-state">{t('cost:quotaBanner.exceeded')}</span>
        </p>
        <p data-testid="quota-banner-explanation">
          {t('cost:quotaBanner.costly-actions-starting-runs-requesting-exports')}
        </p>
        {resetsText !== undefined ? <p data-testid="quota-banner-resets">{t('cost:quotaBanner.resets2')} {resetsText}</p> : null}
        <p>
          <Link data-testid="quota-banner-manage" to="/settings">
            {t('cost:quotaBanner.manage-in-settings2')}
          </Link>
        </p>
        <p className="dp-muted" data-testid="quota-banner-support">
          {t('cost:quotaBanner.support-hint-share-the-page-correlation')}
        </p>
      </Alert>
    </div>
  );
}
