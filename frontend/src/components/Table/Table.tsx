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
