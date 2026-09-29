import type { ReactNode } from 'react';
import { useMemo, useState } from 'react';

export interface CommandItem {
  readonly id: string;
  readonly label: string;
}

export interface CommandMenuProps {
  readonly items: readonly CommandItem[];
  readonly onSelect: (id: string) => void;
  readonly placeholder?: string;
}

/** Filterable command palette (arrow-key navigable). */
export function CommandMenu({ items, onSelect, placeholder }: CommandMenuProps): ReactNode {
  const [query, setQuery] = useState('');
  const [active, setActive] = useState(0);
  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (q === '') {
      return items;
    }
    return items.filter((i) => i.label.toLowerCase().includes(q));
  }, [items, query]);

  return (
    <>
      <div className="dp-cmd" role="dialog" aria-label="Commands">
        <input
          aria-label="Search commands"
          className="dp-cmd-input dp-focus-ring"
          placeholder={placeholder ?? 'Type a command…'}
          value={query}
          onChange={(e) => {
            setQuery(e.target.value);
            setActive(0);
          }}
          onKeyDown={(e) => {
            if (e.key === 'ArrowDown') {
              e.preventDefault();
              setActive((a) => Math.min(a + 1, Math.max(filtered.length - 1, 0)));
            } else if (e.key === 'ArrowUp') {
              e.preventDefault();
              setActive((a) => Math.max(a - 1, 0));
            } else if (e.key === 'Enter') {
              const pick = filtered[active];
              if (pick) {
                onSelect(pick.id);
              }
            }
          }}
        />
        <ul className="dp-cmd-list" role="listbox" aria-label="Results">
          {filtered.map((item, i) => (
            <li
              key={item.id}
              role="option"
              aria-selected={i === active}
              data-active={i === active}
              className="dp-cmd-item"
              onMouseDown={(e) => {
                e.preventDefault();
                onSelect(item.id);
              }}
            >
              {item.label}
            </li>
          ))}
        </ul>
      </div>
    </>
  );
}
