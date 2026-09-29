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

/**
 * Grid with row keyboard navigation (Up/Down + Enter activates).
 *
 * Enter only activates the row when it was not pressed on a control inside a
 * cell - see the guard in `onKeyDown`. A cell that contains a button owns its
 * own Enter.
 */
export function DataGrid<T>({ columns, rows, caption, getRowId, onRowActivate }: DataGridProps<T>): ReactNode {
  const [active, setActive] = useState(0);

  return (
    <>
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
              // Task 041C: Enter inside a cell that holds its own control belongs
              // to that control, not to the row.
              //
              // Without this, pressing Enter on a row action did BOTH things: the
              // button's click fired *and* the row activated, so "Delete" opened
              // the project workspace instead of the delete confirmation. The
              // project's action cell already stopped click propagation for
              // exactly this reason; the key path was missed, and the mouse and
              // the keyboard behaved differently on the same control.
              const target = e.target as HTMLElement | null;
              const tag = target?.tagName.toLowerCase() ?? '';
              if (
                tag === 'button' ||
                tag === 'a' ||
                tag === 'input' ||
                tag === 'select' ||
                tag === 'textarea' ||
                target?.getAttribute('role') === 'button' ||
                target?.getAttribute('role') === 'link'
              ) {
                return;
              }
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
