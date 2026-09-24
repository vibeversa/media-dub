import type { ReactNode } from 'react';

export interface PanelProps {
  readonly title?: string;
  readonly children: ReactNode;
}

/** Flat bordered panel (sections inside cards/pages). */
export function Panel({ title, children }: PanelProps): ReactNode {
  return (
    <>
      <style>{`.dp-panel{background-color:var(--color-surface-raised);border:1px solid var(--color-border);border-radius:var(--radius-md);padding:var(--space-4)}.dp-panel-title{font-size:var(--font-size-md);font-weight:var(--font-weight-semibold);margin:0 0 var(--space-2)}`}</style>
      <section className="dp-panel">
        {title ? <h3 className="dp-panel-title">{title}</h3> : null}
        {children}
      </section>
    </>
  );
}
