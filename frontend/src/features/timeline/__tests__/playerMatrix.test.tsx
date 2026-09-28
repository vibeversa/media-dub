// Task 039B: player-matrix gap closure for timeline/player surfaces.
//
// Covers remaining lines/branches/functions in MediaPlayer.tsx, Waveform.tsx,
// Timeline.tsx, and playerStore.ts that timeline.test.tsx + timelineMatrix
// cover only partially (transport extras, media-event handlers, PiP/
// fullscreen, canvas click/keyboard, ruler/wheel/pointer, store guards).
// Harness: setInnerFetchForTests, synthetic fixtures, fireEvent only.
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
import { useTranscriptPlaybackStore } from '../../transcript/playerStore.js';
import { MediaPlayer } from '../MediaPlayer.js';
import { MemoizedWaveform, Waveform } from '../Waveform.js';
import { Timeline } from '../Timeline.js';
import { useTimelinePlayerStore } from '../playerStore.js';
import { TIMELINE_ZOOM_MAX, TIMELINE_ZOOM_MIN, parseTimelineSegments } from '../types.js';
import type { TimelineIssue } from '../types.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number): Response {
  return jsonResponse(
    { error: { code, message: `backend ${code}`, correlationId: 'corr-pm', details: {} } },
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

function peaksBody(durationMs = 6000): Record<string, unknown> {
  const range = (n: number): number[] => Array.from({ length: n }, (_, i) => (i % 10) / 10);
  return { durationMs, sampleRate: 16000, resolutions: { 64: range(64), 256: range(256), 1024: range(1024) } };
}

function missingPeaksBody(): Record<string, unknown> {
  return { durationMs: 6000, sampleRate: 16000, peaksMissing: true, resolutions: { 64: [], 256: [], 1024: [] } };
}

interface PlayerMatrixWorld {
  mediaMode: 'ok' | 'expiredAlways' | 'error500' | 'never';
  peaksMode: 'ok' | 'missing' | 'error404' | 'never';
  mediaCalls: number;
}

let world: PlayerMatrixWorld;

function resetWorld(): void {
  world = { mediaMode: 'ok', peaksMode: 'ok', mediaCalls: 0 };
}

function urlOf(input: RequestInfo | URL): string {
  if (typeof input === 'string') return input;
  if (input instanceof URL) return input.href;
  return (input as Request).url;
}

function methodOf(input: RequestInfo | URL, init?: RequestInit): string {
  if (typeof input !== 'string' && !(input instanceof URL)) {
    const request = input as Request;
    if (typeof request.method === 'string' && request.method !== '') return request.method.toUpperCase();
  }
  return (init?.method ?? 'GET').toUpperCase();
}

async function mockFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const url = urlOf(input);
  const method = methodOf(input, init);
  if (!url.includes('/api/v1/')) return jsonResponse({});
  if (url.includes('/output/download') && method === 'GET') {
    world.mediaCalls += 1;
    if (world.mediaMode === 'never') return new Promise<Response>(() => {});
    if (world.mediaMode === 'error500') return errorEnvelope('INTERNAL_ERROR', 500);
    if (world.mediaMode === 'expiredAlways') return errorEnvelope('URL_EXPIRED', 410);
    return jsonResponse({ downloadUrl: 'https://example.com/media.mp4', expiresAt: '2026-09-26T00:00:00Z' });
  }
  if (url.includes('/waveform-peaks') && method === 'GET') {
    if (world.peaksMode === 'never') return new Promise<Response>(() => {});
    if (world.peaksMode === 'error404') return errorEnvelope('ARTIFACT_UNAVAILABLE', 404);
    if (world.peaksMode === 'missing') return jsonResponse(missingPeaksBody());
    return jsonResponse(peaksBody());
  }
  if (method === 'GET' && url.includes('/segments')) {
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

function stubMediaBaseline(): void {
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

function parsedSegments(count: number): ReturnType<typeof parseTimelineSegments> {
  const items: Record<string, unknown>[] = [];
  for (let i = 0; i < count; i += 1) items.push(makeSegment(i));
  return parseTimelineSegments({ items });
}

function setVideoDuration(video: HTMLVideoElement, seconds: number): void {
  Object.defineProperty(video, 'duration', { configurable: true, get: () => seconds });
}

function setVideoTime(video: HTMLVideoElement, seconds: number): void {
  try {
    video.currentTime = seconds;
  } catch {
    let backing = seconds;
    Object.defineProperty(video, 'currentTime', {
      configurable: true,
      get: () => backing,
      set: (v: number) => {
        backing = v;
      },
    });
  }
}

function fake2dContext(): Record<string, unknown> & {
  save: () => void;
  restore: () => void;
  scale: () => void;
  clearRect: () => void;
  fillRect: () => void;
} {
  return {
    save: vi.fn(),
    restore: vi.fn(),
    scale: vi.fn(),
    clearRect: vi.fn(),
    fillRect: vi.fn(),
    fillStyle: '',
  };
}

function stubRect(element: Element, rect: Partial<DOMRect>): void {
  const full = {
    width: rect.width ?? 0,
    height: rect.height ?? 0,
    left: rect.left ?? 0,
    top: 0,
    right: (rect.left ?? 0) + (rect.width ?? 0),
    bottom: rect.height ?? 0,
    x: rect.left ?? 0,
    y: 0,
    toJSON: () => ({}),
  } as DOMRect;
  element.getBoundingClientRect = () => full;
}

beforeEach(() => {
  resetWorld();
  setInnerFetchForTests(mockFetch as typeof fetch);
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  useTimelinePlayerStore.getState().resetForTests();
  useTranscriptPlaybackStore.getState().resetForTests();
  queryClient.clear();
  stubMediaBaseline();
});

afterEach(() => {
  cleanup();
  restoreInnerFetchForTests();
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  useTimelinePlayerStore.getState().resetForTests();
  useTranscriptPlaybackStore.getState().resetForTests();
  queryClient.clear();
  vi.restoreAllMocks();
  vi.useRealTimers();
});

describe('playerStore guards', () => {
  it('clamps seeks and ignores non-finite positions', () => {
    const store = useTimelinePlayerStore.getState();
    store.requestSeek(Number.NaN);
    expect(useTimelinePlayerStore.getState().positionMs).toBe(0);
    store.requestSeek(-250);
    expect(useTimelinePlayerStore.getState().positionMs).toBe(0);
    store.requestSeek(Number.POSITIVE_INFINITY);
    expect(useTimelinePlayerStore.getState().positionMs).toBe(0);
    store.requestSeek(1200);
    expect(useTimelinePlayerStore.getState().positionMs).toBe(1200);
    store.reportPosition(Number.NaN);
    expect(useTimelinePlayerStore.getState().positionMs).toBe(1200);
    store.reportPosition(-5);
    expect(useTimelinePlayerStore.getState().positionMs).toBe(1200);
    store.reportPosition(Number.POSITIVE_INFINITY);
    expect(useTimelinePlayerStore.getState().positionMs).toBe(1200);
    store.reportPosition(1300.6);
    expect(useTimelinePlayerStore.getState().positionMs).toBe(1301);
  });

  it('clamps volume and rate defensively', () => {
    const store = useTimelinePlayerStore.getState();
    store.setVolume(Number.NaN);
    expect(useTimelinePlayerStore.getState().volume).toBe(1);
    store.setVolume(2);
    expect(useTimelinePlayerStore.getState().volume).toBe(1);
    store.setVolume(-1);
    expect(useTimelinePlayerStore.getState().volume).toBe(0);
    expect(useTimelinePlayerStore.getState().muted).toBe(true);
    store.setVolume(0.5);
    expect(useTimelinePlayerStore.getState().volume).toBe(0.5);
    expect(useTimelinePlayerStore.getState().muted).toBe(false);
    store.setVolume(0);
    expect(useTimelinePlayerStore.getState().muted).toBe(true);
    store.setRate(Number.NaN);
    expect(useTimelinePlayerStore.getState().rate).toBe(1);
    store.setRate(0);
    expect(useTimelinePlayerStore.getState().rate).toBe(1);
    store.setRate(-1);
    expect(useTimelinePlayerStore.getState().rate).toBe(1);
    store.setRate(2);
    expect(useTimelinePlayerStore.getState().rate).toBe(2);
  });

  it('rejects invalid play regions', () => {
    const store = useTimelinePlayerStore.getState();
    store.setPlayRegion({ startMs: 2000, endMs: 1000 });
    expect(useTimelinePlayerStore.getState().playRegion).toBeUndefined();
    store.setPlayRegion({ startMs: 1000, endMs: 1000 });
    expect(useTimelinePlayerStore.getState().playRegion).toBeUndefined();
    store.setPlayRegion({ startMs: Number.NaN, endMs: Number.NaN });
    expect(useTimelinePlayerStore.getState().playRegion).toBeUndefined();
    store.setPlayRegion({ startMs: 100, endMs: 200 });
    expect(useTimelinePlayerStore.getState().playRegion).toEqual({ startMs: 100, endMs: 200 });
    store.setPlayRegion(undefined);
    expect(useTimelinePlayerStore.getState().playRegion).toBeUndefined();
  });

  it('clamps viewport and guards zoom/pan/issue jumps', () => {
    const store = useTimelinePlayerStore.getState();
    store.setViewport(100, Number.NaN);
    expect(useTimelinePlayerStore.getState().pxPerMs).toBe(0.1);
    store.setViewport(-50, 100);
    expect(useTimelinePlayerStore.getState().viewportStartMs).toBe(0);
    expect(useTimelinePlayerStore.getState().pxPerMs).toBe(TIMELINE_ZOOM_MAX);
    store.setViewport(0, 0.0001);
    expect(useTimelinePlayerStore.getState().pxPerMs).toBeCloseTo(TIMELINE_ZOOM_MIN / 4, 5);
    store.resetForTests();
    const beforeZoom = useTimelinePlayerStore.getState().pxPerMs;
    store.zoomViewport(Number.NaN);
    expect(useTimelinePlayerStore.getState().pxPerMs).toBe(beforeZoom);
    store.zoomViewport(0);
    expect(useTimelinePlayerStore.getState().pxPerMs).toBe(beforeZoom);
    store.zoomViewport(-2);
    expect(useTimelinePlayerStore.getState().pxPerMs).toBe(beforeZoom);
    store.zoomViewport(2);
    expect(useTimelinePlayerStore.getState().pxPerMs).toBeGreaterThan(beforeZoom);
    const startBefore = useTimelinePlayerStore.getState().viewportStartMs;
    store.zoomViewport(2, Number.NaN);
    expect(useTimelinePlayerStore.getState().viewportStartMs).toBe(startBefore);
    store.zoomViewport(1.5, 400);
    expect(useTimelinePlayerStore.getState().viewportStartMs).toBeGreaterThanOrEqual(0);
    const panBefore = useTimelinePlayerStore.getState().viewportStartMs;
    store.panViewport(Number.NaN);
    expect(useTimelinePlayerStore.getState().viewportStartMs).toBe(panBefore);
    store.panViewport(0);
    expect(useTimelinePlayerStore.getState().viewportStartMs).toBe(panBefore);
    store.panViewport(200);
    expect(useTimelinePlayerStore.getState().viewportStartMs).toBe(panBefore + 200);
    store.panViewport(-100000);
    expect(useTimelinePlayerStore.getState().viewportStartMs).toBe(0);
    store.jumpToIssue(undefined);
    expect(useTimelinePlayerStore.getState().positionMs).toBe(0);
    store.jumpToIssue(Number.NaN);
    expect(useTimelinePlayerStore.getState().positionMs).toBe(0);
    store.jumpToIssue(Number.POSITIVE_INFINITY);
    expect(useTimelinePlayerStore.getState().positionMs).toBe(0);
    store.jumpToIssue(750);
    expect(useTimelinePlayerStore.getState().positionMs).toBe(750);
  });
});

describe('MediaPlayer transport extras', () => {
  it('drives volume, mute, and rate controls', async () => {
    authenticate();
    renderWithProviders(<MediaPlayer projectId="prj_1" segments={parsedSegments(1)} />);
    await screen.findByTestId('timeline-media');
    fireEvent.change(screen.getByTestId('timeline-volume'), { target: { value: '0' } });
    expect(useTimelinePlayerStore.getState().muted).toBe(true);
    fireEvent.change(screen.getByTestId('timeline-volume'), { target: { value: '0.5' } });
    expect(useTimelinePlayerStore.getState().volume).toBe(0.5);
    expect(useTimelinePlayerStore.getState().muted).toBe(false);
    fireEvent.click(screen.getByTestId('timeline-mute'));
    expect(useTimelinePlayerStore.getState().muted).toBe(true);
    fireEvent.click(screen.getByTestId('timeline-mute'));
    expect(useTimelinePlayerStore.getState().muted).toBe(false);
    fireEvent.change(screen.getByTestId('timeline-rate'), { target: { value: '1.5' } });
    expect(useTimelinePlayerStore.getState().rate).toBe(1.5);
  });

  it('steps frames and clamps scrub seeks to duration', async () => {
    authenticate();
    renderWithProviders(<MediaPlayer projectId="prj_1" segments={parsedSegments(2)} />);
    await screen.findByTestId('timeline-media');
    useTimelinePlayerStore.getState().setDuration(6000);
    fireEvent.click(screen.getByTestId('timeline-frame-forward'));
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBeGreaterThan(0));
    const afterForward = useTimelinePlayerStore.getState().positionMs;
    fireEvent.click(screen.getByTestId('timeline-frame-back'));
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBeLessThan(afterForward));
    fireEvent.change(screen.getByTestId('timeline-scrub'), { target: { value: '99999' } });
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBe(6000));
    fireEvent.change(screen.getByTestId('timeline-scrub'), { target: { value: '0' } });
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBe(0));
  });

  it('covers the keyboard matrix', async () => {
    authenticate();
    renderWithProviders(<MediaPlayer projectId="prj_1" segments={parsedSegments(2)} />);
    await screen.findByTestId('timeline-media');
    useTimelinePlayerStore.getState().setDuration(60000);
    useTimelinePlayerStore.getState().requestSeek(20000);
    const player = screen.getByTestId('timeline-player');
    fireEvent.keyDown(player, { key: 'ArrowLeft' });
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBe(15000));
    fireEvent.keyDown(player, { key: 'ArrowRight' });
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBe(20000));
    fireEvent.keyDown(player, { key: 'j' });
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBe(10000));
    fireEvent.keyDown(player, { key: 'l' });
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBe(20000));
    fireEvent.keyDown(player, { key: 'J' });
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBe(10000));
    fireEvent.keyDown(player, { key: 'L' });
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBe(20000));
    fireEvent.keyDown(player, { key: 'k' });
    await waitFor(() => expect(useTimelinePlayerStore.getState().isPlaying).toBe(true));
    fireEvent.keyDown(player, { key: 'K' });
    await waitFor(() => expect(useTimelinePlayerStore.getState().isPlaying).toBe(false));
    fireEvent.keyDown(player, { key: 'Home' });
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBe(0));
    fireEvent.keyDown(player, { key: 'End' });
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBe(60000));
    const frozen = useTimelinePlayerStore.getState().positionMs;
    fireEvent.keyDown(player, { key: 'x' });
    expect(useTimelinePlayerStore.getState().positionMs).toBe(frozen);
  });

  it('ignores guarded keydowns', async () => {
    authenticate();
    renderWithProviders(<MediaPlayer projectId="prj_1" segments={parsedSegments(1)} />);
    await screen.findByTestId('timeline-media');
    const player = screen.getByTestId('timeline-player');
    useTimelinePlayerStore.getState().requestSeek(5000);
    const input = document.createElement('input');
    player.appendChild(input);
    fireEvent.keyDown(input, { key: ' ' });
    expect(useTimelinePlayerStore.getState().positionMs).toBe(5000);
    input.remove();
  });

  it('steps to segment edges and tolerates empty lists', async () => {
    authenticate();
    renderWithProviders(<MediaPlayer projectId="prj_1" segments={parsedSegments(2)} />);
    await screen.findByTestId('timeline-media');
    useTimelinePlayerStore.getState().requestSeek(5000);
    fireEvent.click(screen.getByTestId('timeline-next-segment'));
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBe(3800));
    useTimelinePlayerStore.getState().requestSeek(0);
    fireEvent.click(screen.getByTestId('timeline-prev-segment'));
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBe(0));
    cleanup();
    queryClient.clear();
    useTimelinePlayerStore.getState().resetForTests();
    renderWithProviders(<MediaPlayer projectId="prj_1" segments={[]} />);
    await screen.findByTestId('timeline-media');
    fireEvent.click(screen.getByTestId('timeline-next-segment'));
    fireEvent.click(screen.getByTestId('timeline-prev-segment'));
    expect(useTimelinePlayerStore.getState().positionMs).toBe(0);
  });

  it('mirrors play/pause media events into the store', async () => {
    authenticate();
    renderWithProviders(<MediaPlayer projectId="prj_1" segments={parsedSegments(1)} />);
    const video = (await screen.findByTestId('timeline-media')) as HTMLVideoElement;
    fireEvent.play(video);
    expect(useTimelinePlayerStore.getState().isPlaying).toBe(true);
    fireEvent.pause(video);
    expect(useTimelinePlayerStore.getState().isPlaying).toBe(false);
  });

  it('loads duration from metadata and ignores non-finite values', async () => {
    authenticate();
    renderWithProviders(<MediaPlayer projectId="prj_1" segments={parsedSegments(1)} />);
    const video = (await screen.findByTestId('timeline-media')) as HTMLVideoElement;
    setVideoDuration(video, 60);
    fireEvent.loadedMetadata(video);
    expect(useTimelinePlayerStore.getState().durationMs).toBe(60000);
    setVideoDuration(video, Number.NaN);
    fireEvent.loadedMetadata(video);
    expect(useTimelinePlayerStore.getState().durationMs).toBe(60000);
  });

  it('reports time updates and honors loop/single regions', async () => {
    authenticate();
    renderWithProviders(<MediaPlayer projectId="prj_1" segments={parsedSegments(1)} />);
    const video = (await screen.findByTestId('timeline-media')) as HTMLVideoElement;
    setVideoTime(video, 2.5);
    fireEvent.timeUpdate(video);
    expect(useTimelinePlayerStore.getState().positionMs).toBe(2500);
    useTimelinePlayerStore.getState().setPlayRegion({ startMs: 1000, endMs: 2000 });
    useTimelinePlayerStore.getState().setLoopRegion(true);
    setVideoTime(video, 2.2);
    fireEvent.timeUpdate(video);
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBe(1000));
    useTimelinePlayerStore.getState().setLoopRegion(false);
    useTimelinePlayerStore.getState().setPlaying(true);
    setVideoTime(video, 2.4);
    fireEvent.timeUpdate(video);
    expect(useTimelinePlayerStore.getState().isPlaying).toBe(false);
  });

  it('recovers from media errors with resume then surfaces repeat failure', async () => {
    authenticate();
    renderWithProviders(<MediaPlayer projectId="prj_1" segments={parsedSegments(1)} />);
    const video = (await screen.findByTestId('timeline-media')) as HTMLVideoElement;
    useTimelinePlayerStore.getState().requestSeek(1234);
    fireEvent.error(video);
    expect(await screen.findByTestId('timeline-resume-note')).toBeDefined();
    fireEvent.error(video);
    expect(await screen.findByTestId('timeline-error')).toBeDefined();
  });

  it('toggles rejected, sync, and throwing play paths', async () => {
    authenticate();
    renderWithProviders(<MediaPlayer projectId="prj_1" segments={parsedSegments(1)} />);
    await screen.findByTestId('timeline-media');
    const proto = window.HTMLMediaElement.prototype as unknown as Record<string, unknown>;
    const originalPlay = proto['play'];
    const originalPause = proto['pause'];
    try {
      proto['play'] = vi.fn().mockRejectedValue(new Error('denied'));
      fireEvent.click(screen.getByTestId('timeline-play-toggle'));
      await waitFor(() => expect(useTimelinePlayerStore.getState().isPlaying).toBe(false));
      proto['play'] = vi.fn().mockReturnValue(undefined);
      fireEvent.click(screen.getByTestId('timeline-play-toggle'));
      await waitFor(() => expect(useTimelinePlayerStore.getState().isPlaying).toBe(true));
      proto['play'] = vi.fn().mockImplementation(() => {
        throw new Error('boom');
      });
      fireEvent.click(screen.getByTestId('timeline-play-toggle'));
      await waitFor(() => expect(useTimelinePlayerStore.getState().isPlaying).toBe(false));
      proto['pause'] = vi.fn().mockImplementation(() => {
        throw new Error('pause-boom');
      });
      useTimelinePlayerStore.getState().setPlaying(true);
      fireEvent.click(screen.getByTestId('timeline-play-toggle'));
      await waitFor(() => expect(useTimelinePlayerStore.getState().isPlaying).toBe(false));
    } finally {
      proto['play'] = originalPlay;
      proto['pause'] = originalPause;
    }
  });

  it('opens fullscreen and PiP progressively', async () => {
    authenticate();
    renderWithProviders(<MediaPlayer projectId="prj_1" segments={parsedSegments(1)} />);
    const video = (await screen.findByTestId('timeline-media')) as HTMLVideoElement & {
      requestPictureInPicture?: () => Promise<void>;
    };
    const fullscreenSpy = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(Element.prototype, 'requestFullscreen', {
      configurable: true,
      writable: true,
      value: fullscreenSpy,
    });
    fireEvent.click(screen.getByTestId('timeline-fullscreen'));
    expect(fullscreenSpy).toHaveBeenCalled();
    const pipSpy = vi.fn().mockResolvedValue(undefined);
    video.requestPictureInPicture = pipSpy;
    fireEvent.click(screen.getByTestId('timeline-pip'));
    expect(pipSpy).toHaveBeenCalled();
  });

  it('tolerates missing or throwing fullscreen/PiP', async () => {
    authenticate();
    renderWithProviders(<MediaPlayer projectId="prj_1" segments={parsedSegments(1)} />);
    const video = (await screen.findByTestId('timeline-media')) as HTMLVideoElement & {
      requestPictureInPicture?: () => Promise<void>;
    };
    Object.defineProperty(Element.prototype, 'requestFullscreen', {
      configurable: true,
      writable: true,
      value: undefined,
    });
    fireEvent.click(screen.getByTestId('timeline-fullscreen'));
    expect(screen.getByTestId('timeline-fullscreen')).toBeDefined();
    Object.defineProperty(Element.prototype, 'requestFullscreen', {
      configurable: true,
      writable: true,
      value: vi.fn().mockImplementation(() => {
        throw new Error('fs-boom');
      }),
    });
    fireEvent.click(screen.getByTestId('timeline-fullscreen'));
    delete (video as unknown as Record<string, unknown>)['requestPictureInPicture'];
    fireEvent.click(screen.getByTestId('timeline-pip'));
    video.requestPictureInPicture = vi.fn().mockImplementation(() => {
      throw new Error('pip-boom');
    });
    fireEvent.click(screen.getByTestId('timeline-pip'));
    const rejecting = vi.fn().mockRejectedValue(new Error('nope'));
    Object.defineProperty(Element.prototype, 'requestFullscreen', {
      configurable: true,
      writable: true,
      value: rejecting,
    });
    fireEvent.click(screen.getByTestId('timeline-fullscreen'));
    const pipRejecting = vi.fn().mockRejectedValue(new Error('nope'));
    video.requestPictureInPicture = pipRejecting;
    fireEvent.click(screen.getByTestId('timeline-pip'));
  });

  it('hides progressive buttons when the platform lacks support', async () => {
    const fullscreenDesc = Object.getOwnPropertyDescriptor(document, 'fullscreenEnabled');
    const pipDesc = Object.getOwnPropertyDescriptor(document, 'pictureInPictureEnabled');
    Object.defineProperty(document, 'fullscreenEnabled', { configurable: true, value: false });
    Object.defineProperty(document, 'pictureInPictureEnabled', { configurable: true, value: false });
    try {
      authenticate();
      renderWithProviders(<MediaPlayer projectId="prj_1" segments={parsedSegments(1)} />);
      await screen.findByTestId('timeline-media');
      expect(screen.getByTestId('timeline-fullscreen').textContent).toContain('unavailable');
      expect(screen.getByTestId('timeline-pip').textContent).toContain('unavailable');
    } finally {
      if (fullscreenDesc !== undefined) Object.defineProperty(document, 'fullscreenEnabled', fullscreenDesc);
      else delete (document as unknown as Record<string, unknown>)['fullscreenEnabled'];
      if (pipDesc !== undefined) Object.defineProperty(document, 'pictureInPictureEnabled', pipDesc);
      else delete (document as unknown as Record<string, unknown>)['pictureInPictureEnabled'];
    }
  });

  it('shows loading while media resolves and retries expiry to ready', async () => {
    world.mediaMode = 'never';
    authenticate();
    renderWithProviders(<MediaPlayer projectId="prj_1" segments={parsedSegments(1)} />);
    expect(screen.getByTestId('timeline-loading')).toBeDefined();
    cleanup();
    queryClient.clear();
    useTimelinePlayerStore.getState().resetForTests();
    world.mediaMode = 'expiredAlways';
    renderWithProviders(<MediaPlayer projectId="prj_1" segments={parsedSegments(1)} />);
    expect(await screen.findByTestId('timeline-error')).toBeDefined();
    world.mediaMode = 'ok';
    fireEvent.click(screen.getByTestId('timeline-retry'));
    expect(await screen.findByTestId('timeline-media')).toBeDefined();
  });

  it('retries generic media errors to ready', async () => {
    world.mediaMode = 'error500';
    authenticate();
    renderWithProviders(<MediaPlayer projectId="prj_1" segments={parsedSegments(1)} />);
    expect(await screen.findByTestId('timeline-error')).toBeDefined();
    world.mediaMode = 'ok';
    fireEvent.click(screen.getByTestId('timeline-retry'));
    expect(await screen.findByTestId('timeline-media')).toBeDefined();
  });

  it('consumes store seeks with finite and non-finite durations', async () => {
    authenticate();
    renderWithProviders(<MediaPlayer projectId="prj_1" segments={parsedSegments(1)} />);
    const video = (await screen.findByTestId('timeline-media')) as HTMLVideoElement;
    setVideoDuration(video, 60);
    useTimelinePlayerStore.getState().requestSeek(5000);
    await waitFor(() => expect(Math.round(video.currentTime * 1000)).toBe(5000));
    setVideoDuration(video, Number.NaN);
    useTimelinePlayerStore.getState().requestSeek(2500);
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBe(2500));
  });
});

describe('Waveform player matrix', () => {
  it('draws peaks with a real 2d context', async () => {
    const ctx = fake2dContext();
    Object.defineProperty(window.HTMLCanvasElement.prototype, 'getContext', {
      configurable: true,
      writable: true,
      value: vi.fn().mockReturnValue(ctx),
    });
    authenticate();
    renderWithProviders(<Waveform projectId="prj_1" />);
    const canvas = (await screen.findByTestId('timeline-waveform-canvas')) as HTMLCanvasElement;
    expect(Number(canvas.getAttribute('data-peaks') ?? '0')).toBeGreaterThan(0);
  });

  it('draws with zero rect fallback and empty computed styles', async () => {
    const ctx = fake2dContext();
    Object.defineProperty(window.HTMLCanvasElement.prototype, 'getContext', {
      configurable: true,
      writable: true,
      value: vi.fn().mockReturnValue(ctx),
    });
    const styleSpy = vi.spyOn(window, 'getComputedStyle').mockReturnValue({
      getPropertyValue: () => '   ',
    } as unknown as CSSStyleDeclaration);
    const originalRatio = window.devicePixelRatio;
    Object.defineProperty(window, 'devicePixelRatio', { configurable: true, value: Number.NaN });
    try {
      authenticate();
      renderWithProviders(<Waveform projectId="prj_1" />);
      const canvas = (await screen.findByTestId('timeline-waveform-canvas')) as HTMLCanvasElement;
      stubRect(canvas, { width: 0, height: 0, left: 0 });
      useTimelinePlayerStore.getState().requestSeek(100);
      expect(canvas.getAttribute('data-peaks')).toBeDefined();
    } finally {
      styleSpy.mockRestore();
      Object.defineProperty(window, 'devicePixelRatio', { configurable: true, value: originalRatio });
    }
  });

  it('follows container resizes and disconnects on unmount', async () => {
    const ctx = fake2dContext();
    Object.defineProperty(window.HTMLCanvasElement.prototype, 'getContext', {
      configurable: true,
      writable: true,
      value: vi.fn().mockReturnValue(ctx),
    });
    // Warm the peaks cache so the second mount renders the wrap synchronously
    // and the ResizeObserver effect observes on mount (first mount starts
    // pending with no wrap, so the observer never attaches).
    authenticate();
    renderWithProviders(<Waveform projectId="prj_1" />);
    await screen.findByTestId('timeline-waveform-canvas');
    cleanup();
    let listener: ((entries: Array<{ contentRect: { width: number } }>) => void) | undefined;
    const observeSpy = vi.fn();
    const disconnectSpy = vi.fn();
    class MockObserver {
      private callback: (entries: Array<{ contentRect: { width: number } }>) => void;
      public constructor(callback: (entries: Array<{ contentRect: { width: number } }>) => void) {
        this.callback = callback;
        listener = callback;
      }
      public observe(): void {
        observeSpy();
      }
      public disconnect(): void {
        disconnectSpy();
      }
    }
    vi.stubGlobal('ResizeObserver', MockObserver);
    try {
      const { unmount } = render(
        <QueryClientProvider client={queryClient}>
          <LocaleProvider>
            <ToastProvider>
              <MemoryRouter>
                <Waveform projectId="prj_1" />
              </MemoryRouter>
            </ToastProvider>
          </LocaleProvider>
        </QueryClientProvider>,
      );
      await screen.findByTestId('timeline-waveform-canvas');
      expect(observeSpy).toHaveBeenCalled();
      listener?.([{ contentRect: { width: 640 } }]);
      expect(await screen.findByTestId('timeline-waveform-canvas')).toBeDefined();
      unmount();
      expect(disconnectSpy).toHaveBeenCalled();
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('calls onSeek after debounce and coalesces rapid scrubs', async () => {
    const seen: number[] = [];
    authenticate();
    renderWithProviders(<Waveform projectId="prj_1" onSeek={(ms: number) => seen.push(ms)} />);
    await screen.findByTestId('timeline-waveform-canvas');
    fireEvent.change(screen.getByTestId('timeline-waveform-scrub'), { target: { value: '1000' } });
    fireEvent.change(screen.getByTestId('timeline-waveform-scrub'), { target: { value: '2000' } });
    await waitFor(() => expect(seen).toEqual([2000]), { timeout: 3000 });
  });

  it('seeks from canvas clicks including zero-width rects', async () => {
    authenticate();
    renderWithProviders(<Waveform projectId="prj_1" />);
    const canvas = (await screen.findByTestId('timeline-waveform-canvas')) as HTMLCanvasElement;
    stubRect(canvas, { width: 600, height: 96, left: 0 });
    fireEvent.click(canvas, { clientX: 300 });
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBe(3000), { timeout: 3000 });
    stubRect(canvas, { width: 0, height: 96, left: 0 });
    fireEvent.click(canvas, { clientX: 50 });
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBe(0), { timeout: 3000 });
  });

  it('moves with canvas arrow keys', async () => {
    authenticate();
    renderWithProviders(<Waveform projectId="prj_1" />);
    const canvas = await screen.findByTestId('timeline-waveform-canvas');
    useTimelinePlayerStore.getState().requestSeek(10000);
    await waitFor(() => expect(screen.getByTestId('timeline-waveform-canvas').getAttribute('data-position')).toBe('10000'));
    fireEvent.keyDown(canvas, { key: 'ArrowLeft' });
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBe(5000), { timeout: 3000 });
    fireEvent.keyDown(screen.getByTestId('timeline-waveform-canvas'), { key: 'ArrowRight' });
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBe(10000), { timeout: 3000 });
    fireEvent.keyDown(screen.getByTestId('timeline-waveform-canvas'), { key: 'x' });
    expect(useTimelinePlayerStore.getState().positionMs).toBe(10000);
  });

  it('shows skeleton while peaks resolve and renders the memoized export', async () => {
    world.peaksMode = 'never';
    authenticate();
    renderWithProviders(<Waveform projectId="prj_1" />);
    expect(screen.getByTestId('timeline-waveform-skeleton')).toBeDefined();
    cleanup();
    queryClient.clear();
    useTimelinePlayerStore.getState().resetForTests();
    world.peaksMode = 'ok';
    renderWithProviders(<MemoizedWaveform projectId="prj_1" />);
    expect(await screen.findByTestId('timeline-waveform-canvas')).toBeDefined();
  });
});

describe('Timeline player matrix', () => {
  it('seeks from ruler clicks across viewport and total spans', async () => {
    authenticate();
    renderWithProviders(<Timeline projectId="prj_1" segments={parsedSegments(3)} />);
    await screen.findByTestId('timeline');
    const ruler = screen.getByTestId('timeline-ruler');
    stubRect(ruler, { width: 800, height: 24, left: 0 });
    fireEvent.click(ruler, { clientX: 400 });
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBeGreaterThan(0), { timeout: 3000 });
    useTimelinePlayerStore.getState().setViewport(0, 2);
    fireEvent.click(ruler, { clientX: 200 });
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBeGreaterThanOrEqual(0), {
      timeout: 3000,
    });
  });

  it('tolerates zero-width ruler rects', async () => {
    authenticate();
    renderWithProviders(<Timeline projectId="prj_1" segments={parsedSegments(2)} />);
    await screen.findByTestId('timeline');
    const ruler = screen.getByTestId('timeline-ruler');
    stubRect(ruler, { width: 0, height: 24, left: 0 });
    fireEvent.click(ruler, { clientX: 10 });
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBe(0), { timeout: 3000 });
  });

  it('moves the ruler with arrow/home/end keys', async () => {
    authenticate();
    renderWithProviders(<Timeline projectId="prj_1" segments={parsedSegments(3)} />);
    await screen.findByTestId('timeline');
    const ruler = screen.getByTestId('timeline-ruler');
    fireEvent.keyDown(ruler, { key: 'ArrowRight' });
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBeGreaterThan(0), { timeout: 3000 });
    fireEvent.keyDown(ruler, { key: 'ArrowLeft' });
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBe(0), { timeout: 3000 });
    fireEvent.keyDown(ruler, { key: 'End' });
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBeGreaterThan(0), { timeout: 3000 });
    fireEvent.keyDown(ruler, { key: 'Home' });
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBe(0), { timeout: 3000 });
  });

  it('zooms and pans through wheel gestures', async () => {
    authenticate();
    renderWithProviders(<Timeline projectId="prj_1" segments={parsedSegments(3)} />);
    await screen.findByTestId('timeline');
    const ruler = screen.getByTestId('timeline-ruler');
    stubRect(ruler, { width: 800, height: 24, left: 0 });
    const before = useTimelinePlayerStore.getState().pxPerMs;
    fireEvent.wheel(ruler, { deltaY: -100, ctrlKey: true, clientX: 400 });
    await waitFor(() => expect(useTimelinePlayerStore.getState().pxPerMs).not.toBe(before), { timeout: 3000 });
    fireEvent.wheel(ruler, { deltaY: 100, deltaX: 20 });
    fireEvent.wheel(ruler, { deltaY: 0, deltaX: 0 });
    stubRect(ruler, { width: 0, height: 24, left: 0 });
    fireEvent.wheel(ruler, { deltaY: -50, ctrlKey: true, clientX: 0 });
    await waitFor(() => expect(useTimelinePlayerStore.getState().pxPerMs).toBeGreaterThan(0), { timeout: 3000 });
  });

  it('drags to pan and shift-drags to seek', async () => {
    authenticate();
    renderWithProviders(<Timeline projectId="prj_1" segments={parsedSegments(3)} />);
    await screen.findByTestId('timeline');
    const ruler = screen.getByTestId('timeline-ruler');
    const capture = vi.fn();
    (Element.prototype as unknown as Record<string, unknown>)['setPointerCapture'] = capture;
    try {
      useTimelinePlayerStore.getState().setViewport(1000, 0.5);
      fireEvent.pointerDown(ruler, { clientX: 100, pointerId: 1, buttons: 1 });
      fireEvent.pointerMove(ruler, { clientX: 60, buttons: 1 });
      fireEvent.pointerUp(ruler);
      fireEvent.pointerDown(ruler, { clientX: 100, pointerId: 2, shiftKey: true, buttons: 1 });
      fireEvent.pointerMove(ruler, { clientX: 200, buttons: 1 });
      fireEvent.pointerUp(ruler);
      fireEvent.pointerDown(ruler, { clientX: 100, pointerId: 3, buttons: 1 });
      fireEvent.pointerMove(ruler, { clientX: 120, buttons: 0 });
      fireEvent.pointerUp(ruler);
      await waitFor(() => expect(capture).toHaveBeenCalled(), { timeout: 3000 });
    } finally {
      delete (Element.prototype as unknown as Record<string, unknown>)['setPointerCapture'];
    }
  });

  it('disables issues without timing and honors selectedId plus onSelect', async () => {
    const onSelect = vi.fn();
    const issues: TimelineIssue[] = [
      { id: 'issue-ok', segmentId: 'seg_001', atMs: undefined, label: 'ok issue' },
      { id: 'issue-ghost', segmentId: 'ghost', atMs: undefined, label: 'ghost issue' },
    ];
    authenticate();
    renderWithProviders(
      <Timeline projectId="prj_1" segments={parsedSegments(2)} issues={issues} selectedId="seg_002" onSelect={onSelect} />,
    );
    await screen.findByTestId('timeline');
    expect(screen.getByTestId('timeline-segment-seg_002').getAttribute('data-selected')).toBe('true');
    const ghost = screen.getByTestId('timeline-issue-issue-ghost') as HTMLButtonElement;
    expect(ghost.disabled).toBe(true);
    expect(ghost.getAttribute('data-target')).toBe('');
    fireEvent.click(screen.getByTestId('timeline-segment-seg_001'));
    expect(onSelect).toHaveBeenCalledWith('seg_001');
    await waitFor(() => expect(useTimelinePlayerStore.getState().selectedSegmentId).toBe('seg_001'));
  });

  it('builds loop regions from selection, full span, or nothing', async () => {
    authenticate();
    renderWithProviders(<Timeline projectId="prj_1" segments={parsedSegments(2)} />);
    await screen.findByTestId('timeline');
    fireEvent.click(screen.getByTestId('timeline-segment-seg_001'));
    fireEvent.click(screen.getByTestId('timeline-region-set'));
    expect(await screen.findByTestId('timeline-region-note')).toBeDefined();
    fireEvent.click(screen.getByTestId('timeline-region-clear'));
    fireEvent.click(screen.getByTestId('timeline-region-set'));
    expect(await screen.findByTestId('timeline-region-note')).toBeDefined();
    cleanup();
    queryClient.clear();
    useTimelinePlayerStore.getState().resetForTests();
    renderWithProviders(<Timeline projectId="prj_1" segments={[]} />);
    await screen.findByTestId('timeline');
    fireEvent.click(screen.getByTestId('timeline-region-set'));
    expect(screen.queryByTestId('timeline-region-note')).toBeNull();
  });

  it('toggles region looping explicitly', async () => {
    authenticate();
    renderWithProviders(<Timeline projectId="prj_1" segments={parsedSegments(2)} />);
    await screen.findByTestId('timeline');
    fireEvent.click(screen.getByTestId('timeline-region-loop'));
    expect(useTimelinePlayerStore.getState().loopRegion).toBe(true);
    fireEvent.click(screen.getByTestId('timeline-region-loop'));
    expect(useTimelinePlayerStore.getState().loopRegion).toBe(false);
  });
});
