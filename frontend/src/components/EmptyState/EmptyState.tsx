import type { ReactNode } from 'react';

export interface EmptyStateProps {
  readonly title: string;
  readonly description?: string;
  readonly action?: ReactNode;
}

/** Zero-data placeholder. */
export function EmptyState({ title, description, action }: EmptyStateProps): ReactNode {
  return (
    <>
      <style>{`.dp-state{text-align:center;padding:var(--space-8) var(--space-4);color:var(--color-text-muted)}.dp-state-title{font-size:var(--font-size-lg);font-weight:var(--font-weight-semibold);color:var(--color-text);margin:0 0 var(--space-2)}`}</style>
      <div className="dp-state">
        <p className="dp-state-title">{title}</p>
        {description ? <p>{description}</p> : null}
        {action}
      </div>
    </>
  );
}
