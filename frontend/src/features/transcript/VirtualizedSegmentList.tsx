import { useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { SegmentRow } from './SegmentRow.js';
import { filterTranscriptSegments, findActiveSegmentId } from './types.js';
import type { TranscriptFilter, TranscriptSegmentView } from './types.js';

export interface VirtualizedSegmentListProps {
  readonly segments: readonly TranscriptSegmentView[];
  readonly selectedId: string | undefined;
  readonly positionMs: number;
  readonly autoScroll: boolean;
  readonly filter: TranscriptFilter;
  readonly onSelect: (segmentId: string) => void;
  readonly onSeek: (segmentId: string) => void;
  readonly onManualScroll: () => void;
}

const OVERSCAN = 5;
const ESTIMATED_ROW_PX = 96;
const VIEWPORT_ROWS = 12;

/**
 * Virtualized transcript list (Task 027, R6).
 *
 * Windowing over the single transcript query: only the visible slice plus
 * overscan renders (`transcript-row-*` count stays bounded for 1000+ rows).
 * Rows are memoized (`SegmentRow`); filtering is debounced in the parent and
 * applied here purely (speaker exact, text substring, review flag). Never
 * fetches per-row — segments arrive via props. Active highlight tracks
 * `positionMs`; autoscroll yields to manual scrolling via `onManualScroll`.
 */
export function VirtualizedSegmentList({
  segments,
  selectedId,
  positionMs,
  autoScroll,
  filter,
  onSelect,
  onSeek,
  onManualScroll,
}: VirtualizedSegmentListProps): ReactNode {
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const [scrollTop, setScrollTop] = useState(0);
  const filtered = useMemo(() => filterTranscriptSegments(segments, filter), [segments, filter]);
  const activeId = useMemo(() => findActiveSegmentId(filtered, positionMs), [filtered, positionMs]);

  const viewportPx = VIEWPORT_ROWS * ESTIMATED_ROW_PX;
  const totalPx = filtered.length * ESTIMATED_ROW_PX;
  const startIndex = Math.max(0, Math.floor(scrollTop / ESTIMATED_ROW_PX) - OVERSCAN);
  const visibleCount = VIEWPORT_ROWS + OVERSCAN * 2;
  const endIndex = Math.min(filtered.length, startIndex + visibleCount);
  const windowed = filtered.slice(startIndex, endIndex);
  const topPadPx = startIndex * ESTIMATED_ROW_PX;
  const bottomPadPx = Math.max(0, totalPx - topPadPx - windowed.length * ESTIMATED_ROW_PX);

  useEffect(() => {
    if (!autoScroll || activeId === undefined) {
      return;
    }
    const container = scrollRef.current;
    if (container === null) {
      return;
    }
    const index = filtered.findIndex((segment) => segment.id === activeId);
    if (index < 0) {
      return;
    }
    container.scrollTop = Math.max(0, index * ESTIMATED_ROW_PX - viewportPx / 2);
  }, [autoScroll, activeId, filtered, viewportPx]);

  function handleKeyDown(event: React.KeyboardEvent<HTMLDivElement>): void {
    if (filtered.length === 0) {
      return;
    }
    const currentIndex = selectedId !== undefined ? filtered.findIndex((s) => s.id === selectedId) : -1;
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      const next =
        event.key === 'ArrowDown'
          ? filtered[Math.min(filtered.length - 1, currentIndex + 1)]
          : filtered[Math.max(0, currentIndex <= 0 ? 0 : currentIndex - 1)];
      if (next !== undefined) {
        onSelect(next.id);
      }
      return;
    }
    if (event.key === 'Enter' && selectedId !== undefined) {
      event.preventDefault();
      onSeek(selectedId);
    }
  }

  return (
    <div
      ref={scrollRef}
      data-testid="transcript-list-scroll"
      role="listbox"
      aria-label="Transcript segments"
      tabIndex={0}
      onKeyDown={handleKeyDown}
      onScroll={(event) => {
        setScrollTop(event.currentTarget.scrollTop);
        onManualScroll();
      }}
      style={{ overflowY: 'auto', maxHeight: `${String(viewportPx)}px` }}
    >
      <div data-testid="transcript-list" data-total={String(filtered.length)} data-rendered={String(windowed.length)}>
        <div style={{ height: `${String(topPadPx)}px` }} aria-hidden="true" />
        {windowed.map((segment) => (
          <SegmentRow
            key={segment.id}
            segment={segment}
            isSelected={segment.id === selectedId}
            isActive={segment.id === activeId}
            onSelect={onSelect}
            onSeek={onSeek}
          />
        ))}
        <div style={{ height: `${String(bottomPadPx)}px` }} aria-hidden="true" />
      </div>
    </div>
  );
}
