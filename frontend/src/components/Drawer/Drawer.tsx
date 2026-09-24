import type { ReactNode } from 'react';
import { useRef } from 'react';
import { createPortal } from 'react-dom';
import { useEscape } from '../_shared/useEscape.js';
import { useFocusTrap } from '../_shared/useFocusTrap.js';

export interface DrawerProps {
  readonly open: boolean;
  readonly title: string;
  readonly onClose: () => void;
  readonly children: ReactNode;
}

/** Side drawer; focus-trapped, Escape closes, focus restores. */
export function Drawer({ open, title, onClose, children }: DrawerProps): ReactNode {
  const ref = useRef<HTMLDivElement>(null);
  useFocusTrap(open, ref);
  useEscape(open, onClose);

  if (!open) {
    return null;
  }
  return createPortal(
    <>
      <style>{`.dp-drawer-overlay{position:fixed;inset:0;background-color:rgb(15 23 42 / 0.5);z-index:50;display:flex;justify-content:flex-end}.dp-drawer{background-color:var(--color-surface-overlay);color:var(--color-text);inline-size:min(24rem,90vw);block-size:100%;padding:var(--space-6);box-shadow:var(--shadow-lg);overflow:auto}[dir="rtl"] .dp-drawer-overlay{justify-content:flex-start}`}</style>
      <div className="dp-drawer-overlay" onMouseDown={onClose}>
        <div
          ref={ref}
          role="dialog"
          aria-modal="true"
          aria-label={title}
          tabIndex={-1}
          className="dp-drawer"
          onMouseDown={(e) => {
            e.stopPropagation();
          }}
        >
          <h2 className="dp-modal-title">{title}</h2>
          {children}
        </div>
      </div>
    </>,
    document.body,
  );
}
