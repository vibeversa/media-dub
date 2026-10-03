import type { ReactNode } from 'react';
import { Alert } from '../../components/Alert/Alert.js';
import { Badge } from '../../components/Badge/Badge.js';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { useTranslation } from 'react-i18next';

/**
 * Operator-only enrichment states (Task 044, §19.3).
 *
 * These are the ADMIN-side counterparts of the user-facing enrichment states in
 * `features/enrichment/EnrichmentStates.tsx`, and they are separate files on
 * purpose rather than a shared one for a structural reason: a shared module
 * would put enrichment code in the admin route chunk, which is the thing the
 * R6 import gate exists to keep clean. The two sets differ in copy and in
 * audience anyway — an operator reads "the deployment has not provisioned a
 * local-inference health endpoint", a project member reads "lip sync is not
 * available here" — so sharing would have bought nothing.
 *
 * The four meanings are kept distinct for the same reason as the user-facing
 * set: an operator panel that collapses "not provisioned", "denied" and
 * "broken" into one red box cannot be triaged, and a responder who reads
 * "unhealthy" when the truth is "unreachable" acts on the wrong runbook.
 */

/** Flag on, backend not provisioned in this deployment. Not an error. */
export function LocalGpuNotAvailableState(): ReactNode {
    const { t } = useTranslation();
return (
    <div data-testid="admin-local-gpu-not-available">
      <EmptyState
        title={t('admin:localGpuStates.local-gpu-health-not-reported')}
        description={t('admin:localGpuStates.this-deployment-has-not-provisioned-a')}
      />
      <p data-testid="enrichment-not-available-copy" className="dp-muted">
        {t('admin:localGpuStates.this-is-an-operator-feature-in')}
      </p>
    </div>
  );
}

/**
 * The subject could not be reached.
 *
 * Renders `unknown` explicitly and never a health verdict: "we could not ask"
 * and "it is fine" are different answers, and only one of them is what the
 * operator asked.
 */
export function LocalGpuUnknownState({ detail }: { readonly detail: string }): ReactNode {
    const { t } = useTranslation();
return (
    <div data-testid="admin-local-gpu-unknown">
      <EmptyState title={t('admin:localGpuStates.local-gpu-health-unknown')} description={detail} />
      <p data-testid="enrichment-unknown-badge">
        <Badge tone="neutral">{t('admin:localGpuStates.unknown')}</Badge>
      </p>
    </div>
  );
}

/**
 * Flag on, the read failed, and the rest of the admin area is unaffected.
 *
 * The "rest of the admin area is unaffected" line is not reassurance for the
 * user — it is the runbook step. An operator who sees this and then wonders
 * whether the panel took the rest of Admin down has to go and check.
 */
export function LocalGpuUnavailableState({
  message,
  correlationId,
  onRetry,
  retryPending,
  onDismiss,
}: {
  readonly message: string;
  readonly correlationId: string | undefined;
  readonly onRetry: () => void;
  readonly retryPending: boolean;
  readonly onDismiss: () => void;
}): ReactNode {
    const { t } = useTranslation();
return (
    <div data-testid="admin-local-gpu-unavailable">
      <ErrorState
        title={t('admin:localGpuStates.local-gpu-health-unavailable')}
        message={message}
        correlationId={correlationId}
        onRetry={onRetry}
      />
      {retryPending ? (
        <p data-testid="admin-local-gpu-retrying" className="dp-muted">
          {t('admin:localGpuStates.retrying')}
        </p>
      ) : null}
      <button
        type="button"
        className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
        data-testid="admin-local-gpu-dismiss"
        onClick={onDismiss}
      >
        {t('admin:localGpuStates.dismiss')}
      </button>
      <p data-testid="admin-local-gpu-nonblocking" className="dp-muted">
        {t('admin:localGpuStates.the-rest-of-the-admin-area')}
      </p>
    </div>
  );
}

/**
 * Privacy-policy routing note (§19.3).
 *
 * The copy declares the local processing path AND the boundary condition, and
 * is deliberately frozen with the §19.3 policy review rather than edited
 * freely afterwards: this is the one place in the product that makes a
 * residency claim to an operator, and a claim that can drift silently from
 * what the platform does is worse than no claim.
 */
export function LocalGpuPrivacyNote(): ReactNode {
    const { t } = useTranslation();
return (
    <Alert tone="info" title={t('admin:localGpuStates.where-this-processing-runs')}>
      <p data-testid="enrichment-privacy-note">
        {t('admin:localGpuStates.local-processing-path-declared-the-operator')}
      </p>
      <p data-testid="enrichment-privacy-route" className="dp-muted">
        {t('admin:localGpuStates.privacy-policy-and-data-residency-terms')}
      </p>
    </Alert>
  );
}