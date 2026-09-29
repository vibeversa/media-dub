import type { ReactNode } from 'react';

export interface EmptyStateProps {
  readonly title: string;
  readonly description?: string;
  readonly action?: ReactNode;
}

/** Zero-data placeholder. */
export function EmptyState({ title, description, action }: EmptyStateProps): ReactNode {
  return (
      <div className="dp-state">
        <p className="dp-state-title">{title}</p>
        {description ? <p>{description}</p> : null}
        {action}
      </div>
  );
}
