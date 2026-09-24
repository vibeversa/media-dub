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
    <>
      <style>{`.dp-popover{position:relative;display:inline-block}.dp-popover-panel{position:absolute;inset-block-start:100%;inset-inline-start:0;z-index:30;min-inline-size:12rem;max-inline-size:min(20rem,90vw);margin-block-start:var(--space-1);background-color:var(--color-surface-overlay);border:1px solid var(--color-border);border-radius:var(--radius-md);box-shadow:var(--shadow-md);padding:var(--space-3);overflow:auto}`}</style>
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
    </>
  );
}
