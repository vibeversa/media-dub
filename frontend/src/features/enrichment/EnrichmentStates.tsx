import type { ReactNode } from 'react';
import { Alert } from '../../components/Alert/Alert.js';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Badge } from '../../components/Badge/Badge.js';

/**
 * Enrichment state components (Task 044).
 *
 * Four named states, four different meanings. Collapsing them into one
 * "something went wrong" box is what makes an optional feature feel broken
 * rather than absent, so each gets its own component and its own copy:
 *
 * - `UnavailableState` — the capability is ON and it FAILED. Dismissible,
 *   because the correct response to an enrichment failure is to carry on with
 *   the transcript, translation, voice and review work, not to be held up by
 *   a red box that will not go away.
 * - `NotAvailableState` — the capability is provisioned in the product but not
 *   in this deployment. Not an error and not dismissible: it is a fact about
 *   the installation.
 * - `UnknownState` — an operator-only panel whose subject could not be reached.
 *   Renders the subject as unknown rather than as healthy, because "we could
 *   not ask" and "it is fine" are different answers.
 * - `GoneState` — an artifact's link target no longer exists. Inline and
 *   non-blocking by construction.
 *
 * Every one of these renders **inside** the enrichment section. None of them
 * can render outside it, because the section itself only mounts when its flag
 * is on — that is what makes "zero UI chrome when the flag is off" structural
 * rather than a promise each state component has to keep.
 */

export interface UnavailableStateProps {
  readonly title: string;
  readonly message: string;
  readonly correlationId: string | undefined;
  readonly onRetry: () => void;
  readonly retryPending: boolean;
}

/**
 * Enrichment failed while its flag was on. Dismissible with an explicit retry.
 *
 * The dismissal is local state in the *panel*, not in this component, so
 * dismissing one enrichment surface cannot dismiss another; this component
 * renders both affordances and the panel owns which one is shown.
 */
export function UnavailableState({
  title,
  message,
  correlationId,
  onRetry,
  retryPending,
}: UnavailableStateProps): ReactNode {
  return (
    <div data-testid="enrichment-unavailable">
      <ErrorState
        title={title}
        message={message}
        correlationId={correlationId}
        onRetry={onRetry}
      />
      <p data-testid="enrichment-unavailable-nonblocking" className="dp-muted">
        Transcript, translation, voice, review and export continue to work normally.
      </p>
      {retryPending ? (
        <p data-testid="enrichment-unavailable-retrying" className="dp-muted">
          Retrying…
        </p>
      ) : null}
    </div>
  );
}

export interface NotAvailableStateProps {
  readonly title: string;
  readonly description: string;
}

/** Flag on, backend not provisioned in this deployment. Not an error. */
export function NotAvailableState({ title, description }: NotAvailableStateProps): ReactNode {
  return (
    <div data-testid="enrichment-not-available">
      <EmptyState title={title} description={description} />
      <p data-testid="enrichment-not-available-copy" className="dp-muted">
        This is an operator feature in setup. Everything else on the page works without it.
      </p>
    </div>
  );
}

export interface UnknownStateProps {
  readonly title: string;
  readonly detail: string;
}

/** The subject could not be reached. Says so; never implies health. */
export function UnknownState({ title, detail }: UnknownStateProps): ReactNode {
  return (
    <div data-testid="enrichment-unknown">
      <EmptyState title={title} description={detail} />
      <p data-testid="enrichment-unknown-badge">
        <Badge tone="neutral">unknown</Badge>
      </p>
    </div>
  );
}

/** An artifact's link target no longer exists. Inline, non-blocking. */
export function GoneState({ detail }: { readonly detail: string }): ReactNode {
  return (
    <span data-testid="enrichment-gone" className="dp-muted">
      {detail}
    </span>
  );
}

/** Operator privacy routing note (§19.3). Copy is frozen with the policy review. */
export function EnrichmentPrivacyNote({ boundary }: { readonly boundary: string }): ReactNode {
  return (
    <Alert tone="info" title="Where this processing runs">
      <p data-testid="enrichment-privacy-note">
        {`Local processing path declared: ${boundary}. Media and transcript data processed here stays inside the operator's own boundary and is never sent to a third-party provider. Any change that would move data outside that boundary is a policy change and requires explicit operator opt-in before it takes effect.`}
      </p>
      <p data-testid="enrichment-privacy-route" className="dp-muted">
        Privacy policy and data-residency terms are published with your organisation's admin documentation.
      </p>
    </Alert>
  );
}