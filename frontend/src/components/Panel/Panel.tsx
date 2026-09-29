import type { ReactNode } from 'react';

export interface PanelProps {
  readonly title?: string;
  readonly children: ReactNode;
  /**
   * Heading level for the title. Defaults to 3 because a `Panel` is a section
   * *inside* a card or a page section, so it sits one level below whatever
   * contains it. Same reasoning as `Card`'s `titleLevel` (Task 041C), and the
   * same reason it is a prop rather than a constant: a hardcoded level is right
   * until the day the component is used one level deeper, and then the document
   * silently skips a heading level.
   */
  readonly titleLevel?: 2 | 3 | 4 | 5 | 6;
}

/** Flat bordered panel (sections inside cards/pages). */
export function Panel({ title, children, titleLevel = 3 }: PanelProps): ReactNode {
  const Heading = `h${String(titleLevel)}` as 'h2' | 'h3' | 'h4' | 'h5' | 'h6';
  return (
      <section className="dp-panel">
        {title ? <Heading className="dp-panel-title">{title}</Heading> : null}
        {children}
      </section>
  );
}
