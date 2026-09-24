import type { ReactNode } from 'react';
import { useState } from 'react';

export interface DataGridColumn<T> {
  readonly key: string;
  readonly header: string;
  /** Native tooltip for the header cell (e.g. approximate-ordering notes). */
  readonly headerTitle?: string;
  readonly render: (row: T) => ReactNode;
}

export interface DataGridProps<T> {
  readonly columns: readonly DataGridColumn<T>[];
  readonly rows: readonly T[];
  readonly caption: string;
  readonly getRowId: (row: T, index: number) => string;
  readonly onRowActivate?: (row: T, index: number) => void;
}

/** Grid with row keyboard navigation (Up/Down + Enter activates). */
export function DataGrid<T>({ columns, rows, caption, getRowId, onRowActivate }: DataGridProps<T>): ReactNode {
  const [active, setActive] = useState(0);

  return (
    <>
      <style>{`.dp-grid-wrap{overflow:auto;border:1px solid var(--color-border);border-radius:var(--radius-md)}.dp-grid{border-collapse:collapse;inline-size:100%;font-size:var(--font-size-sm)}.dp-grid th,.dp-grid td{padding:var(--space-2) var(--space-3);text-align:start;border-block-end:1px solid var(--color-border)}.dp-grid thead th{position:sticky;inset-block-start:0;background-color:var(--color-surface);font-weight:var(--font-weight-semibold)}.dp-grid tbody tr[data-active="true"]{background-color:var(--color-brand-subtle)}.dp-grid tbody tr{cursor:pointer}`}</style>
      <div className="dp-grid-wrap">
        <table
          className="dp-grid"
          role="grid"
          aria-label={caption}
          tabIndex={0}
          onKeyDown={(e) => {
            if (e.key === 'ArrowDown') {
              e.preventDefault();
              setActive((a) => Math.min(a + 1, Math.max(rows.length - 1, 0)));
            } else if (e.key === 'ArrowUp') {
              e.preventDefault();
              setActive((a) => Math.max(a - 1, 0));
            } else if (e.key === 'Enter') {
              const row = rows[active];
              if (row) {
                onRowActivate?.(row, active);
              }
            }
          }}
        >
          <caption className="dp-muted">{caption}</caption>
          <thead>
            <tr>
              {columns.map((c) => (
                <th key={c.key} scope="col" title={c.headerTitle}>
                  {c.header}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.map((row, i) => (
              <tr
                key={getRowId(row, i)}
                data-active={i === active}
                onClick={() => {
                  setActive(i);
                  onRowActivate?.(row, i);
                }}
              >
                {columns.map((c) => (
                  <td key={c.key}>{c.render(row)}</td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}
