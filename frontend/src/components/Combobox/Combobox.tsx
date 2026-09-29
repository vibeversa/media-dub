import type { ReactNode } from 'react';
import { useId, useMemo, useState } from 'react';
import { cx } from '../_shared/cx.js';

export interface ComboboxOption {
  readonly value: string;
  readonly label: string;
}

export interface ComboboxProps {
  readonly label: string;
  readonly options: readonly ComboboxOption[];
  readonly value?: string;
  readonly placeholder?: string;
  readonly onChange?: (value: string) => void;
}

/** Filterable single-select combobox (arrow-key navigable listbox). */
export function Combobox({ label, options, value, placeholder, onChange }: ComboboxProps): ReactNode {
  const autoId = useId();
  const fieldId = `combobox-${autoId}`;
  const listId = `${fieldId}-list`;
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [active, setActive] = useState(0);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (q === '') {
      return options;
    }
    return options.filter((o) => o.label.toLowerCase().includes(q) || o.value.toLowerCase().includes(q));
  }, [options, query]);

  const selected = options.find((o) => o.value === value);

  const choose = (next: string): void => {
    onChange?.(next);
    setOpen(false);
    setQuery('');
  };

  return (
    <>
      <div className="dp-field">
        <label className="dp-label" htmlFor={fieldId}>
          {label}
        </label>
        <div className="dp-combo">
          <input
            id={fieldId}
            role="combobox"
            aria-expanded={open}
            aria-controls={listId}
            aria-activedescendant={filtered[active] ? `${listId}-${active}` : undefined}
            className="dp-input dp-focus-ring"
            placeholder={placeholder ?? selected?.label ?? 'Search…'}
            value={query}
            onChange={(e) => {
              setQuery(e.target.value);
              setOpen(true);
              setActive(0);
            }}
            onFocus={() => {
              setOpen(true);
            }}
            onBlur={() => {
              setOpen(false);
            }}
            onKeyDown={(e) => {
              if (e.key === 'ArrowDown') {
                e.preventDefault();
                setOpen(true);
                setActive((a) => Math.min(a + 1, Math.max(filtered.length - 1, 0)));
              } else if (e.key === 'ArrowUp') {
                e.preventDefault();
                setActive((a) => Math.max(a - 1, 0));
              } else if (e.key === 'Enter') {
                const pick = filtered[active];
                if (open && pick) {
                  e.preventDefault();
                  choose(pick.value);
                }
              } else if (e.key === 'Escape') {
                setOpen(false);
              }
            }}
          />
          {open ? (
            <ul role="listbox" id={listId} className={cx('dp-combo-list')}>
              {filtered.map((o, i) => (
                <li
                  key={o.value}
                  id={`${listId}-${i}`}
                  role="option"
                  aria-selected={o.value === value}
                  data-active={i === active}
                  className="dp-combo-item"
                  onMouseDown={(e) => {
                    e.preventDefault();
                    choose(o.value);
                  }}
                >
                  {o.label}
                </li>
              ))}
              {filtered.length === 0 ? <li className="dp-hint">No matches.</li> : null}
            </ul>
          ) : null}
        </div>
      </div>
    </>
  );
}
