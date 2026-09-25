import { useState } from 'react';
import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Alert } from '../../components/Alert/Alert.js';
import { formatDate } from '../../i18n/format.js';
import { useAppStore } from '../../stores/index.js';
import { isQuotaBannerDismissed, markQuotaBannerDismissed } from './quotaDismiss.js';
import { iconForQuotaState, isQuotaBlocking, toneForQuotaState } from './types.js';
import type { QuotaState } from './types.js';

export interface QuotaBannerProps {
  readonly state: QuotaState;
  readonly resetsAt?: string;
  readonly remaining?: number;
  /** Allowed actions for valid-actions-only links (Task 021). */
  readonly allowedActions?: readonly string[];
}

/**
 * Quota banner (Task 035).
 *
 * - `exceeded` blocks costly actions: persistent error treatment with an
 *   explanation plus a settings link and a support/runbook hint. Parents
 *   read `isQuotaBlocking(state)` to disable submit buttons (in-flight
 *   costly dialogs disable submit while exceeded).
 * - `near` shows a warning banner dismissible per session (sessionStorage
 *   so a reload within the session stays dismissed, a new session restores
 *   it). `available`/`reserved` render distinctly but never block.
 * - Only valid actions link out: the manage link always targets `/settings`
 *   (no admin-only deep links leak here).
 */
export function QuotaBanner({ state, resetsAt, remaining, allowedActions = [] }: QuotaBannerProps): ReactNode {
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
        <Alert tone={tone} title="Quota available">
          <p>
            <span data-testid="quota-banner-icon" aria-hidden="true">
              {icon}
            </span>{' '}
            <span data-testid="quota-banner-state">available</span>
          </p>
        </Alert>
      </div>
    );
  }

  if (state === 'reserved') {
    return (
      <div data-testid="quota-banner-reserved" data-tone={tone} data-blocked="false">
        <Alert tone={tone} title="Quota reserved">
          <p>
            <span data-testid="quota-banner-icon" aria-hidden="true">
              {icon}
            </span>{' '}
            <span data-testid="quota-banner-state">reserved</span>{' '}
            <span className="dp-muted" data-testid="quota-banner-reserved-note">
              A cost hold is reserved for the active run. Informational only.
            </span>
          </p>
        </Alert>
      </div>
    );
  }

  if (state === 'near') {
    return (
      <div data-testid="quota-banner-near" data-tone={tone} data-blocked="false">
        <Alert tone={tone} title="Quota nearly exhausted">
          <p>
            <span data-testid="quota-banner-icon" aria-hidden="true">
              {icon}
            </span>{' '}
            <span data-testid="quota-banner-state">near</span>
            {remaining !== undefined ? <span data-testid="quota-banner-remaining"> · {String(remaining)} remaining</span> : null}
            {resetsText !== undefined ? <span data-testid="quota-banner-resets"> · Resets {resetsText}</span> : null}
          </p>
          <p>
            <Link data-testid="quota-banner-manage" to="/settings">
              Manage in settings
            </Link>
          </p>
          <button
            type="button"
            data-testid="quota-banner-dismiss"
            className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
            onClick={dismiss}
          >
            Dismiss
          </button>
        </Alert>
      </div>
    );
  }

  return (
    <div data-testid="quota-banner-exceeded" data-tone={tone} data-blocked={blocking ? 'true' : 'false'}>
      <Alert tone={tone} title="Quota exceeded — costly actions paused">
        <p>
          <span data-testid="quota-banner-icon" aria-hidden="true">
            {icon}
          </span>{' '}
          <span data-testid="quota-banner-state">exceeded</span>
        </p>
        <p data-testid="quota-banner-explanation">
          Costly actions (starting runs, requesting exports) are paused until quota resets. Finish reviews or wait for
          the reset. Contact your tenant admin or see the runbook for usage guidance.
        </p>
        {resetsText !== undefined ? <p data-testid="quota-banner-resets">Resets {resetsText}</p> : null}
        <p>
          <Link data-testid="quota-banner-manage" to="/settings">
            Manage in settings
          </Link>
        </p>
        <p className="dp-muted" data-testid="quota-banner-support">
          Support hint: share the page correlation id with your admin. No reservation ids are shown here.
        </p>
      </Alert>
    </div>
  );
}
