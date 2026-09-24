import type { ReactNode } from 'react';
import { useState } from 'react';

export interface TabItem {
  readonly id: string;
  readonly label: string;
  readonly content: ReactNode;
}

export interface TabsProps {
  readonly items: readonly TabItem[];
  readonly defaultId?: string;
}

/** Keyboard-operable tabs (arrow keys move, roving tabindex). */
export function Tabs({ items, defaultId }: TabsProps): ReactNode {
  const [active, setActive] = useState<string>(defaultId ?? items[0]?.id ?? '');
  const current = items.find((i) => i.id === active) ?? items[0];

  return (
    <>
      <style>{`.dp-tabs{display:flex;gap:var(--space-1);border-block-end:1px solid var(--color-border)}.dp-tab{border:none;background:none;color:var(--color-text-muted);font-size:var(--font-size-md);font-weight:var(--font-weight-medium);padding:var(--space-2) var(--space-3);cursor:pointer;border-block-end:2px solid transparent}.dp-tab[aria-selected="true"]{color:var(--color-text);border-block-end-color:var(--color-brand)}.dp-tabpanel{padding-block-start:var(--space-4)}`}</style>
      <div>
        <div role="tablist" aria-label="Tabs" className="dp-tabs">
          {items.map((item) => (
            <button
              key={item.id}
              type="button"
              role="tab"
              id={`tab-${item.id}`}
              aria-selected={item.id === active}
              aria-controls={`panel-${item.id}`}
              tabIndex={item.id === active ? 0 : -1}
              className="dp-tab dp-focus-ring"
              onClick={() => {
                setActive(item.id);
              }}
              onKeyDown={(e) => {
                const idx = items.findIndex((i) => i.id === active);
                if (e.key === 'ArrowRight' || e.key === 'ArrowLeft') {
                  e.preventDefault();
                  const dir = e.key === 'ArrowRight' ? 1 : -1;
                  const next = items[(idx + dir + items.length) % items.length];
                  if (next) {
                    setActive(next.id);
                    document.getElementById(`tab-${next.id}`)?.focus();
                  }
                }
              }}
            >
              {item.label}
            </button>
          ))}
        </div>
        {current ? (
          <div role="tabpanel" id={`panel-${current.id}`} aria-labelledby={`tab-${current.id}`} className="dp-tabpanel">
            {current.content}
          </div>
        ) : null}
      </div>
    </>
  );
}
