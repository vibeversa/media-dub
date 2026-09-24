import type { ReactNode } from 'react';

export interface ErrorStateProps {
  readonly title: string;
  readonly message?: string;
  readonly correlationId?: string;
  readonly onRetry?: () => void;
}

/** Failure placeholder; correlation id shown as plain text for support. */
export function ErrorState({ title, message, correlationId, onRetry }: ErrorStateProps): ReactNode {
  return (
    <>
      <style>{`.dp-err{text-align:center;padding:var(--space-8) var(--space-4)}.dp-err-code{font-family:var(--font-family-mono);font-size:var(--font-size-sm);color:var(--color-text-muted)}`}</style>
      <div className="dp-err" role="alert">
        <p className="dp-state-title">{title}</p>
        {message ? <p className="dp-muted">{message}</p> : null}
        {correlationId ? <p className="dp-err-code">Ref: {correlationId}</p> : null}
        {onRetry ? (
          <button type="button" className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring" onClick={onRetry}>
            Retry
          </button>
        ) : null}
      </div>
    </>
  );
}
