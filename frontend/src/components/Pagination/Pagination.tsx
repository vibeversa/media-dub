import type { ReactNode } from 'react';

export interface PaginationProps {
  readonly page: number;
  readonly pageSize: number;
  readonly total: number;
  readonly onPageChange: (page: number) => void;
}

/** Page-based pagination (1-based). */
export function Pagination({ page, pageSize, total, onPageChange }: PaginationProps): ReactNode {
  const totalPages = Math.max(1, Math.ceil(total / Math.max(pageSize, 1)));
  const safe = Math.min(Math.max(page, 1), totalPages);
  return (
    <>
      <style>{`.dp-pager{display:flex;align-items:center;gap:var(--space-2);font-size:var(--font-size-sm);color:var(--color-text-muted)}`}</style>
      <nav aria-label="Pagination" className="dp-pager">
        <button
          type="button"
          disabled={safe <= 1}
          className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
          onClick={() => {
            onPageChange(safe - 1);
          }}
        >
          Previous
        </button>
        <span aria-live="polite">
          Page {safe} of {totalPages}
        </span>
        <button
          type="button"
          disabled={safe >= totalPages}
          className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
          onClick={() => {
            onPageChange(safe + 1);
          }}
        >
          Next
        </button>
      </nav>
    </>
  );
}
