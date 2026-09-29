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
  );
}
