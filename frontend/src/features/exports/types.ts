/**
 * Output + export domain view (Task 033).
 *
 * Pure parsing + derivation over the Task 012/012A contracts:
 * - `GET /projects/{id}/output` carries the readiness aggregate
 *   `{ state|generationState, reason?, completeness{ready,total},
 *   progressApproximate?, errorCode?, items{video?,audio?,subtitles[],
 *   transcript?,translation?,timeline?,speakers?,qc{summary,issuesUrl?}},
 *   warnings[], updatedAt }`. Top-level states are `Ready|Generating|Failed|
 *   Partial|Unavailable` (capitalized); per-asset states are the lowercase
 *   aliases (`ready|generating|failed|partial|unavailable`). Both normalize
 *   case-insensitively here. Signed URLs are short-lived (≤15 min) and stay
 *   in anchors/query cache only — never logged, never in filter URLs.
 * - `GET /projects/{id}/exports` carries the page envelope
 *   `{ items, page, pageSize, total, hasMore }` of `ExportResponse`
 *   `{ id, projectId, format, status, isPartial, createdAt,
 *   completenessJson? }`. `format` is the kebab wire name allowlisted
 *   server-side by `ExportFormatParser` (`srt|webvtt|json-timeline|
 *   speaker-metadata|transcript|translation|quality-report`); `status` is the
 *   `ExportJobStatus` name (`Pending|Running|Completed|Failed|Cancelled`).
 *   The generated bundle still declares the stale `Srt|Vtt|Mp4|Wav|Mp3` enum,
 *   so parsing accepts both spellings and normalizes to the kebab wire name.
 * - `GET /projects/{id}/processing` carries the run list for the run
 *   selector (newest first, ids only).
 *
 * The export dialog captures `type` (audio/video/subtitles/manifest UX
 * grouping over `OutputItemsDto` asset kinds), `scope`
 * (full/segment-range/per-speaker where advertised), `run` (informational —
 * the server always exports the latest eligible run, never a client-sent
 * run id), and `format` (allowlisted only). Submit sends only
 * `{ format, profile?, allowPartial? }` (`profile` is the normalized kebab
 * `type`/`type-scope` variant, max 64, no traversal); the server remains
 * authoritative and validates the format + profile again.
 *
 * Texts render as plain text only. Internal paths, bucket names, and storage
 * keys never render: `isSafeDisplayUrl` admits only `https://` URLs without
 * internal patterns, and warning lists drop entries containing them.
 */

export type OutputState = 'Ready' | 'Generating' | 'Failed' | 'Partial' | 'Unavailable';

export const OUTPUT_STATES: readonly OutputState[] = ['Ready', 'Generating', 'Failed', 'Partial', 'Unavailable'];

export type OutputItemState = OutputState;

export type ExportDisplayState = 'queued' | 'generating' | 'ready' | 'failed';

export const EXPORT_DISPLAY_STATES: readonly ExportDisplayState[] = ['queued', 'generating', 'ready', 'failed'];

export type OutputItemKind =
  | 'video'
  | 'audio'
  | 'subtitles'
  | 'transcript'
  | 'translation'
  | 'timeline'
  | 'speakers'
  | 'qc';

/**
 * Backend-allowlisted export formats (single source, mirrors
 * `ExportFormatParser` wire names + `ToWireName` outputs server-side).
 * The dialog imports this — never hardcodes format strings.
 */
export const EXPORT_FORMAT_ALLOWLIST: readonly string[] = [
  'srt',
  'webvtt',
  'json-timeline',
  'speaker-metadata',
  'transcript',
  'translation',
  'quality-report',
];

/**
 * Export type grouping over `OutputItemsDto` asset kinds (UX only; the
 * server receives only `format` + derived `profile`).
 */
export const EXPORT_TYPE_ALLOWLIST: readonly string[] = ['audio', 'video', 'subtitles', 'manifest'];

/**
 * Export scope options. `full` is always advertised; `segment-range` needs
 * segments (`completeness.total > 0`); `per-speaker` needs the speakers
 * asset beyond `unavailable`. Unadvertised scopes are omitted, never
 * disabled-rendered.
 */
export const EXPORT_SCOPE_ALLOWLIST: readonly string[] = ['full', 'segment-range', 'per-speaker'];

export interface OutputCompletenessView {
  readonly ready: number;
  readonly total: number;
}

export interface OutputItemView {
  readonly kind: OutputItemKind;
  readonly label: string;
  readonly state: OutputItemState;
  readonly reason: string | undefined;
  readonly missing: readonly string[];
  readonly completeness: OutputCompletenessView | undefined;
  readonly downloadUrl: string | undefined;
  readonly detail: string | undefined;
}

export interface OutputView {
  readonly state: OutputState;
  readonly reason: string | undefined;
  readonly completeness: OutputCompletenessView;
  readonly progressApproximate: number | undefined;
  readonly errorCode: string | undefined;
  readonly items: readonly OutputItemView[];
  readonly warnings: readonly string[];
  readonly updatedAt: string | undefined;
}

export interface ExportCompletenessView {
  readonly ready: number;
  readonly total: number;
}

export interface ExportView {
  readonly id: string;
  readonly projectId: string;
  readonly format: string;
  readonly status: string;
  readonly displayState: ExportDisplayState;
  readonly isPartial: boolean;
  readonly createdAt: string | undefined;
  readonly completeness: ExportCompletenessView | undefined;
  readonly fileSizeBytes: number | undefined;
  readonly failureReason: string | undefined;
}

export interface ProcessingRunView {
  readonly id: string;
  readonly status: string;
}

export interface ExportRequestView {
  readonly type: string;
  readonly scope: string;
  readonly runId: string;
  readonly format: string;
  readonly allowPartial: boolean;
  readonly profile: string | undefined;
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

function toBoolean(value: unknown): boolean {
  return value === true;
}

/** True when text contains an internal path, bucket, or storage URI. Pure. */
export function containsInternalPath(text: string): boolean {
  const lowered = text.toLowerCase();
  return (
    lowered.includes('s3://') ||
    text.includes('/mnt/') ||
    text.includes('/var/') ||
    text.includes('C:\\') ||
    text.includes('c:\\') ||
    lowered.includes('bucket')
  );
}

/**
 * Admits only `https://` display URLs without internal patterns and without
 * `access_token`. Signed URLs stay in anchors only. Pure.
 */
export function isSafeDisplayUrl(url: string | undefined): boolean {
  if (url === undefined || url === '') {
    return false;
  }
  const trimmed = url.trim();
  if (!trimmed.toLowerCase().startsWith('https://')) {
    return false;
  }
  if (containsInternalPath(trimmed)) {
    return false;
  }
  if (trimmed.toLowerCase().includes('access_token')) {
    return false;
  }
  return true;
}

function sanitizeWarnings(raw: unknown): string[] {
  const out: string[] = [];
  const entries = Array.isArray(raw) ? raw : [];
  for (const entry of entries as unknown[]) {
    if (typeof entry === 'string' && entry !== '') {
      if (!containsInternalPath(entry)) {
        out.push(entry);
      }
      continue;
    }
    const record = toRecord(entry);
    if (record === undefined) {
      continue;
    }
    const code = toNonEmptyString(pick(record, 'code', 'Code'));
    const message = toNonEmptyString(pick(record, 'message', 'Message'));
    const text = code !== undefined && message !== undefined ? `${code}: ${message}` : (code ?? message);
    if (text !== undefined && text !== '' && !containsInternalPath(text)) {
      out.push(text);
    }
  }
  return out;
}

function sanitizeMissing(raw: unknown): string[] {
  return toStringArray(raw).filter((entry) => entry !== '' && !containsInternalPath(entry));
}

/** Normalizes any output state spelling to its canonical form. Pure. */
export function normalizeOutputState(raw: unknown): OutputState {
  const text = typeof raw === 'string' ? raw.trim().toLowerCase() : '';
  switch (text) {
    case 'ready':
      return 'Ready';
    case 'generating':
      return 'Generating';
    case 'failed':
      return 'Failed';
    case 'partial':
      return 'Partial';
    case 'unavailable':
      return 'Unavailable';
    default:
      return 'Unavailable';
  }
}

/** Lowercase testid form of an output state (`Ready` → `ready`). Pure. */
export function outputStateKey(state: OutputState): string {
  return state.toLowerCase();
}

/**
 * Normalizes backend `ExportJobStatus` names (plus the display spellings the
 * task uses) to the four export display states. Pure.
 */
export function normalizeExportDisplayState(raw: unknown): ExportDisplayState {
  const text = typeof raw === 'string' ? raw.trim().toLowerCase() : '';
  if (text === 'pending' || text === 'queued' || text === 'queue') {
    return 'queued';
  }
  if (text === 'running' || text === 'generating' || text === 'in-progress' || text === 'inprogress') {
    return 'generating';
  }
  if (text === 'completed' || text === 'ready' || text === 'succeeded' || text === 'success') {
    return 'ready';
  }
  if (text === 'failed' || text === 'cancelled' || text === 'canceled' || text === 'error') {
    return 'failed';
  }
  return 'queued';
}

/**
 * Normalizes any export format spelling (kebab wire names plus the stale
 * bundle Pascal names) to the kebab wire name. Returns undefined for
 * unknown values. Pure.
 */
export function normalizeExportFormat(raw: unknown): string | undefined {
  if (typeof raw !== 'string' || raw.trim() === '') {
    return undefined;
  }
  const text = raw.trim().toLowerCase().replace(/_/g, '-').replace(/\s+/g, '-');
  const collapsed = text.replace(/--+/g, '-');
  switch (collapsed) {
    case 'srt':
      return 'srt';
    case 'vtt':
    case 'webvtt':
    case 'web-vtt':
      return 'webvtt';
    case 'mp4':
    case 'wav':
    case 'mp3':
      return collapsed;
    case 'json-timeline':
    case 'jsontimeline':
    case 'timeline':
    case 'timeline-json':
      return 'json-timeline';
    case 'speaker-metadata':
    case 'speakermetadata':
    case 'speakers':
    case 'speaker-metadata-json':
      return 'speaker-metadata';
    case 'transcript':
    case 'transcript-json':
    case 'transcripts':
      return 'transcript';
    case 'translation':
    case 'translation-json':
    case 'translations':
      return 'translation';
    case 'quality-report':
    case 'qualityreport':
    case 'qc':
    case 'qc-report':
      return 'quality-report';
    default:
      return undefined;
  }
}

/** True when a format is in the backend allowlist (normalized). Pure. */
export function isAllowlistedFormat(format: string): boolean {
  const normalized = normalizeExportFormat(format);
  if (normalized === undefined) {
    return false;
  }
  return (EXPORT_FORMAT_ALLOWLIST as readonly string[]).includes(normalized);
}

/** True when a type is in the backend-mirrored allowlist. Pure. */
export function isAllowlistedType(type: string): boolean {
  return (EXPORT_TYPE_ALLOWLIST as readonly string[]).includes(type);
}

/** True when a scope is in the allowlist. Pure. */
export function isAllowlistedScope(scope: string): boolean {
  return (EXPORT_SCOPE_ALLOWLIST as readonly string[]).includes(scope);
}

/**
 * Formats offered for a type. Every type maps to the full backend
 * allowlist (the server validates `format` independently of the UX `type`;
 * `type` only shapes the derived `profile`). Returning the full set keeps
 * the dialog allowlisted without inventing per-type backend rules. Pure.
 */
export function formatsForType(type: string): readonly string[] {
  if (!isAllowlistedType(type)) {
    return [];
  }
  return EXPORT_FORMAT_ALLOWLIST;
}

/**
 * Scopes advertised for an output aggregate. `full` always; `segment-range`
 * when segments exist; `per-speaker` when the speakers asset is beyond
 * `unavailable`. Pure.
 */
export function advertisedScopes(output: OutputView | undefined): readonly string[] {
  if (output === undefined) {
    return ['full'];
  }
  const out: string[] = ['full'];
  if (output.completeness.total > 0) {
    out.push('segment-range');
  }
  const speakers = output.items.find((item) => item.kind === 'speakers');
  if (speakers !== undefined && speakers.state !== 'Unavailable') {
    out.push('per-speaker');
  }
  return out.filter((scope) => isAllowlistedScope(scope));
}

/**
 * Derives the backend `profile` kebab variant from UX type/scope
 * (`type` when scope is `full`, else `type-scope`). Normalized to
 * lowercase kebab, max 64, no traversal; empty normalizes to undefined.
 * Pure.
 */
export function profileForRequest(type: string, scope: string): string | undefined {
  const base = scope === 'full' || scope === '' ? type : `${type}-${scope}`;
  const kebab = base
    .trim()
    .toLowerCase()
    .replace(/[\s_]+/g, '-')
    .replace(/[^a-z0-9-]/g, '')
    .replace(/--+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 64)
    .replace(/-+$/g, '');
  if (kebab === '' || kebab.includes('/') || kebab.includes('\\') || kebab.includes('..')) {
    return undefined;
  }
  return kebab;
}

/**
 * Validates dialog parameters against the backend allowlists before submit.
 * Returns the wire body on success, or an error message for inline display
 * (no network call). The server remains authoritative and re-validates.
 * Pure.
 */
export function validateExportRequest(input: {
  readonly type: string;
  readonly scope: string;
  readonly runId: string;
  readonly format: string;
  readonly allowPartial: boolean;
  readonly advertisedScopes: readonly string[];
  readonly availableRunIds: readonly string[];
}): { readonly body: { readonly format: string; readonly profile: string | undefined; readonly allowPartial: boolean } } | { readonly error: string } {
  if (!isAllowlistedType(input.type)) {
    return { error: 'Select a valid export type.' };
  }
  if (!isAllowlistedScope(input.scope) || !input.advertisedScopes.includes(input.scope)) {
    return { error: 'Select a valid export scope.' };
  }
  const normalizedFormat = normalizeExportFormat(input.format);
  if (normalizedFormat === undefined || !isAllowlistedFormat(normalizedFormat)) {
    return { error: 'Select a valid export format.' };
  }
  const offered = formatsForType(input.type);
  if (!offered.includes(normalizedFormat)) {
    return { error: 'Select a valid export format.' };
  }
  if (input.runId !== '' && !input.availableRunIds.includes(input.runId)) {
    return { error: 'Select a valid processing run.' };
  }
  const profile = profileForRequest(input.type, input.scope);
  return { body: { format: normalizedFormat, profile, allowPartial: input.allowPartial } };
}

function parseCompleteness(raw: unknown): OutputCompletenessView {
  const record = toRecord(raw);
  const readyRaw = record !== undefined ? pick(record, 'ready', 'Ready') : undefined;
  const totalRaw = record !== undefined ? pick(record, 'total', 'Total') : undefined;
  const ready = typeof readyRaw === 'number' && Number.isFinite(readyRaw) ? Math.max(0, Math.round(readyRaw)) : 0;
  const total = typeof totalRaw === 'number' && Number.isFinite(totalRaw) ? Math.max(0, Math.round(totalRaw)) : 0;
  return { ready: Math.min(ready, Math.max(total, ready)), total };
}

function parseExportCompleteness(raw: unknown): ExportCompletenessView | undefined {
  if (typeof raw === 'string' && raw !== '') {
    try {
      const parsed = JSON.parse(raw) as unknown;
      const record = toRecord(parsed);
      if (record !== undefined) {
        const ready = pick(record, 'ready', 'Ready');
        const total = pick(record, 'total', 'Total');
        if (typeof ready === 'number' && typeof total === 'number') {
          return {
            ready: Math.max(0, Math.round(ready)),
            total: Math.max(0, Math.round(total)),
          };
        }
      }
    } catch {
      return undefined;
    }
    return undefined;
  }
  const record = toRecord(raw);
  if (record === undefined) {
    return undefined;
  }
  const ready = pick(record, 'ready', 'Ready');
  const total = pick(record, 'total', 'Total');
  if (typeof ready === 'number' && typeof total === 'number' && Number.isFinite(ready) && Number.isFinite(total)) {
    return { ready: Math.max(0, Math.round(ready)), total: Math.max(0, Math.round(total)) };
  }
  return undefined;
}

function parseFileSize(raw: unknown): number | undefined {
  const record = toRecord(raw);
  const candidates: readonly unknown[] = record !== undefined
    ? [pick(record, 'sizeBytes', 'SizeBytes', 'fileSizeBytes', 'FileSizeBytes', 'size', 'Size', 'bytes', 'Bytes')]
    : [raw];
  for (const candidate of candidates) {
    if (typeof candidate === 'number' && Number.isFinite(candidate) && candidate >= 0) {
      return Math.round(candidate);
    }
  }
  return undefined;
}

const OUTPUT_ITEM_LABELS: Record<OutputItemKind, string> = {
  video: 'Video',
  audio: 'Audio',
  subtitles: 'Subtitles',
  transcript: 'Transcript',
  translation: 'Translation',
  timeline: 'Timeline',
  speakers: 'Speakers',
  qc: 'Quality report',
};

function parseAssetEntry(raw: unknown, kind: OutputItemKind): OutputItemView | undefined {
  const record = toRecord(raw);
  if (record === undefined) {
    return undefined;
  }
  const state = normalizeOutputState(pick(record, 'state', 'State', 'generationState', 'GenerationState'));
  const reason = toNonEmptyString(pick(record, 'reason', 'Reason'));
  const safeReason = reason !== undefined && !containsInternalPath(reason) ? reason : undefined;
  const missing = sanitizeMissing(pick(record, 'missing', 'Missing'));
  const completenessRaw = pick(record, 'completeness', 'Completeness');
  const completeness = completenessRaw !== undefined && completenessRaw !== null ? parseCompleteness(completenessRaw) : undefined;
  const urlRaw = toNonEmptyString(pick(record, 'downloadUrl', 'DownloadUrl', 'signedUrl', 'SignedUrl', 'url', 'Url'));
  const downloadUrl = urlRaw !== undefined && isSafeDisplayUrl(urlRaw) ? urlRaw : undefined;
  return {
    kind,
    label: OUTPUT_ITEM_LABELS[kind],
    state,
    reason: safeReason,
    missing,
    completeness,
    downloadUrl,
    detail: undefined,
  };
}

function parseQcEntry(raw: unknown): OutputItemView | undefined {
  const record = toRecord(raw);
  if (record === undefined) {
    return undefined;
  }
  const state = normalizeOutputState(pick(record, 'state', 'State', 'generationState', 'GenerationState'));
  const summary = toNonEmptyString(pick(record, 'summary', 'Summary'));
  const safeSummary = summary !== undefined && !containsInternalPath(summary) ? summary : undefined;
  const urlRaw = toNonEmptyString(pick(record, 'issuesUrl', 'IssuesUrl', 'downloadUrl', 'DownloadUrl', 'signedUrl', 'SignedUrl'));
  const downloadUrl = urlRaw !== undefined && isSafeDisplayUrl(urlRaw) ? urlRaw : undefined;
  return {
    kind: 'qc',
    label: OUTPUT_ITEM_LABELS['qc'],
    state,
    reason: undefined,
    missing: sanitizeMissing(pick(record, 'missing', 'Missing')),
    completeness: undefined,
    downloadUrl,
    detail: safeSummary,
  };
}

/**
 * Parses the output readiness aggregate defensively. Unknown shapes fall
 * back to `Unavailable` with zero completeness (never throws). Internal
 * paths are dropped, never rendered. Pure.
 */
export function parseOutput(raw: unknown): OutputView {
  const root = toRecord(raw) ?? {};
  const state = normalizeOutputState(pick(root, 'state', 'State', 'generationState', 'GenerationState'));
  const reasonRaw = toNonEmptyString(pick(root, 'reason', 'Reason'));
  const reason = reasonRaw !== undefined && !containsInternalPath(reasonRaw) ? reasonRaw : undefined;
  const completeness = parseCompleteness(pick(root, 'completeness', 'Completeness'));
  const progressRaw = toFiniteNumber(pick(root, 'progressApproximate', 'ProgressApproximate'));
  const progressApproximate =
    progressRaw === undefined ? undefined : Math.min(100, Math.max(0, Math.round(progressRaw * 10) / 10));
  const errorRaw = toNonEmptyString(pick(root, 'errorCode', 'ErrorCode'));
  const errorCode = errorRaw !== undefined && !containsInternalPath(errorRaw) ? errorRaw : undefined;
  const warnings = sanitizeWarnings(pick(root, 'warnings', 'Warnings'));
  const updatedAt = toNonEmptyString(pick(root, 'updatedAt', 'UpdatedAt'));
  const itemsRecord = toRecord(pick(root, 'items', 'Items')) ?? {};

  const items: OutputItemView[] = [];
  const scalarKinds: readonly OutputItemKind[] = ['video', 'audio', 'transcript', 'translation', 'timeline', 'speakers'];
  for (const kind of scalarKinds) {
    const entry = parseAssetEntry(pick(itemsRecord, kind, kind.charAt(0).toUpperCase() + kind.slice(1)), kind);
    if (entry !== undefined) {
      items.push(entry);
    } else {
      items.push({
        kind,
        label: OUTPUT_ITEM_LABELS[kind],
        state: state === 'Ready' ? 'Unavailable' : state,
        reason: undefined,
        missing: [],
        completeness: state === 'Partial' || state === 'Generating' ? completeness : undefined,
        downloadUrl: undefined,
        detail: undefined,
      });
    }
  }
  const subtitlesRaw = pick(itemsRecord, 'subtitles', 'Subtitles');
  if (Array.isArray(subtitlesRaw) && (subtitlesRaw as unknown[]).length > 0) {
    let index = 0;
    for (const entryRaw of subtitlesRaw as unknown[]) {
      const entry = parseAssetEntry(entryRaw, 'subtitles');
      if (entry !== undefined) {
        items.push({
          ...entry,
          label: index === 0 ? OUTPUT_ITEM_LABELS['subtitles'] : `${OUTPUT_ITEM_LABELS['subtitles']} ${String(index + 1)}`,
        });
        index += 1;
      }
    }
  } else if (state === 'Ready' || state === 'Partial') {
    items.push({
      kind: 'subtitles',
      label: OUTPUT_ITEM_LABELS['subtitles'],
      state: state === 'Ready' ? 'Unavailable' : 'Partial',
      reason: undefined,
      missing: state === 'Ready' ? [] : ['SEGMENT_PENDING'],
      completeness: state === 'Partial' ? completeness : undefined,
      downloadUrl: undefined,
      detail: undefined,
    });
  }
  const qc = parseQcEntry(pick(itemsRecord, 'qc', 'Qc', 'QC'));
  if (qc !== undefined) {
    items.push(qc);
  } else {
    items.push({
      kind: 'qc',
      label: OUTPUT_ITEM_LABELS['qc'],
      state: state === 'Ready' ? 'Unavailable' : state,
      reason: undefined,
      missing: [],
      completeness: undefined,
      downloadUrl: undefined,
      detail: undefined,
    });
  }
  return { state, reason, completeness, progressApproximate, errorCode, items, warnings, updatedAt };
}

/**
 * Quantified partial explanation (e.g. `96/100 segments — 4 incomplete`).
 * First-class partial state copy, never a bare error. Pure.
 */
export function partialExplanationFor(output: OutputView): string {
  const { ready, total } = output.completeness;
  const remaining = Math.max(0, total - ready);
  const warnings = output.warnings.join(' ').toLowerCase();
  if (warnings.includes('qc-blocked') || warnings.includes('qc_blocked')) {
    return `${String(ready)}/${String(total)} segments — ${String(remaining)} failed QC, see Quality`;
  }
  if (warnings.includes('review-open') || warnings.includes('review_open')) {
    return `${String(ready)}/${String(total)} segments — ${String(remaining)} awaiting review, see Quality`;
  }
  return `${String(ready)}/${String(total)} segments — ${String(remaining)} incomplete, see Quality`;
}

/** Per-item partial explanation with its own completeness where present. Pure. */
export function partialExplanationForItem(item: OutputItemView, fallback: OutputCompletenessView): string {
  const completeness = item.completeness ?? fallback;
  const remaining = Math.max(0, completeness.total - completeness.ready);
  return `${String(completeness.ready)}/${String(completeness.total)} segments — ${String(remaining)} incomplete, see Quality`;
}

/** Parses one export list row defensively. Never throws. Pure. */
export function parseExport(raw: unknown): ExportView | undefined {
  const record = toRecord(raw);
  if (record === undefined) {
    return undefined;
  }
  const id = toNonEmptyString(pick(record, 'id', 'Id')) ?? '';
  if (id === '') {
    return undefined;
  }
  const projectId = toNonEmptyString(pick(record, 'projectId', 'ProjectId')) ?? '';
  const formatRaw = pick(record, 'format', 'Format');
  const normalizedFormat = normalizeExportFormat(formatRaw) ?? (typeof formatRaw === 'string' && formatRaw !== '' ? formatRaw : 'srt');
  const status = toNonEmptyString(pick(record, 'status', 'Status')) ?? 'Pending';
  const reasonRaw = toNonEmptyString(
    pick(record, 'reason', 'Reason', 'message', 'Message', 'errorCode', 'ErrorCode', 'error', 'Error'),
  );
  const failureReason = reasonRaw !== undefined && !containsInternalPath(reasonRaw) ? reasonRaw : undefined;
  return {
    id,
    projectId,
    format: normalizedFormat,
    status,
    displayState: normalizeExportDisplayState(status),
    isPartial: toBoolean(pick(record, 'isPartial', 'IsPartial')),
    createdAt: toNonEmptyString(pick(record, 'createdAt', 'CreatedAt')),
    completeness: parseExportCompleteness(pick(record, 'completenessJson', 'CompletenessJson', 'completeness', 'Completeness')),
    fileSizeBytes: parseFileSize(raw),
    failureReason,
  };
}

/** Parses an export page envelope (`{ items, ... }`). Skips bad rows. Pure. */
export function parseExports(raw: unknown): ExportView[] {
  const record = toRecord(raw);
  const items = record !== undefined ? pick(record, 'items', 'Items') : raw;
  if (!Array.isArray(items)) {
    return [];
  }
  const out: ExportView[] = [];
  for (const entry of items as unknown[]) {
    const parsed = parseExport(entry);
    if (parsed !== undefined) {
      out.push(parsed);
    }
  }
  return out.sort((a, b) => {
    const aTime = a.createdAt ?? '';
    const bTime = b.createdAt ?? '';
    if (aTime !== bTime) {
      return bTime.localeCompare(aTime);
    }
    return a.id.localeCompare(b.id);
  });
}

/** Parses a processing-run page envelope into selector rows. Pure. */
export function parseProcessingRuns(raw: unknown): ProcessingRunView[] {
  const record = toRecord(raw);
  const items = record !== undefined ? pick(record, 'items', 'Items') : raw;
  if (!Array.isArray(items)) {
    return [];
  }
  const out: ProcessingRunView[] = [];
  for (const entry of items as unknown[]) {
    const item = toRecord(entry);
    if (item === undefined) {
      continue;
    }
    const id = toNonEmptyString(pick(item, 'runId', 'RunId', 'id', 'Id')) ?? '';
    if (id === '') {
      continue;
    }
    out.push({ id, status: toNonEmptyString(pick(item, 'status', 'Status')) ?? '' });
  }
  return out;
}

/** Human file-size display where the backend provides bytes. Pure. */
export function formatFileSize(bytes: number | undefined): string | undefined {
  if (bytes === undefined || !Number.isFinite(bytes) || bytes < 0) {
    return undefined;
  }
  if (bytes < 1024) {
    return `${String(bytes)} B`;
  }
  const kb = bytes / 1024;
  if (kb < 1024) {
    return `${kb.toFixed(1)} KB`;
  }
  const mb = kb / 1024;
  if (mb < 1024) {
    return `${mb.toFixed(1)} MB`;
  }
  return `${(mb / 1024).toFixed(1)} GB`;
}

/** True for expired signed URLs (410). Pure. */
export function isExpiredError(error: { readonly code?: string; readonly status?: number } | undefined | null): boolean {
  if (error === undefined || error === null) {
    return false;
  }
  return error.code === 'URL_EXPIRED' || error.status === 410;
}

/** True for already-generating conflicts (409). Pure. */
export function isAlreadyGeneratingError(error: { readonly code?: string; readonly status?: number } | undefined | null): boolean {
  if (error === undefined || error === null) {
    return false;
  }
  return error.status === 409;
}

/** True for deleted rows (404). Pure. */
export function isNotFoundError(error: { readonly code?: string; readonly status?: number } | undefined | null): boolean {
  if (error === undefined || error === null) {
    return false;
  }
  return error.status === 404 || (typeof error.code === 'string' && error.code.includes('NOT_FOUND'));
}

/** Non-color icon glyph for an output state (text, never color alone). Pure. */
export function iconForOutputState(state: OutputState): string {
  switch (state) {
    case 'Ready':
      return '✓';
    case 'Generating':
      return '↻';
    case 'Failed':
      return '✕';
    case 'Partial':
      return '◐';
    case 'Unavailable':
      return '○';
  }
}

/** Non-color pattern name for an output state (shape, never color alone). Pure. */
export function patternForOutputState(state: OutputState): string {
  switch (state) {
    case 'Ready':
      return 'solid-fill';
    case 'Generating':
      return 'dotted-block';
    case 'Failed':
      return 'crosshatch-block';
    case 'Partial':
      return 'diagonal-stripes';
    case 'Unavailable':
      return 'hollow-block';
  }
}

/** UI label for an output state (ready/generating/failed/partial/unavailable). Pure. */
export function labelForOutputState(state: OutputState): string {
  switch (state) {
    case 'Ready':
      return 'ready';
    case 'Generating':
      return 'generating';
    case 'Failed':
      return 'failed';
    case 'Partial':
      return 'partial';
    case 'Unavailable':
      return 'unavailable';
  }
}

/** Non-color icon glyph for an export display state. Pure. */
export function iconForExportState(state: ExportDisplayState): string {
  switch (state) {
    case 'ready':
      return '✓';
    case 'generating':
      return '↻';
    case 'queued':
      return '○';
    case 'failed':
      return '✕';
  }
}

/** Non-color pattern name for an export display state. Pure. */
export function patternForExportState(state: ExportDisplayState): string {
  switch (state) {
    case 'ready':
      return 'solid-fill';
    case 'generating':
      return 'dotted-block';
    case 'queued':
      return 'hollow-block';
    case 'failed':
      return 'crosshatch-block';
  }
}
