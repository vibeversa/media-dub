import type { ReactNode } from 'react';

export interface CardProps {
  readonly title?: string;
  readonly children: ReactNode;
}

/** Raised content card. */
export function Card({ title, children }: CardProps): ReactNode {
  return (
    <>
      <style>{`.dp-card{background-color:var(--color-surface);border:1px solid var(--color-border);border-radius:var(--radius-lg);box-shadow:var(--shadow-sm);padding:var(--space-4)}.dp-card-title{font-size:var(--font-size-lg);font-weight:var(--font-weight-semibold);margin:0 0 var(--space-2)}`}</style>
      <section className="dp-card">
        {title ? <h3 className="dp-card-title">{title}</h3> : null}
        {children}
      </section>
    </>
  );
}
