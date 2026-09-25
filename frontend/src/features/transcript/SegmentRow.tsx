import { memo } from 'react';
import type { ReactNode } from 'react';
import { deriveLineage, formatTimestamp } from './types.js';
import type { TranscriptSegmentView } from './types.js';

export interface SegmentRowProps {
  readonly segment: TranscriptSegmentView;
  readonly isSelected: boolean;
  readonly isActive: boolean;
  readonly onSelect: (segmentId: string) => void;
  readonly onSeek: (segmentId: string) => void;
}

/**
 * Transcript segment row (Task 027).
 *
 * Shows timestamp, speaker label, editable text (plain text only), confidence
 * badge, review flag, and an inline playback button. Selected vs active
 * states stay visually distinct (`data-selected` vs `data-active`); rows are
 * memoized for the 1000+ virtualized list. Keyboard: the list container moves
 * selection with Up/Down and seeks with Enter (row itself is a button grid).
 */
export const SegmentRow = memo(function SegmentRow({ segment, isSelected, isActive, onSelect, onSeek }: SegmentRowProps): ReactNode {
  const lineage = deriveLineage(segment);
  const confidenceText =
    segment.confidence === undefined ? 'confidence unknown' : `confidence ${String(Math.round(segment.confidence * 100))}%`;
  return (
    <div
      role="option"
      aria-selected={isSelected}
      data-testid={`transcript-row-${segment.id}`}
      data-selected={isSelected ? 'true' : 'false'}
      data-active={isActive ? 'true' : 'false'}
      data-segment-id={segment.id}
      className={isActive ? 'transcript-row transcript-row-active' : 'transcript-row'}
      style={{
        display: 'flex',
        gap: '0.5rem',
        alignItems: 'flex-start',
        padding: '0.5rem 0.75rem',
        borderLeft: isSelected ? '3px solid var(--color-brand)' : '3px solid transparent',
        background: isActive ? 'var(--color-info-bg)' : 'transparent',
      }}
    >
      <button
        type="button"
        data-testid={`transcript-play-${segment.id}`}
        aria-label={`Play segment starting at ${formatTimestamp(segment.startMs)}`}
        className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
        onClick={() => {
          onSeek(segment.id);
        }}
      >
        ▶
      </button>
      <button
        type="button"
        data-testid={`transcript-select-${segment.id}`}
        className="dp-focus-ring"
        style={{ flexGrow: 1, textAlign: 'left', background: 'none', border: 'none', padding: 0, cursor: 'pointer' }}
        onClick={() => {
          onSelect(segment.id);
        }}
        aria-label={`Select segment ${segment.id}`}
      >
        <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'baseline', flexWrap: 'wrap' }}>
          <span data-testid={`transcript-time-${segment.id}`} className="dp-muted">
            {formatTimestamp(segment.startMs)}
          </span>
          <span data-testid={`transcript-speaker-${segment.id}`}>{segment.speakerLabel}</span>
          <span data-testid={`transcript-confidence-${segment.id}`} title={confidenceText}>
            {segment.confidence === undefined ? '—' : `${String(Math.round(segment.confidence * 100))}%`}
          </span>
          {segment.needsReview ? (
            <a
              href="/review"
              data-testid={`transcript-review-flag-${segment.id}`}
              onClick={(event) => {
                event.stopPropagation();
              }}
            >
              needs review
            </a>
          ) : null}
        </div>
        <p data-testid={`transcript-text-${segment.id}`} style={{ margin: '0.25rem 0' }}>
          {segment.text === '' ? '(empty segment)' : segment.text}
        </p>
        <div style={{ display: 'flex', gap: '0.35rem', flexWrap: 'wrap' }}>
          <span data-testid={`transcript-badge-original-${segment.id}`}>original</span>
          <span data-testid={`transcript-badge-selected-${segment.id}`}>{lineage.selectedBadge}</span>
          {lineage.hasManual ? <span data-testid={`transcript-badge-manual-${segment.id}`}>manual</span> : null}
        </div>
      </button>
    </div>
  );
});
