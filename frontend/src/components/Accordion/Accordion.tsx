import type { ReactNode } from 'react';
import { useState } from 'react';

export interface AccordionItem {
  readonly id: string;
  readonly title: string;
  readonly content: ReactNode;
}

export interface AccordionProps {
  readonly items: readonly AccordionItem[];
}

/** Stacked disclosures; button + region semantics, Enter/Space native. */
export function Accordion({ items }: AccordionProps): ReactNode {
  const [open, setOpen] = useState<ReadonlySet<string>>(new Set());
  return (
      <div className="dp-acc">
        {items.map((item) => {
          const isOpen = open.has(item.id);
          return (
            <div key={item.id} className="dp-acc-item">
              <button
                type="button"
                aria-expanded={isOpen}
                aria-controls={`acc-${item.id}`}
                className="dp-acc-btn dp-focus-ring"
                onClick={() => {
                  setOpen((prev) => {
                    const next = new Set(prev);
                    if (next.has(item.id)) {
                      next.delete(item.id);
                    } else {
                      next.add(item.id);
                    }
                    return next;
                  });
                }}
              >
                {item.title}
              </button>
              {isOpen ? (
                <div id={`acc-${item.id}`} role="region" className="dp-acc-panel">
                  {item.content}
                </div>
              ) : null}
            </div>
          );
        })}
      </div>
  );
}
