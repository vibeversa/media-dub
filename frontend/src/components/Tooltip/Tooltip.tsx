import type { ReactNode } from 'react';
import { useId, useState } from 'react';
import { useEscape } from '../_shared/useEscape.js';

export interface TooltipProps {
  readonly label: string;
  readonly content: ReactNode;
  readonly children: ReactNode;
}

/** Hover/focus tooltip; Escape closes. Content renders as plain text nodes. */
export function Tooltip({ label, content, children }: TooltipProps): ReactNode {
  const autoId = useId();
  const tipId = `tooltip-${autoId}`;
  const [open, setOpen] = useState(false);
  useEscape(open, () => {
    setOpen(false);
  });

  return (
    <>
      <style>{`.dp-tooltip{position:relative;display:inline-block}.dp-tooltip-bubble{position:absolute;inset-block-end:100%;inset-inline-start:50%;transform:translateX(-50%);margin-block-end:var(--space-1);z-index:30;max-inline-size:min(16rem,90vw);background-color:var(--color-surface-inverse);color:var(--color-text-inverse);font-size:var(--font-size-sm);border-radius:var(--radius-md);padding:var(--space-1) var(--space-2)}[dir="rtl"] .dp-tooltip-bubble{transform:translateX(50%)}`}</style>
      <span className="dp-tooltip">
        <span
          tabIndex={0}
          role="button"
          aria-label={label}
          aria-describedby={open ? tipId : undefined}
          className="dp-focus-ring"
          onMouseEnter={() => {
            setOpen(true);
          }}
          onMouseLeave={() => {
            setOpen(false);
          }}
          onFocus={() => {
            setOpen(true);
          }}
          onBlur={() => {
            setOpen(false);
          }}
          onKeyDown={(e) => {
            if (e.key === 'Escape') {
              setOpen(false);
            }
          }}
        >
          {children}
        </span>
        {open ? (
          <span id={tipId} role="tooltip" className="dp-tooltip-bubble">
            {content}
          </span>
        ) : null}
      </span>
    </>
  );
}
