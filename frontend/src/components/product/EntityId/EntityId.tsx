import type { ReactNode } from 'react';
import { useState } from 'react';

export interface EntityIdProps {
  readonly id: string;
  readonly label?: string;
}

/** Monospace id with copy button. */
export function EntityId({ id, label }: EntityIdProps): ReactNode {
  const [copied, setCopied] = useState(false);
  return (
    <>
      <style>{`.dp-entity{display:inline-flex;align-items:center;gap:var(--space-2);font-family:var(--font-family-mono);font-size:var(--font-size-sm);color:var(--color-text-muted)}.dp-entity code{background-color:var(--color-neutral-status-bg);border-radius:var(--radius-sm);padding:0 var(--space-1)}`}</style>
      <span className="dp-entity">
        {label ? <span>{label}</span> : null}
        <code>{id}</code>
        <button
          type="button"
          className="dp-btn dp-btn-ghost dp-btn-sm dp-focus-ring"
          aria-label={`Copy ${label ?? 'id'}`}
          onClick={() => {
            void navigator.clipboard?.writeText(id).then(
              () => {
                setCopied(true);
              },
              () => {
                setCopied(false);
              },
            );
          }}
        >
          {copied ? 'Copied' : 'Copy'}
        </button>
      </span>
    </>
  );
}
