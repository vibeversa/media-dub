import type { ReactNode } from 'react';

export interface CardProps {
  readonly title?: string;
  readonly children: ReactNode;
  /**
   * Heading level for the title. Defaults to 2 because every `Card` in the app is
   * a top-level section of a page whose heading is the `h1` - the dashboard's six
   * cards, all rendered directly under `page-dashboard`'s `h1`.
   *
   * Task 041C: a hardcoded `h3` produced `heading-order` violations on the
   * dashboard, where the document went `h1` -> `h3` and skipped a level. A
   * heading that skips a level breaks the "jump to heading level N" affordance
   * every screen reader offers, and axe reports it. The prop exists so a card
   * nested inside another card can say so instead of guessing.
   */
  readonly titleLevel?: 2 | 3 | 4 | 5 | 6;
}

/** Raised content card. */
export function Card({ title, children, titleLevel = 2 }: CardProps): ReactNode {
  const Heading = `h${String(titleLevel)}` as 'h2' | 'h3' | 'h4' | 'h5' | 'h6';
  return (
      <section className="dp-card">
        {title ? <Heading className="dp-card-title">{title}</Heading> : null}
        {children}
      </section>
  );
}
