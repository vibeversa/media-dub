import type { ReactNode } from 'react';
import { useEffect, useRef } from 'react';
import { createPortal } from 'react-dom';
import { useFocusTrap } from '../_shared/useFocusTrap.js';
import { cx } from '../_shared/cx.js';

export interface ModalProps {
  readonly open: boolean;
  readonly title: string;
  readonly onClose: () => void;
  readonly children: ReactNode;
}

/** Focus-trapped modal; Escape closes, focus restores (R3). */
export function Modal({ open, title, onClose, children }: ModalProps): ReactNode {
  const ref = useRef<HTMLDivElement>(null);
  useFocusTrap(open, ref);

  useEffect(() => {
    if (!open) {
      return;
    }
    const onKey = (e: KeyboardEvent): void => {
      if (e.key === 'Escape') {
        onClose();
      }
    };
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('keydown', onKey);
    };
  }, [open, onClose]);

  if (!open) {
    return null;
  }
  return createPortal(
    <>
      <style>{`.dp-overlay{position:fixed;inset:0;background-color:rgb(15 23 42 / 0.5);display:flex;align-items:center;justify-content:center;padding:var(--space-4);z-index:50}.dp-modal{background-color:var(--color-surface-overlay);color:var(--color-text);border-radius:var(--radius-lg);box-shadow:var(--shadow-lg);max-inline-size:32rem;inline-size:100%;padding:var(--space-6)}.dp-modal-title{font-size:var(--font-size-lg);font-weight:var(--font-weight-semibold);margin:0 0 var(--space-4)}`}</style>
      <div className="dp-overlay" onMouseDown={onClose}>
        <div
          ref={ref}
          role="dialog"
          aria-modal="true"
          aria-label={title}
          tabIndex={-1}
          className={cx('dp-modal')}
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
