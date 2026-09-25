/**
 * Timeline domain view (Task 030).
 *
 * Pure parsing + derivation for the shared media player, canvas waveform,
 * and five-lane timeline. All shapes are parsed defensively (camelCase /
 * PascalCase, missing sections fall back to neutral defaults, never throw
 * for list rows). Timing is display-only: segment boundaries come from the
 * aggregate (Task 009 segments) and this module exposes no mutation that
 * adjusts timing. Texts render as plain text only.
 */

export const TIMELINE_DEBOUNCE_MS = 120;

export const SEEK_STEP_MS = 5000;

export const FRAME_MS = 33;

export const FRAME_STEP_SMALL_MS = 40;

export const PLAYBACK_RATES: readonly number[] = [0.5, 1, 1.25, 1.5, 2];

export const PEAK_RESOLUTIONS: readonly number[] = [64, 256, 1024];

export const SILENCE_GAP_THRESHOLD_MS = 1500;

export const TIMELINE_ZOOM_MIN = 0.25;

export const TIMELINE_ZOOM_MAX = 8;

export const TIMELINE_VIRTUALIZE_OVERSAN = 4;

/** Single source for the peaks endpoint token (peaks-only data layer). */
export const PEAKS_ENDPOINT_TOKEN = 'waveform-peaks';

/** Preview-media endpoint token (signed playback URL, never an archival path). */
export const PREVIEW_MEDIA_TOKEN = 'output/download';

export type TimelineMarkerKind = 'start' | 'end' | 'overlap' | 'silence' | 'warning' | 'missing';

export const TIMELINE_MARKER_KINDS: readonly TimelineMarkerKind[] = [
  'start',
  'end',
  'overlap',
  'silence',
  'warning',
  'missing',
];

export type MarkerOwnerSurface = 'transcript' | 'translation' | 'qc';

export interface WaveformPeaksView {
  readonly durationMs: number;
  readonly sampleRate: number;
  readonly peaksMissing: boolean;
  readonly resolutions: Readonly<Record<string, readonly number[]>>;
}

export interface TimelineSegmentView {
  readonly id: string;
  readonly sequence: number;
  readonly startMs: number;
  readonly endMs: number;
  readonly speakerLabel: string;
  readonly reviewStatus: string | undefined;
  readonly qualityCodes: readonly string[];
  readonly syncStatus: string | undefined;
}

export interface TimelineMarker {
  readonly id: string;
  readonly kind: TimelineMarkerKind;
  readonly atMs: number;
  readonly endMs: number | undefined;
  readonly label: string;
  readonly cause: string;
  readonly ownerSurface: MarkerOwnerSurface;
  /** Non-color encoding: pattern name + text label (never color alone). */
  readonly pattern: string;
}

export interface TimelineIssue {
  readonly id: string;
  readonly segmentId: string | undefined;
  readonly atMs: number | undefined;
  readonly label: string;
}

export interface TimelineGap {
  readonly id: string;
  readonly startMs: number;
  readonly endMs: number;
}

function toRecord(value: unknown): Record<string, unknown> | undefined {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : undefined;
}

function pick(record: Record<string, unknown> | undefined, ...keys: readonly string[]): unknown {
  if (record === undefined) {
    return undefined;
  }
  for (const key of keys) {
    const value = record[key];
    if (value !== undefined && value !== null) {
      return value;
    }
  }
  return undefined;
}

function toNonEmptyString(value: unknown): string | undefined {
  return typeof value === 'string' && value !== '' ? value : undefined;
}

function toFiniteNumber(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

function toStringArray(value: unknown): string[] {
  if (!Array.isArray(value)) {
    return [];
  }
  return (value as unknown[]).filter((entry): entry is string => typeof entry === 'string' && entry !== '');
}

/** Clamps one peak sample to 0..1; NaN/non-finite becomes 0. Pure. */
export function clampPeak(value: unknown): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    return 0;
  }
  if (value <= 0) {
    return 0;
  }
  if (value >= 1) {
    return 1;
  }
  return value;
}

function parseResolutions(raw: unknown): { peaks: Record<string, number[]>; missing: boolean } {
  const peaks: Record<string, number[]> = {};
  let missing = false;
  const record = toRecord(raw);
  const source = record !== undefined ? pick(record, 'resolutions', 'Resolutions', 'peaks', 'Peaks') : undefined;
  const sourceRecord = toRecord(source);
  const entries: Array<[string, unknown]> =
    sourceRecord !== undefined
      ? Object.entries(sourceRecord)
      : Array.isArray(source)
        ? source.map((entry, index) => [String(PEAK_RESOLUTIONS[index] ?? index), entry] as [string, unknown])
        : [];
  for (const [key, value] of entries) {
    if (!Array.isArray(value)) {
      peaks[key] = [];
      continue;
    }
    peaks[key] = (value as unknown[]).map((entry) => clampPeak(entry));
  }
  for (const resolution of PEAK_RESOLUTIONS) {
    const key = String(resolution);
    if (peaks[key] === undefined) {
      peaks[key] = [];
    }
  }
  const flagRecord = record ?? toRecord(raw);
  if (flagRecord !== undefined) {
    const flag = pick(flagRecord, 'peaksMissing', 'PeaksMissing', 'missing', 'Missing');
    if (flag === true) {
      missing = true;
    }
  }
  const allEmpty = PEAK_RESOLUTIONS.every((resolution) => (peaks[String(resolution)] ?? []).length === 0);
  if (allEmpty) {
    missing = true;
  }
  return { peaks, missing };
}

/**
 * Parses a `WaveformPeaks` payload (`{ durationMs, sampleRate,
 * resolutions: { 64, 256, 1024 }, peaksMissing? }`). Peak samples are
 * clamped to 0..1 and NaN is neutralized before canvas draw. Never throws.
 * Pure.
 */
export function parseWaveformPeaks(raw: unknown): WaveformPeaksView {
  const record = toRecord(raw);
  if (record === undefined) {
    return { durationMs: 0, sampleRate: 8000, peaksMissing: true, resolutions: { 64: [], 256: [], 1024: [] } };
  }
  const durationMs = Math.max(0, Math.round(toFiniteNumber(pick(record, 'durationMs', 'DurationMs')) ?? 0));
  const sampleRateRaw = toFiniteNumber(pick(record, 'sampleRate', 'SampleRate')) ?? 8000;
  const sampleRate = Number.isFinite(sampleRateRaw) && sampleRateRaw > 0 ? Math.round(sampleRateRaw) : 8000;
  const { peaks, missing } = parseResolutions(record);
  return { durationMs, sampleRate, peaksMissing: missing, resolutions: peaks };
}

/** True when no usable peaks exist (still processing or trackless media). Pure. */
export function isPeaksMissing(peaks: WaveformPeaksView | undefined): boolean {
  if (peaks === undefined) {
    return true;
  }
  return peaks.peaksMissing;
}

/**
 * Picks the smallest stored resolution at or above the canvas width, else
 * the densest available. Pure.
 */
export function selectPeaksForWidth(peaks: WaveformPeaksView, widthPx: number): readonly number[] {
  const width = Number.isFinite(widthPx) && widthPx > 0 ? Math.ceil(widthPx) : 256;
  const ordered = [...PEAK_RESOLUTIONS].sort((a, b) => a - b);
  for (const resolution of ordered) {
    const bucket = peaks.resolutions[String(resolution)];
    if (bucket !== undefined && bucket.length >= width) {
      return bucket;
    }
  }
  for (const resolution of [...ordered].reverse()) {
    const bucket = peaks.resolutions[String(resolution)];
    if (bucket !== undefined && bucket.length > 0) {
      return bucket;
    }
  }
  return [];
}

/**
 * Builds the peaks request path for a project. The single choke point the
 * waveform data layer uses; visualization must never request archival
 * originals. Pure.
 */
export function peaksPathFor(projectId: string, resolution?: number): string {
  const encoded = encodeURIComponent(projectId);
  if (resolution !== undefined && Number.isFinite(resolution) && resolution > 0) {
    return `/projects/${encoded}/media/${PEAKS_ENDPOINT_TOKEN}?resolution=${String(Math.round(resolution))}`;
  }
  return `/projects/${encoded}/media/${PEAKS_ENDPOINT_TOKEN}`;
}

/**
 * Builds one timeline segment view from an aggregate row. Boundaries are
 * taken verbatim from the aggregate; this parser never synthesizes timing.
 * Pure.
 */
export function parseTimelineSegment(raw: unknown): TimelineSegmentView | undefined {
  const record = toRecord(raw);
  if (record === undefined) {
    return undefined;
  }
  const id = toNonEmptyString(pick(record, 'id', 'Id')) ?? '';
  if (id === '') {
    return undefined;
  }
  const startMs = Math.max(0, Math.round(toFiniteNumber(pick(record, 'startMs', 'StartMs')) ?? 0));
  const endRaw = toFiniteNumber(pick(record, 'endMs', 'EndMs'));
  const endMs = endRaw === undefined || endRaw < startMs ? startMs : Math.max(0, Math.round(endRaw));
  return {
    id,
    sequence: toFiniteNumber(pick(record, 'sequence', 'Sequence')) ?? 0,
    startMs,
    endMs,
    speakerLabel:
      toNonEmptyString(pick(record, 'speakerLabel', 'SpeakerLabel', 'speakerName', 'SpeakerName')) ??
      toNonEmptyString(pick(record, 'speakerId', 'SpeakerId')) ??
      'Unknown speaker',
    reviewStatus: toNonEmptyString(pick(record, 'reviewStatus', 'ReviewStatus')),
    qualityCodes: toStringArray(pick(record, 'qualityCodes', 'QualityCodes')),
    syncStatus: toNonEmptyString(pick(record, 'syncStatus', 'SyncStatus')),
  };
}

/** Parses aggregate rows into time-ordered timeline segments. Pure. */
export function parseTimelineSegments(raw: unknown): TimelineSegmentView[] {
  const record = toRecord(raw);
  const items = record !== undefined ? pick(record, 'items', 'Items') : raw;
  if (!Array.isArray(items)) {
    return [];
  }
  const out: TimelineSegmentView[] = [];
  for (const entry of items as unknown[]) {
    const parsed = parseTimelineSegment(entry);
    if (parsed !== undefined) {
      out.push(parsed);
    }
  }
  return out.sort((a, b) => a.startMs - b.startMs || a.sequence - b.sequence);
}

function ownerForWarning(segment: TimelineSegmentView): MarkerOwnerSurface {
  const codes = segment.qualityCodes.join(' ').toLowerCase();
  if (codes.includes('sync') || (segment.syncStatus ?? '').toLowerCase().includes('issue')) {
    return 'translation';
  }
  if ((segment.reviewStatus ?? '').toLowerCase() === 'open') {
    return 'qc';
  }
  return 'transcript';
}

function markerLinkFor(owner: MarkerOwnerSurface, projectId: string, segmentId?: string): string {
  if (owner === 'translation') {
    return segmentId !== undefined ? `/projects/${projectId}/translation#${segmentId}` : `/projects/${projectId}/translation`;
  }
  if (owner === 'qc') {
    return `/projects/${projectId}/quality`;
  }
  return segmentId !== undefined ? `/projects/${projectId}/transcript#${segmentId}` : `/projects/${projectId}/transcript`;
}

/**
 * Derives display-only timing markers from aggregate segments. Covers
 * start/end per segment plus overlap, silence (gap >= threshold), warning
 * (review-open / quality codes / sync issue), and missing-audio
 * (zero-length segment or empty peaks). Every marker carries a text label, a
 * pattern name, a cause sentence, and an owning-surface link; missing
 * markers use a hatched pattern plus label, never color alone. Pure.
 */
export function deriveTimelineMarkers(
  segments: readonly TimelineSegmentView[],
  options?: { readonly projectId?: string; readonly peaksMissing?: boolean },
): TimelineMarker[] {
  const projectId = options?.projectId ?? '';
  const markers: TimelineMarker[] = [];
  const sorted = [...segments].sort((a, b) => a.startMs - b.startMs || a.sequence - b.sequence);
  for (const segment of sorted) {
    markers.push({
      id: `${segment.id}-start`,
      kind: 'start',
      atMs: segment.startMs,
      endMs: undefined,
      label: `Start ${segment.id}`,
      cause: `Segment ${segment.id} starts at ${formatPlayerTime(segment.startMs)} (aggregate timing, read-only).`,
      ownerSurface: 'transcript',
      pattern: 'solid-tick',
    });
    markers.push({
      id: `${segment.id}-end`,
      kind: 'end',
      atMs: segment.endMs,
      endMs: undefined,
      label: `End ${segment.id}`,
      cause: `Segment ${segment.id} ends at ${formatPlayerTime(segment.endMs)} (aggregate timing, read-only).`,
      ownerSurface: 'transcript',
      pattern: 'hollow-tick',
    });
    const needsWarning =
      segment.qualityCodes.length > 0 ||
      (segment.reviewStatus ?? '').toLowerCase() === 'open' ||
      (segment.syncStatus ?? '').toLowerCase().includes('issue') ||
      (segment.syncStatus ?? '').toLowerCase().includes('overflow');
    if (needsWarning) {
      const owner = ownerForWarning(segment);
      const detail =
        segment.qualityCodes.length > 0
          ? `flagged ${segment.qualityCodes.join(', ')}`
          : (segment.reviewStatus ?? '').toLowerCase() === 'open'
            ? 'review open'
            : `sync ${segment.syncStatus ?? 'issue'}`;
      markers.push({
        id: `${segment.id}-warning`,
        kind: 'warning',
        atMs: segment.startMs,
        endMs: segment.endMs,
        label: `Warning ${segment.id}`,
        cause: `Segment ${segment.id} ${detail}. See details in ${owner}.`,
        ownerSurface: owner,
        pattern: 'diagonal-stripes',
      });
    }
    if (segment.endMs <= segment.startMs || options?.peaksMissing === true) {
      markers.push({
        id: `${segment.id}-missing`,
        kind: 'missing',
        atMs: segment.startMs,
        endMs: segment.endMs > segment.startMs ? segment.endMs : segment.startMs + 1,
        label: `Missing audio ${segment.id}`,
        cause:
          segment.endMs <= segment.startMs
            ? `Segment ${segment.id} has zero length in the aggregate; rendered as an explicit gap block.`
            : `Segment ${segment.id} has no waveform peaks yet; audio preview is unavailable for this range.`,
        ownerSurface: 'qc',
        pattern: 'hatched-block',
      });
    }
  }
  for (let index = 0; index + 1 < sorted.length; index += 1) {
    const current = sorted[index];
    const next = sorted[index + 1];
    if (current === undefined || next === undefined) {
      continue;
    }
    if (next.startMs < current.endMs) {
      markers.push({
        id: `overlap-${current.id}-${next.id}`,
        kind: 'overlap',
        atMs: next.startMs,
        endMs: Math.min(current.endMs, next.endMs),
        label: `Overlap ${current.id} / ${next.id}`,
        cause: `Segments ${current.id} and ${next.id} overlap by ${String(current.endMs - next.startMs)} ms in aggregate timing.`,
        ownerSurface: 'transcript',
        pattern: 'crosshatch-block',
      });
    } else if (next.startMs - current.endMs >= SILENCE_GAP_THRESHOLD_MS) {
      markers.push({
        id: `silence-${current.id}-${next.id}`,
        kind: 'silence',
        atMs: current.endMs,
        endMs: next.startMs,
        label: `Silence ${formatPlayerTime(current.endMs)}–${formatPlayerTime(next.startMs)}`,
        cause: `No dialogue between ${current.id} and ${next.id} (${String(next.startMs - current.endMs)} ms gap).`,
        ownerSurface: 'transcript',
        pattern: 'dotted-block',
      });
    }
  }
  void markerLinkFor;
  void projectId;
  return markers.sort((a, b) => a.atMs - b.atMs || a.id.localeCompare(b.id));
}

/** Owning-surface link for a marker (plain path, never a signed URL). Pure. */
export function markerOwnerLink(marker: TimelineMarker, projectId: string): string {
  const segmentId = marker.id.includes('-start') || marker.id.includes('-end') || marker.id.includes('-warning') || marker.id.includes('-missing')
    ? marker.id.split('-')[0]
    : undefined;
  return markerLinkFor(marker.ownerSurface, projectId, segmentId);
}

/**
 * Derives explicit gap blocks between segments (zero-length segments also
 * surface as gaps). Gaps are rendered as labeled blocks, never collapsed.
 * Pure.
 */
export function deriveTimelineGaps(segments: readonly TimelineSegmentView[]): TimelineGap[] {
  const sorted = [...segments].sort((a, b) => a.startMs - b.startMs || a.sequence - b.sequence);
  const gaps: TimelineGap[] = [];
  for (let index = 0; index + 1 < sorted.length; index += 1) {
    const current = sorted[index];
    const next = sorted[index + 1];
    if (current === undefined || next === undefined) {
      continue;
    }
    if (next.startMs > current.endMs) {
      gaps.push({ id: `gap-${current.id}-${next.id}`, startMs: current.endMs, endMs: next.startMs });
    }
  }
  return gaps;
}

/** Parses issue rows (`{ id, segmentId?, atMs?, label? }`) for issue-jump. Pure. */
export function parseTimelineIssues(raw: unknown): TimelineIssue[] {
  const record = toRecord(raw);
  const items = record !== undefined ? pick(record, 'items', 'Items', 'issues', 'Issues') : raw;
  const list = Array.isArray(items) ? items : Array.isArray(raw) ? (raw as unknown[]) : [];
  const out: TimelineIssue[] = [];
  for (const entry of list as unknown[]) {
    const item = toRecord(entry);
    if (item === undefined) {
      continue;
    }
    const id = toNonEmptyString(pick(item, 'id', 'Id')) ?? '';
    if (id === '') {
      continue;
    }
    const atMs = toFiniteNumber(pick(item, 'atMs', 'AtMs', 'positionMs', 'PositionMs'));
    out.push({
      id,
      segmentId: toNonEmptyString(pick(item, 'segmentId', 'SegmentId')),
      atMs: atMs === undefined ? undefined : Math.max(0, Math.round(atMs)),
      label: toNonEmptyString(pick(item, 'label', 'Label', 'title', 'Title')) ?? id,
    });
  }
  return out;
}

/**
 * Resolves an issue jump target: explicit `atMs` wins, else the owning
 * segment start, else undefined. Pure.
 */
export function resolveIssueTarget(
  issue: TimelineIssue,
  segments: readonly TimelineSegmentView[],
): number | undefined {
  if (issue.atMs !== undefined) {
    return issue.atMs;
  }
  if (issue.segmentId !== undefined) {
    const segment = segments.find((entry) => entry.id === issue.segmentId);
    if (segment !== undefined) {
      return segment.startMs;
    }
  }
  return undefined;
}

/** `mm:ss.mmm` player timestamp (ms-accurate). Pure. */
export function formatPlayerTime(ms: number): string {
  const clamped = Number.isFinite(ms) ? Math.max(0, Math.round(ms)) : 0;
  const minutes = Math.floor(clamped / 60_000);
  const seconds = Math.floor((clamped % 60_000) / 1000);
  const millis = clamped % 1000;
  return `${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}.${String(millis).padStart(3, '0')}`;
}

/** True for expired signed URLs (410). Pure. */
export function isExpiredError(error: { readonly code?: string; readonly status?: number } | undefined | null): boolean {
  if (error === undefined || error === null) {
    return false;
  }
  return error.code === 'URL_EXPIRED' || error.status === 410;
}

/**
 * Leading-edge-suppressed debounce: rapid calls coalesce into one trailing
 * invocation after `waitMs`. Returns a callable with `.cancel()`. Pure
 * (timer-based, no React dependency) so unit tests can drive it with fake
 * timers.
 */
export function debounce<T extends (...args: readonly never[]) => void>(
  fn: T,
  waitMs: number,
): T & { readonly cancel: () => void } {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const wrapped = (...args: readonly never[]): void => {
    if (timer !== undefined) {
      clearTimeout(timer);
    }
    timer = setTimeout(() => {
      timer = undefined;
      fn(...args);
    }, Math.max(0, waitMs));
  };
  const withCancel = wrapped as T & { readonly cancel: () => void };
  Object.defineProperty(withCancel, 'cancel', {
    value: () => {
      if (timer !== undefined) {
        clearTimeout(timer);
        timer = undefined;
      }
    },
    enumerable: false,
  });
  return withCancel;
}
