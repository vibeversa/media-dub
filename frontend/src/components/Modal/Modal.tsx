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
