import type { ReactNode } from 'react';
import { useId, useState } from 'react';
import { useEscape } from '../_shared/useEscape.js';

export interface PopoverProps {
  readonly label: string;
  readonly content: ReactNode;
  readonly children?: ReactNode;
}

/** Click-to-toggle popover; Escape closes, viewport-clamped. */
export function Popover({ label, content, children }: PopoverProps): ReactNode {
  const autoId = useId();
  const panelId = `popover-${autoId}`;
  const [open, setOpen] = useState(false);
  useEscape(open, () => {
    setOpen(false);
  });

  return (
      <div className="dp-popover">
        <button
          type="button"
          aria-expanded={open}
          aria-controls={panelId}
          className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
          onClick={() => {
            setOpen((o) => !o);
          }}
        >
          {label}
        </button>
        {open ? (
          <div id={panelId} role="dialog" aria-label={label} className="dp-popover-panel">
            {content}
            {children}
          </div>
        ) : null}
      </div>
  );
}
