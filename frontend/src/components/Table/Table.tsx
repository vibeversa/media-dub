import type { ReactNode } from 'react';

export interface TableColumn<T> {
  readonly key: string;
  readonly header: string;
  readonly render: (row: T) => ReactNode;
}

export interface TableProps<T> {
  readonly columns: readonly TableColumn<T>[];
  readonly rows: readonly T[];
  readonly caption: string;
  readonly getRowId: (row: T, index: number) => string;
}

/** Accessible table with sticky header and horizontal scroll wrapper. */
export function Table<T>({ columns, rows, caption, getRowId }: TableProps<T>): ReactNode {
  return (
    <>
      <style>{`.dp-table-wrap{overflow:auto;border:1px solid var(--color-border);border-radius:var(--radius-md)}.dp-table{border-collapse:collapse;inline-size:100%;font-size:var(--font-size-sm)}.dp-table th,.dp-table td{padding:var(--space-2) var(--space-3);text-align:start;border-block-end:1px solid var(--color-border)}.dp-table thead th{position:sticky;inset-block-start:0;background-color:var(--color-surface);font-weight:var(--font-weight-semibold)}`}</style>
      <div className="dp-table-wrap dp-table-sticky">
        <table className="dp-table">
          <caption className="dp-muted">{caption}</caption>
          <thead>
            <tr>
              {columns.map((c) => (
                <th key={c.key} scope="col">
                  {c.header}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.map((row, i) => (
              <tr key={getRowId(row, i)}>
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
