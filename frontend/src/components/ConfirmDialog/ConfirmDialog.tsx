import type { ReactNode } from 'react';
import { Modal } from '../Modal/Modal.js';

export interface ConfirmDialogProps {
  readonly open: boolean;
  readonly title: string;
  readonly description: string;
  readonly confirmLabel: string;
  readonly onConfirm: () => void;
  readonly onCancel: () => void;
}

/** Destructive confirmation; focus-trapped via Modal. */
export function ConfirmDialog({ open, title, description, confirmLabel, onConfirm, onCancel }: ConfirmDialogProps): ReactNode {
  return (
    <Modal open={open} title={title} onClose={onCancel}>
      <p className="dp-muted">{description}</p>
      <div style={{ display: 'flex', gap: 'var(--space-2)', justifyContent: 'flex-end', marginBlockStart: 'var(--space-4)' }}>
        <button type="button" className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring" onClick={onCancel}>
          Cancel
        </button>
        <button type="button" className="dp-btn dp-btn-danger dp-btn-md dp-focus-ring" onClick={onConfirm}>
          {confirmLabel}
        </button>
      </div>
    </Modal>
  );
}
