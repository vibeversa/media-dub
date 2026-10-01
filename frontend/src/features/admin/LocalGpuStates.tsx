import type { ReactNode } from 'react';
import { Alert } from '../../components/Alert/Alert.js';
import { Badge } from '../../components/Badge/Badge.js';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';

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
  return (
    <div data-testid="admin-local-gpu-not-available">
      <EmptyState
        title="Local GPU health not reported"
        description="This deployment has not provisioned a local-inference device health endpoint."
      />
      <p data-testid="enrichment-not-available-copy" className="dp-muted">
        This is an operator feature in setup. Everything else on the page works without it.
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
  return (
    <div data-testid="admin-local-gpu-unknown">
      <EmptyState title="Local GPU health unknown" description={detail} />
      <p data-testid="enrichment-unknown-badge">
        <Badge tone="neutral">unknown</Badge>
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
  return (
    <div data-testid="admin-local-gpu-unavailable">
      <ErrorState
        title="Local GPU health unavailable"
        message={message}
        correlationId={correlationId}
        onRetry={onRetry}
      />
      {retryPending ? (
        <p data-testid="admin-local-gpu-retrying" className="dp-muted">
          Retrying…
        </p>
      ) : null}
      <button
        type="button"
        className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
        data-testid="admin-local-gpu-dismiss"
        onClick={onDismiss}
      >
        Dismiss
      </button>
      <p data-testid="admin-local-gpu-nonblocking" className="dp-muted">
        The rest of the admin area is unaffected.
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
  return (
    <Alert tone="info" title="Where this processing runs">
      <p data-testid="enrichment-privacy-note">
        Local processing path declared: the operator&apos;s own local-inference host. Media, audio and transcript data processed here
        stays inside the operator&apos;s boundary and is never sent to a third-party provider. Any change that would move data
        outside that boundary is a policy change and requires explicit operator opt-in before it takes effect.
      </p>
      <p data-testid="enrichment-privacy-route" className="dp-muted">
        Privacy policy and data-residency terms are published with your organisation&apos;s admin documentation.
      </p>
    </Alert>
  );
}