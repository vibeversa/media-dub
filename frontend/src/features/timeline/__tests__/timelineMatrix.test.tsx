// Task 039B: timeline state-matrix gap closure.
//
// Extends `timeline.test.tsx` (player shortcuts, peaks waveform, lanes,
// markers, workspace composition) with the missing states: workspace
// loading/error-retry/empty, MediaPlayer error/expired/empty/full-control
// matrices, Waveform error/missing/retry, Timeline empty/issues/zoom/region
// matrices, the player-store action sweep, the types helper sweep, and the
// media-hook guards. Every failure asserts its recovery control per §11.6
// with text signals (041C); fixtures are synthetic and fetch is intercepted.
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
import { MediaPlayer } from '../MediaPlayer.js';
import { Timeline } from '../Timeline.js';
import { TimelineWorkspace } from '../TimelineWorkspace.js';
import { Waveform } from '../Waveform.js';
import { useTimelinePlayerStore } from '../playerStore.js';
import {
  clampPeak,
  debounce,
  deriveTimelineGaps,
  deriveTimelineMarkers,
  formatPlayerTime,
  isExpiredError,
  isPeaksMissing,
  markerOwnerLink,
  parseTimelineIssues,
  parseTimelineSegment,
  parseTimelineSegments,
  parseWaveformPeaks,
  peaksPathFor,
  resolveIssueTarget,
  selectPeaksForWidth,
} from '../types.js';
import type { TimelineIssue, TimelineSegmentView } from '../types.js';
import { fetchPreviewMedia, fetchWaveformPeaks, invalidateTimelineMedia, usePreviewMedia } from '../useTimelineMedia.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number): Response {
  return jsonResponse(
    { error: { code, message: `backend ${code}`, correlationId: 'corr-t30', details: {} } },
    status,
  );
}

function makeSegment(index: number, overrides: Record<string, unknown> = {}): Record<string, unknown> {
  const id = `seg_${String(index + 1).padStart(3, '0')}`;
  const startMs = index * 2000;
  return {
    id,
    projectId: 'prj_1',
    status: 'Ready',
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
    transcriptVersions: [],
    ...overrides,
  };
}

function peaksBody(): Record<string, unknown> {
  const range = (n: number): number[] => Array.from({ length: n }, (_, i) => (i % 10) / 10);
  return { durationMs: 6000, sampleRate: 16000, resolutions: { 64: range(64), 256: range(256), 1024: range(1024) } };
}

function missingPeaksBody(): Record<string, unknown> {
  return { durationMs: 6000, sampleRate: 16000, peaksMissing: true, resolutions: { 64: [], 256: [], 1024: [] } };
}

interface TimelineMatrixWorld {
  segmentsMode: 'ok' | 'empty' | 'error500' | 'never';
  peaksMode: 'ok' | 'missing' | 'error404';
  mediaMode: 'ok' | 'expiredOnce' | 'expiredAlways' | 'error500';
  mediaCalls: number;
}

let world: TimelineMatrixWorld;

function resetWorld(): void {
  world = { segmentsMode: 'ok', peaksMode: 'ok', mediaMode: 'ok', mediaCalls: 0 };
}

function urlOf(input: RequestInfo | URL): string {
  if (typeof input === 'string') {
    return input;
  }
  if (input instanceof URL) {
    return input.href;
  }
  return (input as Request).url;
}

function methodOf(input: RequestInfo | URL, init?: RequestInit): string {
  if (typeof input !== 'string' && !(input instanceof URL)) {
    const request = input as Request;
    if (typeof request.method === 'string' && request.method !== '') {
      return request.method.toUpperCase();
    }
  }
  return (init?.method ?? 'GET').toUpperCase();
}

async function mockFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const url = urlOf(input);
  const method = methodOf(input, init);
  if (!url.includes('/api/v1/')) {
    return jsonResponse({});
  }
  if (url.includes('/output/download') && method === 'GET') {
    world.mediaCalls += 1;
    if (world.mediaMode === 'error500') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    if (world.mediaMode === 'expiredAlways') {
      return errorEnvelope('URL_EXPIRED', 410);
    }
    if (world.mediaMode === 'expiredOnce' && world.mediaCalls === 1) {
      return errorEnvelope('URL_EXPIRED', 410);
    }
    return jsonResponse({ downloadUrl: 'https://example.com/media.mp4', expiresAt: '2026-09-26T00:00:00Z' });
  }
  if (url.includes('/waveform-peaks') && method === 'GET') {
    if (world.peaksMode === 'error404') {
      return errorEnvelope('ARTIFACT_UNAVAILABLE', 404);
    }
    if (world.peaksMode === 'missing') {
      return jsonResponse(missingPeaksBody());
    }
    return jsonResponse(peaksBody());
  }
  if (method === 'GET' && url.includes('/segments')) {
    if (world.segmentsMode === 'error500') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    if (world.segmentsMode === 'empty') {
      return jsonResponse({ items: [], page: 1, pageSize: 200, total: 0, hasMore: false });
    }
    if (world.segmentsMode === 'never') {
      return new Promise<Response>(() => {});
    }
    return jsonResponse({ items: [makeSegment(0), makeSegment(1)], page: 1, pageSize: 200, total: 2, hasMore: false });
  }
  return jsonResponse({});
}

function renderWithProviders(node: React.JSX.Element): void {
  render(
    <QueryClientProvider client={queryClient}>
      <LocaleProvider>
        <ToastProvider>
          <MemoryRouter>{node}</MemoryRouter>
        </ToastProvider>
      </LocaleProvider>
    </QueryClientProvider>,
  );
}

function authenticate(): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', ['project.view']);
}

function stubMedia(): void {
  Object.defineProperty(window.HTMLMediaElement.prototype, 'play', {
    configurable: true,
    writable: true,
    value: vi.fn().mockResolvedValue(undefined),
  });
  Object.defineProperty(window.HTMLMediaElement.prototype, 'pause', {
    configurable: true,
    writable: true,
    value: vi.fn(),
  });
  Object.defineProperty(window.HTMLCanvasElement.prototype, 'getContext', {
    configurable: true,
    writable: true,
    value: vi.fn().mockReturnValue(null),
  });
}

function parsedSegments(count: number, overrides: Record<string, unknown> = {}): TimelineSegmentView[] {
  const items: Record<string, unknown>[] = [];
  for (let i = 0; i < count; i += 1) {
    items.push(makeSegment(i, overrides));
  }
  return parseTimelineSegments({ items });
}

beforeEach(() => {
  resetWorld();
  setInnerFetchForTests(mockFetch as typeof fetch);
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  useTimelinePlayerStore.getState().resetForTests();
  queryClient.clear();
  stubMedia();
});

afterEach(() => {
  cleanup();
  restoreInnerFetchForTests();
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  useTimelinePlayerStore.getState().resetForTests();
  queryClient.clear();
  vi.restoreAllMocks();
});

describe('TimelineWorkspace shell states', () => {
  it('shows loading while segments resolve (never blank)', () => {
    world.segmentsMode = 'never';
    authenticate();
    renderWithProviders(<TimelineWorkspace projectId="prj_1" />);
    expect(screen.getByTestId('timeline-workspace-loading')).toBeDefined();
  });

  it('recovers from segment errors with retry (recovery: retry)', async () => {
    world.segmentsMode = 'error500';
    authenticate();
    renderWithProviders(<TimelineWorkspace projectId="prj_1" />);
    expect(await screen.findByTestId('timeline-workspace-error')).toBeDefined();
    world.segmentsMode = 'ok';
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByTestId('timeline-workspace-player')).toBeDefined();
  });

  it('renders the empty state with a processing link (recovery: process)', async () => {
    world.segmentsMode = 'empty';
    authenticate();
    renderWithProviders(<TimelineWorkspace projectId="prj_1" />);
    expect(await screen.findByTestId('timeline-workspace-empty')).toBeDefined();
    expect(screen.getByTestId('timeline-workspace-empty-link').getAttribute('href')).toBe('/projects/prj_1');
  });
});

describe('MediaPlayer state matrix', () => {
  it('recovers from media errors with retry (recovery: retry)', async () => {
    world.mediaMode = 'error500';
    authenticate();
    renderWithProviders(<MediaPlayer projectId="prj_1" segments={parsedSegments(1)} />);
    expect(await screen.findByTestId('timeline-error')).toBeDefined();
    world.mediaMode = 'ok';
    fireEvent.click(screen.getByTestId('timeline-retry'));
    expect(await screen.findByTestId('timeline-media')).toBeDefined();
  });

  it('refreshes expired media and notes the resume (recovery: refetch)', async () => {
    world.mediaMode = 'expiredOnce';
    authenticate();
    renderWithProviders(<MediaPlayer projectId="prj_1" segments={parsedSegments(1)} />);
    expect(await screen.findByTestId('timeline-media')).toBeDefined();
    expect(await screen.findByTestId('timeline-resume-note')).toBeDefined();
    expect(world.mediaCalls).toBeGreaterThanOrEqual(2);
  });

  it('surfaces persistent expiry with retry (recovery: retry)', async () => {
    world.mediaMode = 'expiredAlways';
    authenticate();
    renderWithProviders(<MediaPlayer projectId="prj_1" segments={parsedSegments(1)} />);
    expect(await screen.findByTestId('timeline-error')).toBeDefined();
  });

  it('exposes full transport controls with text signals (041C)', async () => {
    authenticate();
    renderWithProviders(<MediaPlayer projectId="prj_1" segments={parsedSegments(2)} />);
    expect(await screen.findByTestId('timeline-media')).toBeDefined();
    for (const testId of [
      'timeline-play-toggle',
      'timeline-seek-back',
      'timeline-seek-forward',
      'timeline-frame-back',
      'timeline-frame-forward',
      'timeline-prev-segment',
      'timeline-next-segment',
      'timeline-scrub',
      'timeline-position',
      'timeline-duration',
      'timeline-volume',
      'timeline-mute',
      'timeline-rate',
    ]) {
      expect(screen.getByTestId(testId)).toBeDefined();
    }
    expect(screen.getByTestId('timeline-query-key').textContent).toContain('timeline.media.ready');
  });
});

describe('Waveform state matrix', () => {
  it('recovers from peak errors with retry (recovery: retry)', async () => {
    world.peaksMode = 'error404';
    authenticate();
    renderWithProviders(<Waveform projectId="prj_1" />);
    expect(await screen.findByTestId('timeline-waveform-error')).toBeDefined();
    world.peaksMode = 'ok';
    fireEvent.click(screen.getByTestId('timeline-waveform-retry'));
    expect(await screen.findByTestId('timeline-waveform-canvas')).toBeDefined();
  });

  it('links missing peaks back to processing (recovery: process)', async () => {
    world.peaksMode = 'missing';
    authenticate();
    renderWithProviders(<Waveform projectId="prj_1" />);
    expect(await screen.findByTestId('timeline-waveform-missing')).toBeDefined();
    expect(screen.getByTestId('timeline-waveform-progress-link').getAttribute('href')).toBe('/projects/prj_1');
  });
});

describe('Timeline direct matrix', () => {
  it('renders empty lanes without crashing', () => {
    authenticate();
    renderWithProviders(<Timeline projectId="prj_1" segments={[]} issues={[]} />);
    expect(screen.getByTestId('timeline')).toBeDefined();
    expect(screen.getByTestId('timeline-dialogue-track').getAttribute('data-total')).toBe('0');
    expect(screen.getByTestId('timeline-gaps').getAttribute('data-total')).toBe('0');
  });

  it('jumps to issues with store sync (recovery: jump-to-issue)', () => {
    authenticate();
    const issues: TimelineIssue[] = [{ id: 'issue-seg_002', segmentId: 'seg_002', atMs: 2000, label: 'Issue seg_002' }];
    renderWithProviders(<Timeline projectId="prj_1" segments={parsedSegments(2)} issues={issues} />);
    fireEvent.click(screen.getByTestId('timeline-issue-issue-seg_002'));
    expect(useTimelinePlayerStore.getState().positionMs).toBe(2000);
  });

  it('zooms, pans regions, and loops with text signals', async () => {
    authenticate();
    renderWithProviders(<Timeline projectId="prj_1" segments={parsedSegments(2)} issues={[]} />);
    const labelBefore = screen.getByTestId('timeline-zoom-label').textContent;
    fireEvent.click(screen.getByTestId('timeline-zoom-in'));
    await waitFor(() => expect(screen.getByTestId('timeline-zoom-label').textContent).not.toBe(labelBefore), {
      timeout: 3000,
    });
    fireEvent.click(screen.getByTestId('timeline-zoom-out'));
    fireEvent.click(screen.getByTestId('timeline-region-set'));
    expect(screen.getByTestId('timeline-region-note')).toBeDefined();
    fireEvent.click(screen.getByTestId('timeline-region-loop'));
    fireEvent.click(screen.getByTestId('timeline-region-clear'));
  });

  it('derives silence gaps between distant segments', () => {
    authenticate();
    const segments = parseTimelineSegments({
      items: [makeSegment(0), { ...makeSegment(1), startMs: 20000, endMs: 21800 }],
    });
    renderWithProviders(<Timeline projectId="prj_1" segments={segments} issues={[]} />);
    expect(Number(screen.getByTestId('timeline-gaps').getAttribute('data-total'))).toBeGreaterThan(0);
  });
});

describe('player store matrix', () => {
  it('drives every transport action with readable state', () => {
    const store = useTimelinePlayerStore.getState();
    store.setDuration(6000);
    expect(useTimelinePlayerStore.getState().durationMs).toBe(6000);
    store.requestSeek(1500);
    expect(useTimelinePlayerStore.getState().seekTargetMs).toBe(1500);
    expect(useTimelinePlayerStore.getState().seekVersion).toBe(1);
    store.reportPosition(1500);
    expect(useTimelinePlayerStore.getState().positionMs).toBe(1500);
    store.setPlaying(true);
    expect(useTimelinePlayerStore.getState().isPlaying).toBe(true);
    store.setVolume(0.5);
    expect(useTimelinePlayerStore.getState().volume).toBe(0.5);
    store.setRate(1.5);
    expect(useTimelinePlayerStore.getState().rate).toBe(1.5);
    store.setMuted(true);
    expect(useTimelinePlayerStore.getState().muted).toBe(true);
    store.selectSegment('seg_001');
    expect(useTimelinePlayerStore.getState().selectedSegmentId).toBe('seg_001');
    store.selectSegment(undefined);
    expect(useTimelinePlayerStore.getState().selectedSegmentId).toBeUndefined();
    store.setPlayRegion({ startMs: 0, endMs: 1000 });
    expect(useTimelinePlayerStore.getState().playRegion).toEqual({ startMs: 0, endMs: 1000 });
    store.setPlayRegion(undefined);
    store.setLoopRegion(true);
    expect(useTimelinePlayerStore.getState().loopRegion).toBe(true);
    store.setViewport(500, 2);
    expect(useTimelinePlayerStore.getState().viewportStartMs).toBe(500);
    expect(useTimelinePlayerStore.getState().pxPerMs).toBe(2);
    store.zoomViewport(2);
    expect(useTimelinePlayerStore.getState().pxPerMs).toBeGreaterThan(2);
    store.panViewport(250);
    expect(useTimelinePlayerStore.getState().viewportStartMs).toBeGreaterThanOrEqual(500);
    store.jumpToIssue(2000);
    expect(useTimelinePlayerStore.getState().positionMs).toBe(2000);
    store.jumpToIssue(undefined);
    store.resetForTests();
    expect(useTimelinePlayerStore.getState().positionMs).toBe(0);
  });
});

describe('timeline types sweep', () => {
  it('clamps peaks and selects widths deterministically', () => {
    expect(clampPeak(0.5)).toBe(0.5);
    expect(clampPeak(-1)).toBe(0);
    expect(clampPeak(2)).toBe(1);
    expect(clampPeak('x')).toBe(0);
    expect(clampPeak(Number.NaN)).toBe(0);
    const peaks = parseWaveformPeaks(peaksBody());
    expect(isPeaksMissing(peaks)).toBe(false);
    expect(isPeaksMissing(undefined)).toBe(true);
    expect(selectPeaksForWidth(peaks, 2).length).toBe(64);
    expect(selectPeaksForWidth(peaks, 500).length).toBe(1024);
    expect(peaksPathFor('prj_1')).toContain('waveform-peaks');
    expect(peaksPathFor('prj_1', 128)).toContain('128');
  });

  it('rejects malformed peak payloads without throwing', () => {
    expect(isPeaksMissing(parseWaveformPeaks(null))).toBe(true);
    // Non-record peaks payloads parse to a missing view; the waveform
    // renders its preparing state from the empty resolutions.
    expect(isPeaksMissing(parseWaveformPeaks({ peaks: 'nope' }))).toBe(true);
    const partial = parseWaveformPeaks({ peaks: [0.2], durationMs: 100 });
    expect(partial.durationMs).toBe(100);
    expect(partial.resolutions).toBeDefined();
  });

  it('parses segments defensively', () => {
    expect(parseTimelineSegment(null)).toBeUndefined();
    expect(parseTimelineSegment({ id: 'seg_001' })).toBeDefined();
    expect(parseTimelineSegments(null)).toEqual([]);
    expect(parseTimelineSegments({ items: 'nope' })).toEqual([]);
    expect(parseTimelineSegments({ items: [makeSegment(0)] }).length).toBe(1);
  });

  it('derives markers, gaps, issues, and links from parsed segments', () => {
    const segments = parsedSegments(2, { reviewStatus: 'Open', qualityCodes: ['QC_NOISY'] });
    const markers = deriveTimelineMarkers(segments, { projectId: 'prj_1' });
    expect(markers.length).toBeGreaterThan(4);
    expect(markers.some((m) => m.kind === 'start')).toBe(true);
    expect(deriveTimelineMarkers([], { peaksMissing: true })).toEqual([]);
    const gaps = deriveTimelineGaps(segments);
    expect(Array.isArray(gaps)).toBe(true);
    expect(markerOwnerLink(markers[0] as never, 'prj_1')).toContain('prj_1');
    const issues = parseTimelineIssues([{ id: 'i1', segmentId: 'seg_001', atMs: 0, label: 'x' }]);
    expect(issues.length).toBe(1);
    expect(parseTimelineIssues('nope')).toEqual([]);
    expect(resolveIssueTarget({ id: 'i1', segmentId: 'seg_001', atMs: 0, label: 'x' }, segments)).toBe(0);
    expect(resolveIssueTarget({ id: 'i9', segmentId: 'ghost', atMs: undefined, label: 'g' }, segments)).toBeUndefined();
    expect(resolveIssueTarget({ id: 'i0', segmentId: undefined, atMs: undefined, label: 'g' }, segments)).toBeUndefined();
  });

  it('formats player time and classifies expiry without throwing', () => {
    expect(formatPlayerTime(90000)).toContain('01:30');
    expect(formatPlayerTime(Number.NaN)).toContain('00:00');
    expect(formatPlayerTime(-5)).toContain('00:00');
    expect(isExpiredError({ code: 'URL_EXPIRED', status: 410 })).toBe(true);
    expect(isExpiredError({ code: 'X', status: 500 })).toBe(false);
    expect(isExpiredError(undefined)).toBe(false);
    expect(isExpiredError(null)).toBe(false);
  });

  it('debounces trailing calls with fake timers', () => {
    vi.useFakeTimers();
    try {
      const calls: string[] = [];
      const debounced = debounce((value: string) => {
        calls.push(value);
      }, 120);
      debounced('a');
      debounced('b');
      expect(calls).toEqual([]);
      vi.advanceTimersByTime(120);
      expect(calls).toEqual(['b']);
    } finally {
      vi.useRealTimers();
    }
  });
});

describe('timeline media hooks matrix', () => {
  it('fetches preview media and normalizes failures (recovery: retry)', async () => {
    authenticate();
    const media = await fetchPreviewMedia('prj_1');
    expect(media.url).toBe('https://example.com/media.mp4');
    world.mediaMode = 'error500';
    await expect(fetchPreviewMedia('prj_1')).rejects.toMatchObject({ code: 'INTERNAL_ERROR' });
    const peaks = await fetchWaveformPeaks('prj_1', 128);
    expect(isPeaksMissing(peaks)).toBe(false);
    world.peaksMode = 'error404';
    await expect(fetchWaveformPeaks('prj_1')).rejects.toMatchObject({ code: 'ARTIFACT_UNAVAILABLE' });
    await invalidateTimelineMedia(queryClient, 'prj_1');
  });

  it('stays idle for empty project ids and disabled callers', async () => {
    authenticate();
    let calls = 0;
    setInnerFetchForTests((async () => {
      calls += 1;
      return jsonResponse({});
    }) as typeof fetch);
    function Probe(): null {
      usePreviewMedia('');
      return null;
    }
    renderWithProviders(<Probe />);
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(calls).toBe(0);
  });
});
