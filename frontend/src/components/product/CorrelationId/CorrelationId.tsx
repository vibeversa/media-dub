import type { ReactNode } from 'react';

export interface CorrelationIdProps {
  readonly value: string;
}

/** Debug-only correlation id with copy (hidden unless explicitly rendered). */
export function CorrelationId({ value }: CorrelationIdProps): ReactNode {
  return (
    <span className="dp-muted" style={{ fontFamily: 'var(--font-family-mono)', fontSize: 'var(--font-size-xs)' }}>
      corr: {value}
      <button
        type="button"
        aria-label="Copy correlation id"
        className="dp-focus-ring"
        onClick={() => {
          void navigator.clipboard?.writeText(value).catch(() => {});
        }}
      >
        Copy
      </button>
    </span>
  );
}
