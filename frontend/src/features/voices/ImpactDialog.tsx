import type { ReactNode } from 'react';
import { Modal } from '../../components/Modal/Modal.js';

export type ImpactMode = 'assign' | 'replace' | 'reset';

export interface ImpactDialogProps {
  readonly open: boolean;
  readonly speakerLabel: string;
  readonly segmentCount: number;
  readonly voiceLabel: string;
  readonly mode: ImpactMode;
  /** Backend-provided cost note, rendered verbatim when present. */
  readonly costNote?: string;
  readonly isPending: boolean;
  readonly onConfirm: () => void;
  readonly onCancel: () => void;
}

function titleFor(mode: ImpactMode): string {
  if (mode === 'replace') {
    return 'Replace voice?';
  }
  if (mode === 'reset') {
    return 'Reset voice?';
  }
  return 'Assign voice?';
}

/**
 * Impact pre-confirm (Task 029, R2).
 *
 * Precedes every assign/replace/reset: affected segment count, downstream
 * invalidation note (translations/audio re-render), and the backend cost
 * note where provided. Confirm runs the mutation; cancel is a no-op (the
 * caller never mutates on cancel). Zero-segment speakers note explicitly
 * that no segments are affected (assignment stays allowed).
 */
export function ImpactDialog({
  open,
  speakerLabel,
  segmentCount,
  voiceLabel,
  mode,
  costNote,
  isPending,
  onConfirm,
  onCancel,
}: ImpactDialogProps): ReactNode {
  const segmentsText =
    segmentCount === 0
      ? `Speaker ${speakerLabel} has no segments yet. No segments will be affected, but future segments will use ${voiceLabel}.`
      : `This will affect ${String(segmentCount)} ${segmentCount === 1 ? 'segment' : 'segments'} for speaker ${speakerLabel} with voice ${voiceLabel}.`;
  return (
    <Modal open={open} title={titleFor(mode)} onClose={onCancel}>
      <div data-testid="voices-impact-dialog" role="dialog" aria-label={titleFor(mode)}>
        <p data-testid="voices-impact-segments">{segmentsText}</p>
        <p data-testid="voices-impact-invalidation">
          Assigning a new voice invalidates dependent translations and re-renders dub audio for the affected segments.
        </p>
        {costNote !== undefined && costNote !== '' ? (
          <p data-testid="voices-impact-cost">{costNote}</p>
        ) : null}
        <div style={{ display: 'flex', gap: '0.5rem', marginTop: '1rem' }}>
          <button
            type="button"
            data-testid="voices-impact-confirm"
            className="dp-btn dp-btn-primary dp-btn-md dp-focus-ring"
            disabled={isPending}
            onClick={onConfirm}
          >
            Confirm voice change
          </button>
          <button
            type="button"
            data-testid="voices-impact-cancel"
            className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
            disabled={isPending}
            onClick={onCancel}
          >
            Cancel
          </button>
        </div>
        {isPending ? <p data-testid="voices-impact-pending">Applying voice change…</p> : null}
      </div>
    </Modal>
  );
}
