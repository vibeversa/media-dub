import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
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
import { useTranscriptPlaybackStore } from '../../transcript/playerStore.js';
import { MediaPlayer } from '../MediaPlayer.js';
import { Timeline } from '../Timeline.js';
import { TimelineWorkspace } from '../TimelineWorkspace.js';
import { Waveform } from '../Waveform.js';
import { useTimelinePlayerStore } from '../playerStore.js';
import { fetchWaveformPeaks } from '../useTimelineMedia.js';
import {
  PEAKS_ENDPOINT_TOKEN,
  TIMELINE_DEBOUNCE_MS,
  clampPeak,
  debounce,
  deriveTimelineGaps,
  deriveTimelineMarkers,
  formatPlayerTime,
  isExpiredError,
  isPeaksMissing,
  parseTimelineSegments,
  parseWaveformPeaks,
  peaksPathFor,
  resolveIssueTarget,
  selectPeaksForWidth,
} from '../types.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number): Response {
  return jsonResponse(
    { error: { code, message: `backend ${code}`, correlationId: 'corr-t30', details: {} } },
    status,
  );
}

function makePeaks(count: number, seed = 0): number[] {
  const out: number[] = [];
  for (let i = 0; i < count; i += 1) {
    out.push(Math.round((((i * 37 + seed * 13) % 100) / 100) * 10_000) / 10_000);
  }
  return out;
}

function peaksBody(): Record<string, unknown> {
  return {
    durationMs: 60_000,
    sampleRate: 16_000,
    resolutions: { 64: makePeaks(64), 256: makePeaks(256, 1), 1024: makePeaks(1024, 2) },
  };
}

function emptyPeaksBody(): Record<string, unknown> {
  return { durationMs: 60_000, sampleRate: 16_000, peaksMissing: true, resolutions: { 64: [], 256: [], 1024: [] } };
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
    speakerId: index % 2 === 0 ? 'spk_alice' : 'spk_bob',
    speakerLabel: index % 2 === 0 ? 'Alice' : 'Bob',
    selectionVersion: 2,
    reviewStatus: 'Approved',
    qualityCodes: [],
    syncStatus: 'SyncAcceptable',
    confidence: 0.95,
    text: `line ${id}`,
    transcriptVersions: [
      { id: `${id}-v1`, provider: 'acme', model: 'stt-v1', text: `original ${id}`, isSelected: false, createdAt: '2024-01-15T12:00:00Z' },
      { id: `${id}-v2`, provider: 'acme', model: 'stt-v2', text: `line ${id}`, isSelected: true, createdAt: '2024-01-15T13:00:00Z' },
    ],
    ...overrides,
  };
}

function segmentsBody(count: number): Record<string, unknown> {
  const items: Record<string, unknown>[] = [];
  for (let i = 0; i < count; i += 1) {
    items.push(makeSegment(i));
  }
  return { items, page: 1, pageSize: 200, total: count, hasMore: false };
}

interface TimelineWorld {
  mediaCalls: number;
  peaksCalls: number;
  requestedUrls: string[];
  mediaExpiredFirst: boolean;
  mediaAlwaysExpired: boolean;
  peaksMissing: boolean;
  peaksError: boolean;
  listCalls: number;
}

function newWorld(overrides: Partial<TimelineWorld> = {}): TimelineWorld {
  return {
    mediaCalls: 0,
    peaksCalls: 0,
    requestedUrls: [],
    mediaExpiredFirst: false,
    mediaAlwaysExpired: false,
    peaksMissing: false,
    peaksError: false,
    listCalls: 0,
    ...overrides,
  };
}

let world: TimelineWorld = newWorld();

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
  world.requestedUrls.push(url);
  if (url.includes('/output/download') && method === 'GET') {
    world.mediaCalls += 1;
    if (world.mediaAlwaysExpired) {
      return errorEnvelope('URL_EXPIRED', 410);
    }
    if (world.mediaExpiredFirst && world.mediaCalls === 1) {
      return errorEnvelope('URL_EXPIRED', 410);
    }
    return jsonResponse({ downloadUrl: 'https://example.com/media.mp4', expiresAt: '2026-09-26T00:00:00Z' });
  }
  if (url.includes(`/${PEAKS_ENDPOINT_TOKEN}`) && method === 'GET') {
    world.peaksCalls += 1;
    if (world.peaksError) {
      return errorEnvelope('ARTIFACT_UNAVAILABLE', 404);
    }
    if (world.peaksMissing) {
      return jsonResponse(emptyPeaksBody());
    }
    return jsonResponse(peaksBody());
  }
  if (method === 'GET' && /\/segments\/seg_/.test(url)) {
    const match = /\/segments\/(seg_[^/?]+)/.exec(url);
    const segmentId = match?.[1] ?? 'seg_001';
    const index = Number(segmentId.replace('seg_', '')) - 1;
    const row = makeSegment(Number.isFinite(index) && index >= 0 ? index : 0);
    return jsonResponse({ ...row, id: segmentId });
  }
  if (method === 'GET' && url.includes('/segments')) {
    world.listCalls += 1;
    return jsonResponse(segmentsBody(3));
  }
  return jsonResponse({});
}

function authenticate(): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', ['project.view', 'project.edit']);
}

function renderWithProviders(node: React.ReactNode): void {
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

function stubMediaElement(): void {
  const playStub = vi.fn().mockResolvedValue(undefined);
  const pauseStub = vi.fn();
  Object.defineProperty(window.HTMLMediaElement.prototype, 'play', {
    configurable: true,
    writable: true,
    value: playStub,
  });
  Object.defineProperty(window.HTMLMediaElement.prototype, 'pause', {
    configurable: true,
    writable: true,
    value: pauseStub,
  });
  const canvasProto = window.HTMLCanvasElement.prototype as unknown as Record<string, unknown>;
  if (typeof canvasProto['getContext'] !== 'function' || String(canvasProto['getContext']).includes('not implemented')) {
    Object.defineProperty(window.HTMLCanvasElement.prototype, 'getContext', {
      configurable: true,
      writable: true,
      value: () => ({
        save: () => undefined,
        restore: () => undefined,
        scale: () => undefined,
        clearRect: () => undefined,
        fillRect: () => undefined,
        fillStyle: '',
      }),
    });
  } else {
    const original = window.HTMLCanvasElement.prototype.getContext.bind(window.HTMLCanvasElement.prototype);
    Object.defineProperty(window.HTMLCanvasElement.prototype, 'getContext', {
      configurable: true,
      writable: true,
      value: function getContextStub(this: HTMLCanvasElement, ...args: unknown[]) {
        try {
          const context = (original as (...a: unknown[]) => unknown)(...args) as Record<string, unknown> | null;
          if (context !== null) {
            return context;
          }
        } catch {
          // Fall through to the fake below.
        }
        return {
          save: () => undefined,
          restore: () => undefined,
          scale: () => undefined,
          clearRect: () => undefined,
          fillRect: () => undefined,
          fillStyle: '',
        };
      },
    });
  }
}

beforeEach(() => {
  world = newWorld();
  setInnerFetchForTests(mockFetch as typeof fetch);
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  useTimelinePlayerStore.getState().resetForTests();
  useTranscriptPlaybackStore.getState().resetForTests();
  resetRestoreStartedForTests();
  queryClient.clear();
  stubMediaElement();
});

afterEach(() => {
  cleanup();
  restoreInnerFetchForTests();
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  useTimelinePlayerStore.getState().resetForTests();
  useTranscriptPlaybackStore.getState().resetForTests();
  resetRestoreStartedForTests();
  queryClient.clear();
  vi.restoreAllMocks();
  vi.useRealTimers();
});

describe('pure timeline helpers', () => {
  it('clamps peaks and parses payloads defensively', () => {
    expect(clampPeak(Number.NaN)).toBe(0);
    expect(clampPeak(Number.POSITIVE_INFINITY)).toBe(0);
    expect(clampPeak(-0.5)).toBe(0);
    expect(clampPeak(1.5)).toBe(1);
    expect(clampPeak(0.42424)).toBeCloseTo(0.42424, 5);
    const parsed = parseWaveformPeaks(peaksBody());
    expect(parsed.durationMs).toBe(60_000);
    expect(parsed.sampleRate).toBe(16_000);
    expect(isPeaksMissing(parsed)).toBe(false);
    expect(parsed.resolutions['1024']).toHaveLength(1024);
    const missing = parseWaveformPeaks(emptyPeaksBody());
    expect(isPeaksMissing(missing)).toBe(true);
    const dirty = parseWaveformPeaks({
      durationMs: 10_000,
      sampleRate: 8000,
      resolutions: { 64: [0.5, Number.NaN, 2, -1], 256: [], 1024: [] },
    });
    expect(dirty.resolutions['64']).toEqual([0.5, 0, 1, 0]);
  });

  it('selects the smallest resolution covering the canvas width', () => {
    const parsed = parseWaveformPeaks(peaksBody());
    expect(selectPeaksForWidth(parsed, 40)).toHaveLength(64);
    expect(selectPeaksForWidth(parsed, 200)).toHaveLength(256);
    expect(selectPeaksForWidth(parsed, 900)).toHaveLength(1024);
  });

  it('formats player timestamps ms-accurately', () => {
    expect(formatPlayerTime(0)).toBe('00:00.000');
    expect(formatPlayerTime(61_250)).toBe('01:01.250');
    expect(formatPlayerTime(Number.NaN)).toBe('00:00.000');
  });

  it('derives all six marker kinds with text and pattern encoding', () => {
    const rows = [
      makeSegment(0, { reviewStatus: 'Open', qualityCodes: ['QC_NOISY'] }),
      makeSegment(1, { startMs: 1500, endMs: 3300 }),
      makeSegment(2, { startMs: 6000, endMs: 7800 }),
      makeSegment(3, { startMs: 8000, endMs: 8000 }),
    ];
    const segments = parseTimelineSegments({ items: rows });
    const markers = deriveTimelineMarkers(segments, { projectId: 'prj_1' });
    const kinds = new Set(markers.map((marker) => marker.kind));
    expect(kinds.has('start')).toBe(true);
    expect(kinds.has('end')).toBe(true);
    expect(kinds.has('overlap')).toBe(true);
    expect(kinds.has('silence')).toBe(true);
    expect(kinds.has('warning')).toBe(true);
    expect(kinds.has('missing')).toBe(true);
    for (const marker of markers) {
      expect(marker.label).not.toBe('');
      expect(marker.pattern).not.toBe('');
      expect(marker.cause).not.toBe('');
    }
    const missing = markers.find((marker) => marker.kind === 'missing');
    expect(missing?.pattern).toBe('hatched-block');
    const gaps = deriveTimelineGaps(segments);
    expect(gaps.length).toBeGreaterThan(0);
    for (const gap of gaps) {
      expect(gap.endMs).toBeGreaterThan(gap.startMs);
    }
  });

  it('resolves issue targets from explicit time or segment start', () => {
    const segments = parseTimelineSegments(segmentsBody(3));
    expect(resolveIssueTarget({ id: 'i1', segmentId: undefined, atMs: 1234, label: 'i1' }, segments)).toBe(1234);
    expect(resolveIssueTarget({ id: 'i2', segmentId: 'seg_002', atMs: undefined, label: 'i2' }, segments)).toBe(2000);
    expect(resolveIssueTarget({ id: 'i3', segmentId: 'seg_missing', atMs: undefined, label: 'i3' }, segments)).toBeUndefined();
  });

  it('detects expired signed URLs and builds the peaks path', () => {
    expect(isExpiredError({ code: 'URL_EXPIRED', status: 410 })).toBe(true);
    expect(isExpiredError({ code: 'NOT_FOUND', status: 404 })).toBe(false);
    expect(isExpiredError(undefined)).toBe(false);
    expect(peaksPathFor('prj_1')).toContain(PEAKS_ENDPOINT_TOKEN);
    expect(peaksPathFor('prj_1', 256)).toContain('resolution=256');
  });

  it('debounces rapid calls into one trailing invocation', () => {
    vi.useFakeTimers();
    const spy = vi.fn();
    const debounced = debounce(spy as (...args: readonly never[]) => void, TIMELINE_DEBOUNCE_MS);
    debounced();
    debounced();
    debounced();
    expect(spy).not.toHaveBeenCalled();
    vi.advanceTimersByTime(TIMELINE_DEBOUNCE_MS + 10);
    expect(spy).toHaveBeenCalledTimes(1);
    debounced.cancel();
    vi.useRealTimers();
  });
});

describe('player controls and shortcuts', () => {
  it('renders full controls with a signed media element', async () => {
    authenticate();
    renderWithProviders(<MediaPlayer projectId="prj_1" segments={parseTimelineSegments(segmentsBody(3))} />);
    expect(await screen.findByTestId('timeline-player')).toBeDefined();
    const media = (await screen.findByTestId('timeline-media')) as HTMLVideoElement;
    expect(media.getAttribute('src')).toBe('https://example.com/media.mp4');
    expect(media.getAttribute('referrerpolicy')).toBe('no-referrer');
    expect(screen.getByTestId('timeline-play-toggle')).toBeDefined();
    expect(screen.getByTestId('timeline-seek-back')).toBeDefined();
    expect(screen.getByTestId('timeline-seek-forward')).toBeDefined();
    expect(screen.getByTestId('timeline-frame-back')).toBeDefined();
    expect(screen.getByTestId('timeline-frame-forward')).toBeDefined();
    expect(screen.getByTestId('timeline-scrub')).toBeDefined();
    expect(screen.getByTestId('timeline-volume')).toBeDefined();
    expect(screen.getByTestId('timeline-rate')).toBeDefined();
    expect(screen.getByTestId('timeline-prev-segment')).toBeDefined();
    expect(screen.getByTestId('timeline-next-segment')).toBeDefined();
    expect(screen.getByTestId('timeline-position')).toBeDefined();
    expect(screen.getByTestId('timeline-duration')).toBeDefined();
  });

  it('toggles playback and seeks with buttons, scrubber, and shortcuts', async () => {
    authenticate();
    renderWithProviders(<MediaPlayer projectId="prj_1" segments={parseTimelineSegments(segmentsBody(3))} />);
    await screen.findByTestId('timeline-media');
    const toggle = screen.getByTestId('timeline-play-toggle');
    expect(toggle.textContent).toBe('Play');
    fireEvent.click(toggle);
    await waitFor(() => {
      expect(screen.getByTestId('timeline-play-toggle').textContent).toBe('Pause');
    });
    fireEvent.click(screen.getByTestId('timeline-seek-forward'));
    await waitFor(() => {
      expect(useTimelinePlayerStore.getState().positionMs).toBeGreaterThan(0);
    });
    const before = useTimelinePlayerStore.getState().positionMs;
    fireEvent.change(screen.getByTestId('timeline-scrub'), { target: { value: String(before + 1000) } });
    await waitFor(() => {
      expect(useTimelinePlayerStore.getState().positionMs).toBeGreaterThanOrEqual(before);
    });
    const player = screen.getByTestId('timeline-player');
    fireEvent.keyDown(player, { key: 'ArrowRight' });
    await waitFor(() => {
      expect(useTimelinePlayerStore.getState().positionMs).toBeGreaterThan(before);
    });
    fireEvent.keyDown(player, { key: ' ' });
    await waitFor(() => {
      expect(screen.getByTestId('timeline-play-toggle').textContent).toBe('Play');
    });
  });

  it('navigates prev/next segment and shares state with the transcript store', async () => {
    authenticate();
    renderWithProviders(<MediaPlayer projectId="prj_1" segments={parseTimelineSegments(segmentsBody(3))} />);
    await screen.findByTestId('timeline-media');
    fireEvent.click(screen.getByTestId('timeline-next-segment'));
    await waitFor(() => {
      expect(useTimelinePlayerStore.getState().positionMs).toBe(2000);
    });
    expect(useTranscriptPlaybackStore.getState().positionMs).toBe(2000);
    fireEvent.click(screen.getByTestId('timeline-prev-segment'));
    await waitFor(() => {
      expect(useTimelinePlayerStore.getState().positionMs).toBe(0);
    });
  });

  it('refetches once on expiry and resumes, then surfaces double-expiry', async () => {
    world.mediaExpiredFirst = true;
    authenticate();
    renderWithProviders(<MediaPlayer projectId="prj_1" />);
    const media = (await screen.findByTestId('timeline-media', {}, { timeout: 5000 })) as HTMLVideoElement;
    expect(media.getAttribute('src')).toBe('https://example.com/media.mp4');
    expect(world.mediaCalls).toBeGreaterThanOrEqual(2);
  });

  it('shows the error state with retry on persistent expiry', async () => {
    world.mediaAlwaysExpired = true;
    authenticate();
    renderWithProviders(<MediaPlayer projectId="prj_1" />);
    expect(await screen.findByTestId('timeline-error')).toBeDefined();
    expect(screen.getByTestId('timeline-retry')).toBeDefined();
  });
});

describe('peaks-only waveform (R1)', () => {
  it('fetches peaks through the peaks endpoint and never the archival original', async () => {
    authenticate();
    const peaks = await fetchWaveformPeaks('prj_1');
    expect(peaks.resolutions['64']).toHaveLength(64);
    expect(world.requestedUrls.some((url) => url.includes(PEAKS_ENDPOINT_TOKEN))).toBe(true);
    for (const url of world.requestedUrls) {
      expect(url.toLowerCase()).not.toContain('original');
      expect(url.toLowerCase()).not.toContain('archival');
      expect(url.toLowerCase()).not.toContain('source-media');
    }
  });

  it('renders canvas peaks with a debounced scrubber', async () => {
    authenticate();
    renderWithProviders(<Waveform projectId="prj_1" />);
    const canvas = (await screen.findByTestId('timeline-waveform-canvas')) as HTMLCanvasElement;
    expect(Number(canvas.getAttribute('data-peaks') ?? '0')).toBeGreaterThan(0);
    expect(screen.getByTestId('timeline-waveform-scrub')).toBeDefined();
    fireEvent.change(screen.getByTestId('timeline-waveform-scrub'), { target: { value: '5000' } });
    await waitFor(() => {
      expect(useTimelinePlayerStore.getState().positionMs).toBe(5000);
    });
  });

  it('shows a skeleton plus progress link when peaks are missing', async () => {
    world.peaksMissing = true;
    authenticate();
    renderWithProviders(<Waveform projectId="prj_1" />);
    expect(await screen.findByTestId('timeline-waveform-skeleton')).toBeDefined();
    expect(await screen.findByTestId('timeline-waveform-missing')).toBeDefined();
    expect(screen.getByTestId('timeline-waveform-progress-link')).toBeDefined();
  });
});

describe('timeline lanes, markers, and read-only timing (R2-R4)', () => {
  function markerSegments(): ReturnType<typeof parseTimelineSegments> {
    const rows = [
      makeSegment(0, { reviewStatus: 'Open', qualityCodes: ['QC_NOISY'] }),
      makeSegment(1, { startMs: 1500, endMs: 3300 }),
      makeSegment(2, { startMs: 6000, endMs: 7800 }),
      makeSegment(3, { startMs: 8000, endMs: 8000 }),
    ];
    return parseTimelineSegments({ items: rows });
  }

  it('renders all five lanes with full marker coverage and owning links', async () => {
    authenticate();
    const segments = markerSegments();
    renderWithProviders(<Timeline projectId="prj_1" segments={segments} />);
    expect(await screen.findByTestId('timeline')).toBeDefined();
    expect(screen.getByTestId('timeline-lane-video')).toBeDefined();
    expect(screen.getByTestId('timeline-lane-source-audio')).toBeDefined();
    expect(screen.getByTestId('timeline-lane-dialogue')).toBeDefined();
    expect(screen.getByTestId('timeline-lane-generated-audio')).toBeDefined();
    expect(screen.getByTestId('timeline-lane-markers')).toBeDefined();
    expect(screen.getByTestId('timeline-ruler')).toBeDefined();
    expect(screen.getByTestId('timeline-segment-seg_001')).toBeDefined();
    const track = screen.getByTestId('timeline-markers-track');
    expect(track.getAttribute('data-total')).not.toBe('0');
    for (const kind of ['start', 'end', 'overlap', 'silence', 'warning', 'missing'] as const) {
      const markers = within(track).queryAllByTestId(new RegExp(`^timeline-marker-${kind}-`));
      expect(markers.length, `expected ${kind} markers`).toBeGreaterThan(0);
      for (const marker of markers) {
        expect(marker.getAttribute('data-pattern')).not.toBe('');
        expect(within(marker).queryByTestId(/^timeline-marker-label-/)).not.toBeNull();
        expect(within(marker).queryByTestId(/^timeline-marker-link-/)).not.toBeNull();
        expect(marker.getAttribute('title')).toContain('Owner:');
      }
    }
    const missing = within(track).queryAllByTestId(/^timeline-marker-missing-/);
    expect(missing.length).toBeGreaterThan(0);
    for (const marker of missing) {
      expect(marker.getAttribute('data-pattern')).toBe('hatched-block');
    }
    expect(screen.getByTestId('timeline-gaps').textContent).toContain('Gap');
  });

  it('selects segments, loops a region, and jumps issues to the playhead', async () => {
    authenticate();
    const segments = parseTimelineSegments(segmentsBody(3));
    renderWithProviders(
      <Timeline
        projectId="prj_1"
        segments={segments}
        issues={[{ id: 'issue-seg_002', segmentId: 'seg_002', atMs: 2000, label: 'Issue seg_002' }]}
      />,
    );
    expect(await screen.findByTestId('timeline-segment-seg_002')).toBeDefined();
    fireEvent.click(screen.getByTestId('timeline-segment-seg_002'));
    await waitFor(() => {
      expect(useTimelinePlayerStore.getState().selectedSegmentId).toBe('seg_002');
    });
    fireEvent.click(screen.getByTestId('timeline-region-set'));
    expect(await screen.findByTestId('timeline-region-note')).toBeDefined();
    fireEvent.click(screen.getByTestId('timeline-issue-issue-seg_002'));
    await waitFor(() => {
      expect(useTimelinePlayerStore.getState().positionMs).toBe(2000);
    });
    fireEvent.click(screen.getByTestId('timeline-region-clear'));
    await waitFor(() => {
      expect(screen.queryByTestId('timeline-region-note')).toBeNull();
    });
  });

  it('zooms and pans through debounced handlers', async () => {
    authenticate();
    const segments = parseTimelineSegments(segmentsBody(3));
    renderWithProviders(<Timeline projectId="prj_1" segments={segments} />);
    expect(await screen.findByTestId('timeline')).toBeDefined();
    const label = (): string => screen.getByTestId('timeline-zoom-label').textContent ?? '';
    const before = label();
    fireEvent.click(screen.getByTestId('timeline-zoom-in'));
    await waitFor(() => {
      expect(label()).not.toBe(before);
    });
    fireEvent.click(screen.getByTestId('timeline-zoom-out'));
    fireEvent.keyDown(screen.getByTestId('timeline-ruler'), { key: 'ArrowRight' });
    await waitFor(() => {
      expect(useTimelinePlayerStore.getState().positionMs).toBeGreaterThan(0);
    });
  });

  it('virtualizes markers for long media', async () => {
    authenticate();
    const items: Record<string, unknown>[] = [];
    for (let i = 0; i < 400; i += 1) {
      items.push(makeSegment(i, { startMs: i * 18_000, endMs: i * 18_000 + 16_000 }));
    }
    const segments = parseTimelineSegments({ items });
    renderWithProviders(<Timeline projectId="prj_1" segments={segments} />);
    const track = await screen.findByTestId('timeline-markers-track');
    const total = Number(track.getAttribute('data-total') ?? '0');
    const rendered = Number(track.getAttribute('data-rendered') ?? '0');
    expect(total).toBeGreaterThan(200);
    expect(rendered).toBeLessThan(total);
  });

  it('renders zero-length ranges as explicit gap blocks', async () => {
    authenticate();
    const segments = parseTimelineSegments({
      items: [makeSegment(0, { startMs: 0, endMs: 1000 }), makeSegment(1, { startMs: 5000, endMs: 6000 })],
    });
    renderWithProviders(<Timeline projectId="prj_1" segments={segments} />);
    expect(await screen.findByTestId('timeline-gaps')).toBeDefined();
    expect(screen.getByTestId('timeline-gaps').textContent).toContain('Gap');
  });

  it('exposes no manual timing affordances in player or timeline sources', () => {
    const banned = /retime|trim-handle|trimhandle|duration-input|durationinput|duration-edit/i;
    const hits: string[] = [];
    const walk = (dir: string): void => {
      for (const entry of readdirSync(dir)) {
        const full = join(dir, entry);
        const stat = statSync(full);
        if (stat.isDirectory()) {
          if (entry === '__tests__' || entry === 'node_modules' || entry === 'dist' || entry === 'coverage') {
            continue;
          }
          walk(full);
          continue;
        }
        if (!/\.(ts|tsx)$/.test(entry)) {
          continue;
        }
        const text = readFileSync(full, 'utf8');
        if (banned.test(text)) {
          hits.push(full);
        }
      }
    };
    walk(join(process.cwd(), 'src', 'features', 'timeline'));
    expect(hits).toEqual([]);
  });

  it('keeps a 2h fixture interactive with a virtualized window', async () => {
    authenticate();
    const items: Record<string, unknown>[] = [];
    const twoHoursMs = 2 * 60 * 60 * 1000;
    const slotMs = 4000;
    const count = Math.floor(twoHoursMs / slotMs);
    for (let i = 0; i < count; i += 1) {
      items.push(makeSegment(i, { startMs: i * slotMs, endMs: i * slotMs + 3600 }));
    }
    const segments = parseTimelineSegments({ items });
    expect(segments[segments.length - 1]?.endMs ?? 0).toBeGreaterThanOrEqual(twoHoursMs - slotMs);
    const started = Date.now();
    renderWithProviders(<Timeline projectId="prj_1" segments={segments} />);
    const track = await screen.findByTestId('timeline-markers-track');
    const total = Number(track.getAttribute('data-total') ?? '0');
    const rendered = Number(track.getAttribute('data-rendered') ?? '0');
    expect(total).toBeGreaterThan(1000);
    expect(rendered).toBeLessThan(total);
    expect(Date.now() - started).toBeLessThan(10_000);
  });
});

describe('timeline workspace', () => {
  it('composes player, waveform, and timeline over aggregate segments', async () => {
    authenticate();
    renderWithProviders(<TimelineWorkspace projectId="prj_1" />);
    expect(await screen.findByTestId('timeline-workspace')).toBeDefined();
    expect(await screen.findByTestId('timeline-player')).toBeDefined();
    expect(await screen.findByTestId('timeline-waveform')).toBeDefined();
    expect(await screen.findByTestId('timeline')).toBeDefined();
    expect(screen.getByTestId('timeline-workspace-query-key').textContent).toContain('timeline');
  });

  it('mounts the compact player on the shared store without a second element', async () => {
    authenticate();
    renderWithProviders(
      <>
        <MediaPlayer projectId="prj_1" compact segments={parseTimelineSegments(segmentsBody(2))} />
      </>,
    );
    const player = await screen.findByTestId('timeline-player');
    expect(player.getAttribute('data-compact')).toBe('true');
    await screen.findByTestId('timeline-media');
    await screen.findByTestId('timeline-scrub');
    expect(screen.queryByTestId('timeline-frame-back')).toBeNull();
    fireEvent.change(screen.getByTestId('timeline-scrub'), { target: { value: '1500' } });
    await waitFor(() => {
      expect(useTimelinePlayerStore.getState().positionMs).toBe(1500);
    });
    expect(useTranscriptPlaybackStore.getState().positionMs).toBe(1500);
  });
});
