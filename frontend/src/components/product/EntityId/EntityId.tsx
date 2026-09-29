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
