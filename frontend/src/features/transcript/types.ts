/**
 * Transcript domain view (Task 027).
 *
 * Pure parsing + derivation over the Task 009 segment contract. All shapes
 * are parsed defensively (camelCase/PascalCase, missing sections fall back
 * to neutral defaults, never throw for list rows). Texts render as plain
 * text only — never HTML — and provider/model metadata is display-only
 * (never secrets, tokens, or internal paths).
 */

export const TRANSCRIPT_PAGE_SIZE = 200;

export const TRANSCRIPT_MAX_PAGES = 10;

export const LOW_CONFIDENCE_THRESHOLD = 0.7;

export const MANUAL_PROVIDER = 'manual';

export interface TranscriptVersionView {
  readonly id: string;
  readonly text: string;
  readonly provider: string;
  readonly model: string;
  readonly isSelected: boolean;
  readonly isManual: boolean;
  readonly createdAt: string;
  /** 1-based position in creation order (v1 = original). */
  readonly versionNumber: number;
}

export interface TranscriptSegmentView {
  readonly id: string;
  readonly sequence: number;
  readonly startMs: number;
  readonly endMs: number;
  readonly speakerId: string | undefined;
  readonly speakerLabel: string;
  /** Current selection pointer (optimistic-concurrency version). */
  readonly selectionVersion: number;
  readonly reviewStatus: string | undefined;
  readonly qualityCodes: readonly string[];
  readonly syncStatus: string | undefined;
  readonly confidence: number | undefined;
  /** Currently-selected text (center list + inspector). */
  readonly text: string;
  /** First (original) version text; falls back to `text` when history is absent. */
  readonly originalText: string;
  readonly selectedVersionId: string | undefined;
  readonly manualVersionId: string | undefined;
  readonly versions: readonly TranscriptVersionView[];
  readonly needsReview: boolean;
  readonly isLowConfidence: boolean;
}

export interface TranscriptFilter {
  readonly query: string;
  readonly speaker: string;
  readonly reviewOnly: boolean;
}

export const EMPTY_TRANSCRIPT_FILTER: TranscriptFilter = {
  query: '',
  speaker: '',
  reviewOnly: false,
};

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

function isManualProvider(provider: string): boolean {
  return provider.trim().toLowerCase() === MANUAL_PROVIDER;
}

/** Parses one transcript version row (detail `transcriptVersions[]`). */
export function parseTranscriptVersion(raw: unknown, index: number): TranscriptVersionView | undefined {
  const record = toRecord(raw);
  if (record === undefined) {
    return undefined;
  }
  const id = toNonEmptyString(pick(record, 'id', 'Id')) ?? '';
  if (id === '') {
    return undefined;
  }
  const provider = toNonEmptyString(pick(record, 'provider', 'Provider')) ?? 'unknown';
  const model = toNonEmptyString(pick(record, 'model', 'Model')) ?? 'unknown';
  const text = typeof pick(record, 'text', 'Text') === 'string' ? (pick(record, 'text', 'Text') as string) : '';
  return {
    id,
    text,
    provider,
    model,
    isSelected: pick(record, 'isSelected', 'IsSelected') === true,
    isManual: isManualProvider(provider),
    createdAt: toNonEmptyString(pick(record, 'createdAt', 'CreatedAt')) ?? '',
    versionNumber: index + 1,
  };
}

function parseVersions(raw: unknown): TranscriptVersionView[] {
  if (!Array.isArray(raw)) {
    return [];
  }
  const out: TranscriptVersionView[] = [];
  let number = 0;
  for (const entry of raw as unknown[]) {
    const parsed = parseTranscriptVersion(entry, number);
    if (parsed !== undefined) {
      out.push(parsed);
      number += 1;
    }
  }
  return out;
}

function parseConfidence(record: Record<string, unknown> | undefined): number | undefined {
  const raw = pick(record, 'confidence', 'Confidence');
  if (typeof raw === 'number' && Number.isFinite(raw)) {
    return Math.min(1, Math.max(0, raw));
  }
  return undefined;
}

function deriveText(record: Record<string, unknown>, versions: readonly TranscriptVersionView[]): string {
  const direct = pick(record, 'text', 'Text', 'transcriptText', 'TranscriptText', 'selectedText', 'SelectedText', 'primaryText', 'PrimaryText');
  if (typeof direct === 'string' && direct !== '') {
    return direct;
  }
  const selected = versions.find((v) => v.isSelected);
  if (selected !== undefined) {
    return selected.text;
  }
  if (versions.length > 0) {
    return versions[versions.length - 1]?.text ?? '';
  }
  return '';
}

function deriveOriginalText(versions: readonly TranscriptVersionView[], fallback: string): string {
  if (versions.length > 0) {
    return versions[0]?.text ?? fallback;
  }
  return fallback;
}

/**
 * Builds a segment view from a list-summary row and/or a detail row. Both
 * shapes are accepted: summaries carry timing/speaker/selection/review flags
 * while details additionally carry `transcriptVersions[]`. Missing texts fall
 * back to `''` (the Inspector hydrates the selected segment via detail).
 */
export function parseTranscriptSegment(raw: unknown): TranscriptSegmentView | undefined {
  const record = toRecord(raw);
  if (record === undefined) {
    return undefined;
  }
  const id = toNonEmptyString(pick(record, 'id', 'Id')) ?? '';
  if (id === '') {
    return undefined;
  }
  const versions = parseVersions(pick(record, 'transcriptVersions', 'TranscriptVersions'));
  const text = deriveText(record, versions);
  const originalText = deriveOriginalText(versions, text);
  const selectedFromVersions = versions.find((v) => v.isSelected)?.id;
  const selectedVersionId =
    toNonEmptyString(pick(record, 'selectedTranscriptVersionId', 'SelectedTranscriptVersionId')) ?? selectedFromVersions;
  const manualVersionId = versions.find((v) => v.isManual)?.id;
  const confidence = parseConfidence(record);
  const reviewStatus = toNonEmptyString(pick(record, 'reviewStatus', 'ReviewStatus'));
  const qualityCodes = toStringArray(pick(record, 'qualityCodes', 'QualityCodes'));
  const syncStatus = toNonEmptyString(pick(record, 'syncStatus', 'SyncStatus'));
  const speakerId = toNonEmptyString(pick(record, 'speakerId', 'SpeakerId'));
  const speakerLabel =
    toNonEmptyString(pick(record, 'speakerLabel', 'SpeakerLabel', 'speakerName', 'SpeakerName')) ??
    speakerId ??
    'Unknown speaker';
  const startMs = toFiniteNumber(pick(record, 'startMs', 'StartMs')) ?? 0;
  const endMsRaw = toFiniteNumber(pick(record, 'endMs', 'EndMs'));
  const endMs = endMsRaw === undefined || endMsRaw < startMs ? startMs : endMsRaw;
  const selectionVersion = toFiniteNumber(pick(record, 'selectionVersion', 'SelectionVersion')) ?? 0;
  const sequence = toFiniteNumber(pick(record, 'sequence', 'Sequence')) ?? 0;
  const needsReviewFlag = pick(record, 'needsReview', 'NeedsReview') === true;
  const reviewOpen = reviewStatus !== undefined && reviewStatus.toLowerCase() === 'open';
  const isLowConfidence = confidence !== undefined && confidence < LOW_CONFIDENCE_THRESHOLD;
  return {
    id,
    sequence,
    startMs: Math.max(0, Math.round(startMs)),
    endMs: Math.max(0, Math.round(endMs)),
    speakerId,
    speakerLabel,
    selectionVersion,
    reviewStatus,
    qualityCodes,
    syncStatus,
    confidence,
    text,
    originalText,
    selectedVersionId,
    manualVersionId,
    versions,
    needsReview: needsReviewFlag || reviewOpen || isLowConfidence,
    isLowConfidence,
  };
}

/** Parses a `SegmentListResponse` page (`{ items, ... }`). Skips bad rows. */
export function parseTranscriptListItems(raw: unknown): TranscriptSegmentView[] {
  const record = toRecord(raw);
  const items = record !== undefined ? pick(record, 'items', 'Items') : raw;
  if (!Array.isArray(items)) {
    return [];
  }
  const out: TranscriptSegmentView[] = [];
  for (const entry of items as unknown[]) {
    const parsed = parseTranscriptSegment(entry);
    if (parsed !== undefined) {
      out.push(parsed);
    }
  }
  return out.sort((a, b) => a.startMs - b.startMs || a.sequence - b.sequence);
}

/** Merges list summaries with detail hydration (detail wins for versions). */
export function mergeSegmentDetail(
  summary: TranscriptSegmentView,
  detailRaw: unknown,
): TranscriptSegmentView {
  const detail = parseTranscriptSegment(detailRaw);
  if (detail === undefined) {
    return summary;
  }
  if (detail.versions.length === 0) {
    return { ...summary, selectionVersion: detail.selectionVersion };
  }
  const text = detail.text !== '' ? detail.text : summary.text;
  const originalText = detail.originalText !== '' ? detail.originalText : summary.originalText;
  return {
    ...summary,
    text,
    originalText,
    selectionVersion: detail.selectionVersion,
    selectedVersionId: detail.selectedVersionId ?? summary.selectedVersionId,
    manualVersionId: detail.manualVersionId ?? summary.manualVersionId,
    versions: detail.versions,
    reviewStatus: detail.reviewStatus ?? summary.reviewStatus,
    qualityCodes: detail.qualityCodes.length > 0 ? detail.qualityCodes : summary.qualityCodes,
    syncStatus: detail.syncStatus ?? summary.syncStatus,
    confidence: detail.confidence ?? summary.confidence,
    needsReview: detail.needsReview || summary.needsReview,
    isLowConfidence: detail.isLowConfidence || summary.isLowConfidence,
  };
}

export interface LineageView {
  readonly originalText: string;
  readonly selectedText: string;
  readonly manualText: string | undefined;
  readonly selectedBadge: string;
  readonly hasManual: boolean;
}

/** Original vs selected vs manual lineage for badges + inspector. */
export function deriveLineage(segment: TranscriptSegmentView): LineageView {
  const selected = segment.versions.find((v) => v.id === segment.selectedVersionId) ?? segment.versions.find((v) => v.isSelected);
  const manual = segment.versions.find((v) => v.isManual);
  const selectedText = selected?.text ?? segment.text;
  const versionNumber = selected?.versionNumber;
  return {
    originalText: segment.originalText,
    selectedText,
    manualText: manual?.text,
    selectedBadge: versionNumber !== undefined ? `selected v${String(versionNumber)}` : 'selected',
    hasManual: manual !== undefined,
  };
}

/** `mm:ss.mmm` timestamp for rows (ms-accurate). */
export function formatTimestamp(ms: number): string {
  const clamped = Math.max(0, Math.round(ms));
  const minutes = Math.floor(clamped / 60_000);
  const seconds = Math.floor((clamped % 60_000) / 1000);
  const millis = clamped % 1000;
  return `${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}.${String(millis).padStart(3, '0')}`;
}

/** Active segment for a playback position; null beyond range (no crash). */
export function findActiveSegmentId(
  segments: readonly TranscriptSegmentView[],
  positionMs: number,
): string | undefined {
  if (!Number.isFinite(positionMs) || positionMs < 0) {
    return undefined;
  }
  for (const segment of segments) {
    if (positionMs >= segment.startMs && positionMs < segment.endMs) {
      return segment.id;
    }
  }
  return undefined;
}

/** Nearest surviving segment after a server-side delete (by sequence). */
export function nearestSegmentId(
  segments: readonly TranscriptSegmentView[],
  removedId: string,
  fallbackId: string | undefined,
): string | undefined {
  if (segments.length === 0) {
    return undefined;
  }
  if (fallbackId !== undefined && segments.some((s) => s.id === fallbackId)) {
    return fallbackId;
  }
  const removed = segments.find((s) => s.id === removedId);
  void removed;
  const sorted = [...segments].sort((a, b) => a.sequence - b.sequence || a.startMs - b.startMs);
  return sorted[0]?.id;
}

/** In-memory filter (speaker exact, text substring, review flag). Pure. */
export function filterTranscriptSegments(
  segments: readonly TranscriptSegmentView[],
  filter: TranscriptFilter,
): TranscriptSegmentView[] {
  const query = filter.query.trim().toLowerCase();
  const speaker = filter.speaker.trim().toLowerCase();
  return segments.filter((segment) => {
    if (filter.reviewOnly && !segment.needsReview) {
      return false;
    }
    if (speaker !== '' && segment.speakerLabel.toLowerCase() !== speaker && (segment.speakerId ?? '').toLowerCase() !== speaker) {
      return false;
    }
    if (query !== '') {
      const haystack = `${segment.text} ${segment.originalText} ${segment.speakerLabel}`.toLowerCase();
      if (!haystack.includes(query)) {
        return false;
      }
    }
    return true;
  });
}

/** True for 409 conflict errors (stale selection version). Pure. */
export function isSelectionConflict(error: { readonly code?: string; readonly status?: number } | undefined | null): boolean {
  if (error === undefined || error === null) {
    return false;
  }
  return error.code === 'SELECTION_CONFLICT' || error.status === 409;
}
