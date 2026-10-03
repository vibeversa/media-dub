import { useState } from 'react';
import type { ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Alert } from '../../components/Alert/Alert.js';
import { useToast } from '../../components/Toast/useToast.js';
import { useAppStore } from '../../stores/index.js';
import { canAccessAdmin } from './adminGuard.js';
import { invalidateAdminQueries } from './useAdminQueries.js';
import type { AdminActionReceipt } from './useAdminQueries.js';
import { isAdminForbiddenError, isValidAuditReason, sanitizeReasonText } from './types.js';
import { useTranslation } from 'react-i18next';

export interface DestructiveActionProps {
  /** Stable action id, e.g. `dlq.redrive`. Shown in the dialog + receipt. */
  readonly action: string;
  /** Human label for the trigger button, e.g. `Redrive entry`. */
  readonly label: string;
  /** Exact string the operator must type to confirm (type-to-confirm). */
  readonly confirmToken: string;
  /**
   * Executes the destructive call with the sanitized reason. Must include an
   * audit emission server-side (`auditAction` + reason in the request body —
   * see `useAdminQueries` mutations). Resolves to a receipt on success.
   */
  readonly onConfirm: (reason: string) => Promise<AdminActionReceipt>;
  /** Test id prefix, e.g. `dlq-redrive-abc`. */
  readonly testId: string;
  /** Short plain-text explanation of what the action destroys. */
  readonly description?: string;
}

/**
 * Shared destructive-action gate (Task 036, R4).
 *
 * Every destructive operation passes through: type-to-confirm (exact match on
 * `confirmToken`) + mandatory audit reason (min 10 chars, sanitized before
 * display/transport) + permission re-check at submit time + explicit audit
 * emission in the mutation body. On success the affected admin queries are
 * invalidated and a receipt (action id, timestamp) renders; on 403 a
 * `ForbiddenState` renders without leaking the required role name.
 */
export function DestructiveAction({ action, label, confirmToken, onConfirm, testId, description }: DestructiveActionProps): ReactNode {
    const { t } = useTranslation();
const queryClient = useQueryClient();
  const { push } = useToast();
  const permissions = useAppStore((s) => s.permissions);
  const [open, setOpen] = useState(false);
  const [confirmText, setConfirmText] = useState('');
  const [reason, setReason] = useState('');
  const [pending, setPending] = useState(false);
  const [forbidden, setForbidden] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
  const [receipt, setReceipt] = useState<AdminActionReceipt | null>(null);

  const sanitized = sanitizeReasonText(reason);
  const reasonValid = isValidAuditReason(reason);
  const confirmValid = confirmText === confirmToken;
  const canSubmit = reasonValid && confirmValid && !pending;

  function reset(): void {
    setOpen(false);
    setConfirmText('');
    setReason('');
    setPending(false);
    setFailure(null);
    setForbidden(false);
  }

  async function submit(): Promise<void> {
    if (!canSubmit) {
      return;
    }
    // Permission re-check at submit time: revoked elevation between dialog
    // open and confirm locks the gate without leaking the required role.
    if (!canAccessAdmin(permissions)) {
      setForbidden(true);
      return;
    }
    setPending(true);
    setFailure(null);
    setForbidden(false);
    try {
      const result = await onConfirm(sanitized);
      setReceipt(result);
      await invalidateAdminQueries(queryClient);
      push('success', `${label} completed. Receipt ${result.actionId}.`);
      setOpen(false);
      setConfirmText('');
      setReason('');
    } catch (error) {
      if (isAdminForbiddenError(error)) {
        setForbidden(true);
      } else {
        const message = error instanceof Error && error.message !== '' ? error.message : `${label} failed. No data was changed.`;
        setFailure(message);
      }
    } finally {
      setPending(false);
    }
  }

  return (
    <div data-testid={`${testId}-gate`}>
      <button
        type="button"
        data-testid={`${testId}-open`}
        className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
        onClick={() => {
          setReceipt(null);
          setFailure(null);
          setForbidden(false);
          setOpen(true);
        }}
      >
        {label}
      </button>
      {receipt !== null ? (
        <div data-testid={`${testId}-receipt`}>
          <Alert tone="success" title={t('admin:destructiveAction.completed', { label: label })}>
            <p data-testid={`${testId}-receipt-text`}>
              {t('admin:destructiveAction.action')} {receipt.action} {t('admin:destructiveAction.completed-at')} {receipt.timestamp}.
            </p>
            <p className="dp-muted" data-testid={`${testId}-receipt-id`}>
              {t('admin:destructiveAction.receipt')} {receipt.actionId}
            </p>
          </Alert>
        </div>
      ) : null}
      {open ? (
        <div role="dialog" aria-modal="true" aria-label={t('admin:destructiveAction.confirmation', { label: label })} data-testid={`${testId}-dialog`}>
          <h4>{label}</h4>
          {description !== undefined && description !== '' ? <p className="dp-muted">{description}</p> : null}
          <p className="dp-muted" data-testid={`${testId}-confirm-hint`}>
            {t('admin:destructiveAction.type')} <code>{confirmToken}</code> {t('admin:destructiveAction.to-confirm-this-action-is-audited')}
          </p>
          <label htmlFor={`${testId}-confirm-input`}>{t('admin:destructiveAction.type-to-confirm')}</label>
          <input
            id={`${testId}-confirm-input`}
            data-testid={`${testId}-confirm-input`}
            type="text"
            autoComplete="off"
            value={confirmText}
            onChange={(event) => {
              setConfirmText(event.target.value);
            }}
          />
          <label htmlFor={`${testId}-reason-input`}>{t('admin:destructiveAction.audit-reason-minimum-10-characters')}</label>
          <textarea
            id={`${testId}-reason-input`}
            data-testid={`${testId}-reason-input`}
            rows={3}
            value={reason}
            onChange={(event) => {
              setReason(event.target.value);
            }}
          />
          {!reasonValid && reason !== '' ? (
            <p data-testid={`${testId}-reason-error`} role="alert">
              {t('admin:destructiveAction.enter-at-least-10-characters-explaining')}
            </p>
          ) : null}
          {forbidden ? (
            <div data-testid={`${testId}-forbidden`}>
              <Alert tone="error" title={t('admin:destructiveAction.action-not-permitted')}>
                <p data-testid={`${testId}-forbidden-text`}>
                  {t('admin:destructiveAction.you-do-not-have-permission-for')}
                </p>
              </Alert>
            </div>
          ) : null}
          {failure !== null ? (
            <div data-testid={`${testId}-error`}>
              <Alert tone="error" title={t('admin:destructiveAction.failed', { label: label })}>
                <p>{failure}</p>
              </Alert>
            </div>
          ) : null}
          <div style={{ display: 'flex', gap: 'var(--space-2)' }}>
            <button
              type="button"
              data-testid={`${testId}-confirm`}
              className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
              disabled={!canSubmit}
              onClick={() => {
                void submit();
              }}
            >
              {pending ? 'Working…' : `Confirm ${action}`}
            </button>
            <button
              type="button"
              data-testid={`${testId}-cancel`}
              className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
              disabled={pending}
              onClick={reset}
            >
              {t('admin:destructiveAction.cancel')}
            </button>
          </div>
        </div>
      ) : null}
    </div>
  );
}
