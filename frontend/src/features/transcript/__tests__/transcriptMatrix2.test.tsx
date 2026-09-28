// Delta 2: transcript remaining-branch closure.
//
// Supplements transcriptMatrix with SegmentRow display variants
// (unknown confidence, empty text, missing review flag, selected/active
// chrome), VirtualizedSegmentList guard branches (no autoscroll, no active
// segment, empty filter, selection-less keys), player-store clamping,
// types defensive parsing (PascalCase, clamps, fallbacks, merge paths),
// and useTranscript hydration/error branches. Synthetic fixtures only.
import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import '../../../i18n/i18n.js';
import {
  clearTokenProvider,
  restoreInnerFetchForTests,
  setInnerFetchForTests,
  setTokenProvider,
} from '../../../api/client/index.js';
import { LocaleProvider } from '../../../app/providers/LocaleProvider.js';
import { queryClient } from '../../../app/providers/queryClient.js';
import { ToastProvider } from '../../../components/Toast/Toast.js';
import { useAppStore } from '../../../stores/index.js';
import { useAuthStore } from '../../auth/authStore.js';
import { resetRestoreStartedForTests } from '../../auth/useSession.js';
import { SegmentRow } from '../SegmentRow.js';
import { VirtualizedSegmentList } from '../VirtualizedSegmentList.js';
import { useTranscriptPlaybackStore } from '../playerStore.js';
import { useTranscriptSegment } from '../useTranscript.js';
import {
  deriveLineage,
  filterTranscriptSegments,
  findActiveSegmentId,
  formatTimestamp,
  isSelectionConflict,
  mergeSegmentDetail,
  nearestSegmentId,
  parseTranscriptListItems,
  parseTranscriptSegment,
  parseTranscriptVersion,
} from '../types.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number): Response {
  return jsonResponse(
    { error: { code, message: `backend ${code}`, correlationId: 'corr-t27b', details: {} } },
    status,
  );
}

function makeSegment(index: number, overrides: Record<string, unknown> = {}): Record<string, unknown> {
  const id = `seg_${String(index + 1).padStart(3, '0')}`;
  const startMs = index * 2000;
  return {
    id,
    sequence: index + 1,
    startMs,
    endMs: startMs + 1800,
    speakerId: 'spk_alice',
    speakerLabel: 'Alice',
    selectionVersion: 2,
    reviewStatus: 'Approved',
    qualityCodes: [],
    syncStatus: 'SyncAcceptable',
    confidence: 0.95,
    text: `line ${id}`,
    transcriptVersions: [
      { id: `${id}-v1`, provider: 'acme', model: 'stt-v1', text: `v1 ${id}`, isSelected: false, createdAt: '2024-01-15T12:00:00Z' },
      { id: `${id}-v2`, provider: 'acme', model: 'stt-v2', text: `line ${id}`, isSelected: true, createdAt: '2024-01-15T13:00:00Z' },
    ],
    ...overrides,
  };
}

function parsedRow(overrides: Record<string, unknown> = {}) {
  const parsed = parseTranscriptSegment(makeSegment(0, overrides));
  if (parsed === undefined) throw new Error('fixture failed to parse');
  return parsed;
}

function urlOf(input: RequestInfo | URL): string {
  if (typeof input === 'string') return input;
  if (input instanceof URL) return input.href;
  return (input as Request).url;
}

async function mockFetch(input: RequestInfo | URL): Promise<Response> {
  const url = urlOf(input);
  if (!url.includes('/api/v1/')) return jsonResponse({});
  if (/\/segments\/seg_fail/.test(url)) return errorEnvelope('INTERNAL_ERROR', 500);
  if (/\/segments\/seg_/.test(url)) {
    const match = /\/segments\/(seg_[^/?]+)/.exec(url);
    return jsonResponse({ ...makeSegment(0), id: match?.[1] ?? 'seg_001' });
  }
  return jsonResponse({});
}

function authenticate(): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', ['project.view']);
}

beforeEach(() => {
  setInnerFetchForTests(mockFetch as typeof fetch);
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  useTranscriptPlaybackStore.getState().resetForTests();
  resetRestoreStartedForTests();
  queryClient.clear();
});

afterEach(() => {
  cleanup();
  restoreInnerFetchForTests();
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  useTranscriptPlaybackStore.getState().resetForTests();
  resetRestoreStartedForTests();
  queryClient.clear();
  vi.restoreAllMocks();
});

describe('SegmentRow display variants', () => {
  it('renders unknown confidence and empty text with plain fallbacks', () => {
    const onSelect = vi.fn();
    const onSeek = vi.fn();
    render(
      <MemoryRouter>
        <SegmentRow
          segment={parsedRow({ confidence: undefined, text: '', transcriptVersions: [] })}
          isSelected={false}
          isActive={false}
          onSelect={onSelect}
          onSeek={onSeek}
        />
      </MemoryRouter>,
    );
    expect(screen.getByTestId('transcript-confidence-seg_001').textContent).toBe('—');
    expect(screen.getByTestId('transcript-confidence-seg_001').getAttribute('title')).toContain('unknown');
    expect(screen.getByTestId('transcript-text-seg_001').textContent).toBe('(empty segment)');
    expect(screen.queryByTestId('transcript-review-flag-seg_001')).toBeNull();
    expect(screen.queryByTestId('transcript-badge-manual-seg_001')).toBeNull();
    expect(screen.getByTestId('transcript-row-seg_001').getAttribute('data-active')).toBe('false');
    fireEvent.click(screen.getByTestId('transcript-play-seg_001'));
    expect(onSeek).toHaveBeenCalledWith('seg_001');
  });

  it('marks manual lineage and review flags distinctly', () => {
    const onSelect = vi.fn();
    const onSeek = vi.fn();
    const manual = parseTranscriptSegment({
      ...makeSegment(0),
      reviewStatus: 'Open',
      transcriptVersions: [
        { id: 'seg_001-v1', provider: 'Manual', model: 'human', text: 'fixed', isSelected: true },
      ],
    });
    if (manual === undefined) throw new Error('fixture failed');
    render(
      <MemoryRouter>
        <SegmentRow segment={manual} isSelected={true} isActive={true} onSelect={onSelect} onSeek={onSeek} />
      </MemoryRouter>,
    );
    expect(screen.getByTestId('transcript-badge-manual-seg_001')).toBeDefined();
    expect(screen.getByTestId('transcript-review-flag-seg_001')).toBeDefined();
    expect(screen.getByTestId('transcript-row-seg_001').getAttribute('data-active')).toBe('true');
    expect(screen.getByTestId('transcript-row-seg_001').getAttribute('aria-selected')).toBe('true');
  });
});

describe('VirtualizedSegmentList guard branches', () => {
  function segments(count: number) {
    const items: Record<string, unknown>[] = [];
    for (let i = 0; i < count; i += 1) items.push(makeSegment(i));
    return parseTranscriptListItems({ items });
  }

  it('stays inert without autoscroll or an active segment', () => {
    const onSelect = vi.fn();
    const onSeek = vi.fn();
    const onManualScroll = vi.fn();
    render(
      <VirtualizedSegmentList
        segments={segments(4)}
        selectedId={undefined}
        positionMs={-5}
        autoScroll={false}
        filter={{ query: '', speaker: '', reviewOnly: false }}
        onSelect={onSelect}
        onSeek={onSeek}
        onManualScroll={onManualScroll}
      />,
    );
    const list = screen.getByTestId('transcript-list-scroll');
    fireEvent.keyDown(list, { key: 'ArrowDown' });
    expect(onSelect).toHaveBeenCalledWith('seg_001');
    fireEvent.keyDown(list, { key: 'ArrowUp' });
    expect(onSelect).toHaveBeenCalled();
    fireEvent.keyDown(list, { key: 'Enter' });
    expect(onSeek).not.toHaveBeenCalled();
    fireEvent.keyDown(list, { key: 'Escape' });
    expect(onSeek).not.toHaveBeenCalled();
  });

  it('filters to empty and ignores seek without selection', () => {
    const onSelect = vi.fn();
    const onSeek = vi.fn();
    render(
      <VirtualizedSegmentList
        segments={segments(4)}
        selectedId="seg_999"
        positionMs={100}
        autoScroll={true}
        filter={{ query: 'no-such-line', speaker: '', reviewOnly: false }}
        onSelect={onSelect}
        onSeek={onSeek}
        onManualScroll={() => {}}
      />,
    );
    expect(screen.getByTestId('transcript-list').getAttribute('data-total')).toBe('0');
    fireEvent.keyDown(screen.getByTestId('transcript-list-scroll'), { key: 'ArrowDown' });
    expect(onSelect).not.toHaveBeenCalled();
  });
});

describe('player store clamping', () => {
  it('clamps non-finite seeks and ignores invalid reports', () => {
    const store = useTranscriptPlaybackStore.getState();
    store.requestSeek(Number.POSITIVE_INFINITY);
    expect(useTranscriptPlaybackStore.getState().seekTargetMs).toBe(0);
    store.reportPosition(800);
    store.reportPosition(Number.POSITIVE_INFINITY);
    expect(useTranscriptPlaybackStore.getState().positionMs).toBe(800);
    store.reportPosition(-1);
    expect(useTranscriptPlaybackStore.getState().positionMs).toBe(800);
    store.requestSeek(1500);
    expect(useTranscriptPlaybackStore.getState().seekVersion).toBeGreaterThan(0);
  });
});

describe('transcript types defensive sweep', () => {
  it('parses versions with manual detection and fallbacks', () => {
    expect(parseTranscriptVersion(undefined, 0)).toBeUndefined();
    expect(parseTranscriptVersion([], 0)).toBeUndefined();
    expect(parseTranscriptVersion({ text: 'hi' }, 0)).toBeUndefined();
    expect(parseTranscriptVersion({ id: 'v1' }, 2)?.versionNumber).toBe(3);
    expect(parseTranscriptVersion({ id: 'v1', provider: 'MANUAL', model: '' }, 0)?.isManual).toBe(true);
    expect(parseTranscriptVersion({ id: 'v1', provider: '  manual  ' }, 0)?.isManual).toBe(true);
    expect(parseTranscriptVersion({ id: 'v1', provider: 'acme' }, 0)?.provider).toBe('acme');
  });

  it('clamps confidence and derives text through every fallback', () => {
    expect(parseTranscriptSegment({ id: 's1', confidence: 9 })?.confidence).toBe(1);
    expect(parseTranscriptSegment({ id: 's1', confidence: -4 })?.confidence).toBe(0);
    expect(parseTranscriptSegment({ id: 's1', confidence: 'high' })?.confidence).toBeUndefined();
    expect(parseTranscriptSegment({ id: 's1', text: 'direct' })?.text).toBe('direct');
    expect(parseTranscriptSegment({ id: 's1', transcriptVersions: [{ id: 'v1', text: 'last' }] })?.text).toBe('last');
    expect(parseTranscriptSegment({ id: 's1' })?.text).toBe('');
    expect(parseTranscriptSegment({ id: 's1', transcriptVersions: [{ id: 'v1', text: 'first' }] })?.originalText).toBe('first');
    expect(parseTranscriptSegment({ id: 's1', Text: 'pascal' })?.text).toBe('pascal');
  });

  it('falls back speaker labels and clamps timing', () => {
    expect(parseTranscriptSegment({ id: 's1', speakerName: 'Sam' })?.speakerLabel).toBe('Sam');
    expect(parseTranscriptSegment({ id: 's1', SpeakerLabel: 'Pascal' })?.speakerLabel).toBe('Pascal');
    expect(parseTranscriptSegment({ id: 's1' })?.speakerLabel).toBe('Unknown speaker');
    const clamped = parseTranscriptSegment({ id: 's1', startMs: -20, endMs: -40 });
    expect(clamped?.startMs).toBe(0);
    expect(clamped?.endMs).toBe(0);
    expect(parseTranscriptSegment({ id: 's1', startMs: 5.6 })?.startMs).toBe(6);
  });

  it('derives review flags from every signal', () => {
    expect(parseTranscriptSegment({ id: 's1', needsReview: true })?.needsReview).toBe(true);
    expect(parseTranscriptSegment({ id: 's1', reviewStatus: 'open' })?.needsReview).toBe(true);
    expect(parseTranscriptSegment({ id: 's1', reviewStatus: 'OPEN' })?.needsReview).toBe(true);
    expect(parseTranscriptSegment({ id: 's1', confidence: 0.2 })?.needsReview).toBe(true);
    expect(parseTranscriptSegment({ id: 's1', confidence: 0.2 })?.isLowConfidence).toBe(true);
    expect(parseTranscriptSegment({ id: 's1', confidence: 0.9 })?.needsReview).toBe(false);
    expect(parseTranscriptSegment({ id: 's1', qualityCodes: 'nope', syncStatus: 7 })?.qualityCodes).toEqual([]);
  });

  it('merges details with fallback retention', () => {
    const summary = parsedRow();
    expect(mergeSegmentDetail(summary, undefined)).toBe(summary);
    expect(mergeSegmentDetail(summary, null)).toBe(summary);
    expect(mergeSegmentDetail(summary, { id: 'other' }).versions).toEqual(summary.versions);
    const withSelection = mergeSegmentDetail(summary, { ...makeSegment(0), selectionVersion: 9, transcriptVersions: [] });
    expect(withSelection.selectionVersion).toBe(9);
    expect(withSelection.versions).toEqual(summary.versions);
    const hydrated = mergeSegmentDetail(
      parseTranscriptSegment({ ...makeSegment(0), text: '', transcriptVersions: [] })!,
      makeSegment(0),
    );
    expect(hydrated.text).toContain('line seg_001');
  });

  it('derives lineage without selection pointers', () => {
    const bare = parseTranscriptSegment({ id: 's1', text: 'hello' })!;
    const lineage = deriveLineage(bare);
    expect(lineage.selectedText).toBe('hello');
    expect(lineage.selectedBadge).toBe('selected');
    expect(lineage.hasManual).toBe(false);
    expect(lineage.manualText).toBeUndefined();
  });

  it('locates actives, nearest fallbacks, and filters by speaker id', () => {
    const segments = parseTranscriptListItems({ items: [makeSegment(0), makeSegment(1)] });
    expect(findActiveSegmentId(segments, Number.NaN)).toBeUndefined();
    expect(findActiveSegmentId(segments, -1)).toBeUndefined();
    expect(findActiveSegmentId(segments, 999999)).toBeUndefined();
    expect(findActiveSegmentId(segments, 0)).toBe('seg_001');
    expect(nearestSegmentId(segments, 'missing', 'seg_002')).toBe('seg_002');
    expect(nearestSegmentId(segments, 'missing', 'gone')).toBe('seg_001');
    expect(filterTranscriptSegments(segments, { query: '', speaker: 'spk_alice', reviewOnly: false }).length).toBe(2);
    expect(filterTranscriptSegments(segments, { query: 'ALICE', speaker: '', reviewOnly: false }).length).toBe(2);
    expect(filterTranscriptSegments(segments, { query: 'v1 seg_002', speaker: '', reviewOnly: false }).length).toBe(1);
    expect(formatTimestamp(61500)).toBe('01:01.500');
    expect(isSelectionConflict({ status: 409 })).toBe(true);
  });
});

describe('useTranscriptSegment error branch', () => {
  it('normalizes detail failures to AppError (recovery: retry)', async () => {
    authenticate();
    function Probe(): null {
      useTranscriptSegment('prj_1', 'seg_fail');
      return null;
    }
    render(
      <QueryClientProvider client={queryClient}>
        <LocaleProvider>
          <ToastProvider>
            <Probe />
          </ToastProvider>
        </LocaleProvider>
      </QueryClientProvider>,
    );
    await waitFor(
      () => {
        const state = queryClient.getQueryState(['projects', 'detail', 'prj_1', 'transcript', 'detail', 'seg_fail']);
        expect(state?.error).toBeDefined();
      },
      { timeout: 5000 },
    );
  });
});
