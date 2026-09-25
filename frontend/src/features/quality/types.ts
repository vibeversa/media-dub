/**
 * Quality-control domain view (Task 032).
 *
 * Pure parsing + derivation over the Task 008/009 contracts:
 * - `GET /projects/{id}/segments` rows carry `qualityCodes[]`, `reviewStatus`,
 *   `syncStatus`, and timing (the evidence-backed source for issues).
 * - `GET /projects/{id}/quality` carries counts-only `{ blockedCount,
 *   failedCount, codes[] }` (never payload text, by backend contract).
 * - `GET /projects/{id}/workspace` carries the run status (pending detection)
 *   plus `allowedActions` (retry-link gating).
 *
 * The quality projection is counts-only and never sufficient for issue
 * bodies, so issues are synthesized from segment rows: one issue per
 * `(segment, code)`. Status mapping mirrors the backend
 * `QualityControlService` verdict ordering (Blocked > ManualReviewRequired >
 * RetryRequired > PassWithWarnings > Pass); unknown codes fall back to a
 * generic warning card and are never dropped. Metric values are displayed as
 * provided (segment timing / sync state); the client computes no scores or
 * thresholds beyond the display-only status mapping.
 *
 * Texts render as plain text only.
 */

export const QUALITY_PAGE_SIZE = 200;

export const QUALITY_MAX_PAGES = 5;

export const QUALITY_RENDER_LIMIT = 50;

export type QualityBackendStatus =
  | 'Pass'
  | 'PassWithWarnings'
  | 'RetryRequired'
  | 'ManualReviewRequired'
  | 'Blocked';

export const QUALITY_STATUSES: readonly QualityBackendStatus[] = [
  'Pass',
  'PassWithWarnings',
  'RetryRequired',
  'ManualReviewRequired',
  'Blocked',
];

export type QualitySeverity = 'Info' | 'Warning' | 'Error' | 'Blocking';

export type QualityScope = 'segment' | 'project' | 'run';

export type QualityGroupMode = 'segment' | 'code';

export interface QualityFilters {
  readonly severity: string;
  readonly status: string;
  readonly scope: string;
  readonly group: QualityGroupMode;
}

export const EMPTY_QUALITY_FILTERS: QualityFilters = {
  severity: '',
  status: '',
  scope: '',
  group: 'segment',
};

export function hasActiveQualityFilters(filters: QualityFilters): boolean {
  return filters.severity !== '' || filters.status !== '' || filters.scope !== '';
}

export interface QualitySegmentView {
  readonly id: string;
  readonly sequence: number;
  readonly startMs: number;
  readonly endMs: number;
  readonly speakerId: string | undefined;
  readonly reviewStatus: string | undefined;
  readonly qualityCodes: readonly string[];
  readonly syncStatus: string | undefined;
  readonly artifactId: string | undefined;
  readonly artifactUrl: string | undefined;
  readonly reviewId: string | undefined;
}

export interface QualityIssueActions {
  readonly canJump: boolean;
  readonly canOpenReview: boolean;
  readonly canRetry: boolean;
  readonly retryReason: string | undefined;
}

export interface QualityIssueView {
  readonly id: string;
  readonly code: string;
  readonly severity: QualitySeverity;
  readonly status: QualityBackendStatus;
  readonly scope: QualityScope;
  readonly segmentId: string | undefined;
  readonly description: string;
  readonly suggestedAction: string;
  readonly startMs: number | undefined;
  readonly endMs: number | undefined;
  readonly metricName: string | undefined;
  readonly metricValue: string | undefined;
  readonly metricUnit: string | undefined;
  readonly artifactId: string | undefined;
  readonly artifactUrl: string | undefined;
  readonly reviewId: string | undefined;
  readonly actions: QualityIssueActions;
}

export interface QualitySummaryView {
  readonly passed: number;
  readonly warning: number;
  readonly retry: number;
  readonly review: number;
  readonly blocked: number;
  readonly totalIssues: number;
  readonly totalSegments: number;
  readonly cleanSegments: number;
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

/**
 * Display-only status mapping for a QC code. Mirrors the backend verdict
 * ordering: stale metadata retries, sync failures need review, gap/drift /
 * silence / crossfade warn, everything else blocks. Unknown codes warn
 * generically (never dropped, never crash). Pure.
 */
export function statusForCode(code: string, reviewStatus?: string, syncStatus?: string): QualityBackendStatus {
  const upper = code.trim().toUpperCase();
  if (upper === '') {
    return 'PassWithWarnings';
  }
  if (upper.includes('STALE')) {
    return 'RetryRequired';
  }
  if (upper.includes('SYNC_FAILURE')) {
    return 'ManualReviewRequired';
  }
  if (
    upper === 'QC_GAP' ||
    upper.includes('GAP') ||
    upper.includes('DRIFT') ||
    upper.includes('SILENCE') ||
    upper.includes('CROSSFADE')
  ) {
    // Silence-boundary / crossfade / gap / drift are backend warnings.
    // A sync issue alongside a warning code still escalates to review.
    const syncBad =
      syncStatus !== undefined &&
      syncStatus !== '' &&
      !/acceptable/i.test(syncStatus);
    if (syncBad && upper.includes('SYNC')) {
      return 'ManualReviewRequired';
    }
    void reviewStatus;
    return 'PassWithWarnings';
  }
  if (upper.includes('RETRY')) {
    return 'RetryRequired';
  }
  if (upper.includes('REVIEW') && !upper.includes('UNRESOLVED')) {
    return 'ManualReviewRequired';
  }
  // Backend warnings above are the only non-blocking codes; every other
  // known QC code (missing/overflow/overlap/voice/terminology/checksum /
  // corrupt / rate / channel / loudness / clipping / peak / routing /
  // placement / attenuation / unresolved review) blocks the render. Unknown
  // `QC_*` codes fall through to a generic warning (never dropped).
  const knownWarning =
    upper.includes('NOISY') ||
    upper.includes('LOW_CONF') ||
    upper.includes('TERMINOLOGY_WARN') ||
    upper.includes('CUSTOM') ||
    upper.includes('UNKNOWN');
  if (knownWarning) {
    return 'PassWithWarnings';
  }
  if (
    upper.includes('BLOCK') ||
    upper.includes('MISS') ||
    upper.includes('OVER') ||
    upper.includes('VOICE') ||
    upper.includes('CHECK') ||
    upper.includes('CORRUPT') ||
    upper.includes('RATE') ||
    upper.includes('CHANNEL') ||
    upper.includes('LOUD') ||
    upper.includes('CLIP') ||
    upper.includes('PEAK') ||
    upper.includes('ROUT') ||
    upper.includes('PLACE') ||
    upper.includes('ATTEN') ||
    upper.includes('UNRESOLVED') ||
    upper.includes('INVALID') ||
    upper.includes('TERMINOLOGY') ||
    upper.includes('EMPTY') ||
    upper.includes('NO_SEGMENTS')
  ) {
    return 'Blocked';
  }
  return 'PassWithWarnings';
}

/** Severity for a backend QC status (backend `Severity*` contract). Pure. */
export function severityForStatus(status: QualityBackendStatus): QualitySeverity {
  switch (status) {
    case 'Pass':
      return 'Info';
    case 'PassWithWarnings':
      return 'Warning';
    case 'RetryRequired':
      return 'Warning';
    case 'ManualReviewRequired':
      return 'Error';
    case 'Blocked':
      return 'Blocking';
  }
}

/** Human description for a code (generic fallback for unknown codes). Pure. */
export function descriptionForCode(code: string, segmentId?: string): string {
  const trimmed = code.trim();
  const where = segmentId !== undefined && segmentId !== '' ? ` on segment ${segmentId}` : '';
  switch (trimmed.toUpperCase()) {
    case 'QC_SYNC_FAILURE':
      return `Timing is out of tolerance${where}; manual review required.`;
    case 'QC_STALE_METADATA':
      return `Upstream attempts have not settled${where}; QC will retry.`;
    case 'QC_GAP':
      return `Silence gap detected${where}; verify the pause is intentional.`;
    case 'QC_DRIFT':
      return `Mixed duration drifts from source${where}; verify alignment.`;
    case 'QC_SILENCE':
    case 'QC_SILENCE_BOUNDARY':
      return `Near-silence detected${where}; verify boundaries are clean.`;
    case 'QC_CROSSFADE':
      return `Overlap window is silent${where}; verify the crossfade kept audio.`;
    case 'QC_UNRESOLVED_REVIEW':
      return `Open review blocks the render${where}; resolve it first.`;
    case 'QC_OVERFLOW':
      return `Segment ends past the source duration${where}; trim or retime upstream.`;
    case 'QC_MISSING_TRANSCRIPT':
      return `No selected transcript${where}; transcription must complete.`;
    case 'QC_EMPTY_TRANSLATION':
      return `No selected translation${where}; translation must complete.`;
    case 'QC_MISSING_VOICE':
      return `No assigned voice${where}; assign a voice upstream.`;
    case 'QC_MISSING_AUDIO':
      return `No generated final audio${where}; synthesis must complete.`;
    default:
      return `${trimmed !== '' ? trimmed : 'Unknown QC code'} flagged${where}; shown generically (code plus context, never dropped).`;
  }
}

/** Suggested next step for a status (display-only guidance). Pure. */
export function suggestedActionForStatus(status: QualityBackendStatus): string {
  switch (status) {
    case 'Blocked':
      return 'Resolve the blocking flag, then retry the segment or run.';
    case 'ManualReviewRequired':
      return 'Open in review and resolve the timing/content flag.';
    case 'RetryRequired':
      return 'Retry once upstream attempts settle.';
    case 'PassWithWarnings':
      return 'Verify the warning, then continue or open in review.';
    case 'Pass':
      return 'No action needed.';
  }
}

/** Parses one segment list row defensively. Never throws. Pure. */
export function parseQualitySegment(raw: unknown): QualitySegmentView | undefined {
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
    speakerId: toNonEmptyString(pick(record, 'speakerId', 'SpeakerId')),
    reviewStatus: toNonEmptyString(pick(record, 'reviewStatus', 'ReviewStatus')),
    qualityCodes: toStringArray(pick(record, 'qualityCodes', 'QualityCodes')),
    syncStatus: toNonEmptyString(pick(record, 'syncStatus', 'SyncStatus')),
    artifactId: toNonEmptyString(pick(record, 'artifactId', 'ArtifactId', 'previewArtifactId', 'PreviewArtifactId')),
    artifactUrl: toNonEmptyString(
      pick(record, 'artifactUrl', 'ArtifactUrl', 'signedUrl', 'SignedUrl', 'downloadUrl', 'DownloadUrl'),
    ),
    reviewId: toNonEmptyString(pick(record, 'reviewId', 'ReviewId')),
  };
}

/** Parses a `SegmentListResponse` page (`{ items, ... }`). Skips bad rows. Pure. */
export function parseQualitySegments(raw: unknown): QualitySegmentView[] {
  const record = toRecord(raw);
  const items = record !== undefined ? pick(record, 'items', 'Items') : raw;
  if (!Array.isArray(items)) {
    return [];
  }
  const out: QualitySegmentView[] = [];
  for (const entry of items as unknown[]) {
    const parsed = parseQualitySegment(entry);
    if (parsed !== undefined) {
      out.push(parsed);
    }
  }
  return out.sort((a, b) => a.startMs - b.startMs || a.sequence - b.sequence);
}

function metricForCode(code: string, segment: QualitySegmentView): { name: string; value: string; unit: string } | undefined {
  const upper = code.trim().toUpperCase();
  const durationMs = Math.max(0, segment.endMs - segment.startMs);
  if (upper.includes('GAP') || upper.includes('DRIFT') || upper.includes('SYNC')) {
    return { name: 'Segment window', value: `${String(segment.startMs)}–${String(segment.endMs)}`, unit: 'ms' };
  }
  if (upper.includes('OVERFLOW') || upper.includes('PEAK') || upper.includes('CLIP') || upper.includes('LOUD')) {
    return { name: 'Segment duration', value: String(durationMs), unit: 'ms' };
  }
  if (upper.includes('SILENCE') || upper.includes('CROSSFADE')) {
    return { name: 'Segment window', value: `${String(segment.startMs)}–${String(segment.endMs)}`, unit: 'ms' };
  }
  // Default metric is the segment timing as provided (no computed scores).
  return { name: 'Segment window', value: `${String(segment.startMs)}–${String(segment.endMs)}`, unit: 'ms' };
}

/**
 * Builds one issue per `(segment, code)`. Unknown codes become generic
 * warning cards (raw code + description, never dropped). Retry gating reads
 * the workspace `allowedActions`: `processing.retry` advertises the retry
 * link, otherwise the action is omitted with a reason. Pure.
 */
export function buildQualityIssues(
  segments: readonly QualitySegmentView[],
  allowedActions: readonly string[],
): QualityIssueView[] {
  const canRetryGlobal = allowedActions.some((entry) => entry === 'processing.retry');
  const out: QualityIssueView[] = [];
  for (const segment of segments) {
    const uniqueCodes = [...new Set(segment.qualityCodes.filter((code) => code !== ''))].sort();
    for (const code of uniqueCodes) {
      const status = statusForCode(code, segment.reviewStatus, segment.syncStatus);
      const severity = severityForStatus(status);
      const openReview = (segment.reviewStatus ?? '').toLowerCase() === 'open';
      const canJump = Number.isFinite(segment.startMs) && Number.isFinite(segment.endMs);
      const canOpenReview = openReview || segment.reviewId !== undefined;
      const metric = metricForCode(code, segment);
      const retryReason = canRetryGlobal ? undefined : 'Retry needs the processing.retry permission.';
      out.push({
        id: `qc-${segment.id}-${code.toLowerCase().replace(/[^a-z0-9]+/g, '-')}`,
        code,
        severity,
        status,
        scope: 'segment',
        segmentId: segment.id,
        description: descriptionForCode(code, segment.id),
        suggestedAction: suggestedActionForStatus(status),
        startMs: segment.startMs,
        endMs: segment.endMs,
        metricName: metric?.name,
        metricValue: metric?.value,
        metricUnit: metric?.unit,
        artifactId: segment.artifactId,
        artifactUrl: segment.artifactUrl,
        reviewId: segment.reviewId,
        actions: {
          canJump,
          canOpenReview,
          canRetry: canRetryGlobal,
          retryReason,
        },
      });
    }
  }
  return sortQualityIssuesBlockedFirst(out);
}

/** Blocked-first ordering (blocked banner + pinned list). Pure. */
export function sortQualityIssuesBlockedFirst(issues: readonly QualityIssueView[]): QualityIssueView[] {
  const rank = (status: QualityBackendStatus): number => {
    switch (status) {
      case 'Blocked':
        return 0;
      case 'ManualReviewRequired':
        return 1;
      case 'RetryRequired':
        return 2;
      case 'PassWithWarnings':
        return 3;
      case 'Pass':
        return 4;
    }
  };
  return [...issues].sort((a, b) => rank(a.status) - rank(b.status) || a.id.localeCompare(b.id));
}

/**
 * Summarizes issues plus clean-segment passed count. `totalSegments` is the
 * segment denominator; `passed` counts clean segments, the other four count
 * issues (so `warning+retry+review+blocked === totalIssues`). Pure.
 */
export function summarizeQuality(
  issues: readonly QualityIssueView[],
  totalSegments: number,
): QualitySummaryView {
  let warning = 0;
  let retry = 0;
  let review = 0;
  let blocked = 0;
  const flaggedSegments = new Set<string>();
  for (const issue of issues) {
    switch (issue.status) {
      case 'PassWithWarnings':
        warning += 1;
        break;
      case 'RetryRequired':
        retry += 1;
        break;
      case 'ManualReviewRequired':
        review += 1;
        break;
      case 'Blocked':
        blocked += 1;
        break;
      case 'Pass':
        break;
    }
    if (issue.segmentId !== undefined) {
      flaggedSegments.add(issue.segmentId);
    }
  }
  const cleanSegments = Math.max(0, totalSegments - flaggedSegments.size);
  return {
    passed: cleanSegments,
    warning,
    retry,
    review,
    blocked,
    totalIssues: issues.length,
    totalSegments,
    cleanSegments,
  };
}

/** In-memory filter (severity exact, status exact, scope exact). Pure. */
export function filterQualityIssues(
  issues: readonly QualityIssueView[],
  filters: QualityFilters,
): QualityIssueView[] {
  const severity = filters.severity.trim().toLowerCase();
  const status = filters.status.trim().toLowerCase();
  const scope = filters.scope.trim().toLowerCase();
  return issues.filter((issue) => {
    if (severity !== '' && issue.severity.toLowerCase() !== severity) {
      return false;
    }
    if (status !== '' && issue.status.toLowerCase() !== status) {
      return false;
    }
    if (scope !== '' && issue.scope.toLowerCase() !== scope) {
      return false;
    }
    return true;
  });
}

export interface QualityGroup {
  readonly key: string;
  readonly label: string;
  readonly issues: readonly QualityIssueView[];
}

/** Groups issues by segment or by code (stable key order). Pure. */
export function groupQualityIssues(
  issues: readonly QualityIssueView[],
  mode: QualityGroupMode,
): QualityGroup[] {
  const buckets = new Map<string, QualityIssueView[]>();
  for (const issue of issues) {
    const key = mode === 'code' ? issue.code : (issue.segmentId ?? 'project');
    const bucket = buckets.get(key);
    if (bucket === undefined) {
      buckets.set(key, [issue]);
    } else {
      bucket.push(issue);
    }
  }
  const groups: QualityGroup[] = [];
  for (const [key, bucket] of [...buckets.entries()].sort((a, b) => a[0].localeCompare(b[0]))) {
    const sorted = sortQualityIssuesBlockedFirst(bucket);
    groups.push({
      key,
      label: mode === 'code' ? `Code ${key} (${String(sorted.length)})` : `Segment ${key} (${String(sorted.length)})`,
      issues: sorted,
    });
  }
  return groups;
}

const FILTER_PARAM_KEYS = ['severity', 'status', 'scope', 'group'] as const;

/** Parses URL search params into quality filters (shareable, no signed URLs). Pure. */
export function qualityFiltersFromSearchParams(params: URLSearchParams): QualityFilters {
  const groupRaw = (params.get('group') ?? '').trim().toLowerCase();
  return {
    severity: params.get('severity') ?? '',
    status: params.get('status') ?? '',
    scope: params.get('scope') ?? '',
    group: groupRaw === 'code' ? 'code' : 'segment',
  };
}

/** Serializes filters to URL search params (omits empties, never URLs). Pure. */
export function qualityFiltersToSearchParams(filters: QualityFilters): URLSearchParams {
  const params = new URLSearchParams();
  if (filters.severity.trim() !== '') {
    params.set('severity', filters.severity.trim());
  }
  if (filters.status.trim() !== '') {
    params.set('status', filters.status.trim());
  }
  if (filters.scope.trim() !== '') {
    params.set('scope', filters.scope.trim());
  }
  if (filters.group === 'code') {
    params.set('group', 'code');
  }
  void FILTER_PARAM_KEYS;
  return params;
}

/** Non-color icon glyph for a QC status (text, never color alone). Pure. */
export function iconForQualityStatus(status: QualityBackendStatus): string {
  switch (status) {
    case 'Pass':
      return '✓';
    case 'PassWithWarnings':
      return '▲';
    case 'RetryRequired':
      return '↻';
    case 'ManualReviewRequired':
      return '◉';
    case 'Blocked':
      return '■';
  }
}

/** Non-color icon glyph for a severity (text, never color alone). Pure. */
export function iconForQualitySeverity(severity: QualitySeverity): string {
  switch (severity) {
    case 'Info':
      return '○';
    case 'Warning':
      return '▲';
    case 'Error':
      return '◉';
    case 'Blocking':
      return '■';
  }
}

/** Non-color pattern name for a QC status (shape, never color alone). Pure. */
export function patternForQualityStatus(status: QualityBackendStatus): string {
  switch (status) {
    case 'Pass':
      return 'solid-fill';
    case 'PassWithWarnings':
      return 'diagonal-stripes';
    case 'RetryRequired':
      return 'dotted-block';
    case 'ManualReviewRequired':
      return 'crosshatch-block';
    case 'Blocked':
      return 'hatched-block';
  }
}

/** UI label for a QC status (passed/warning/retry/review/blocked). Pure. */
export function labelForQualityStatus(status: QualityBackendStatus): string {
  switch (status) {
    case 'Pass':
      return 'passed';
    case 'PassWithWarnings':
      return 'warning';
    case 'RetryRequired':
      return 'retry';
    case 'ManualReviewRequired':
      return 'review';
    case 'Blocked':
      return 'blocked';
  }
}

/** UI label for a severity. Pure. */
export function labelForQualitySeverity(severity: QualitySeverity): string {
  switch (severity) {
    case 'Info':
      return 'info';
    case 'Warning':
      return 'warning';
    case 'Error':
      return 'error';
    case 'Blocking':
      return 'blocking';
  }
}

/** True while the run is active (QC pending shows a skeleton, not zeros). Pure. */
export function isQualityPendingRun(runStatus: string | undefined): boolean {
  if (runStatus === undefined || runStatus === '') {
    return false;
  }
  const normalized = runStatus.trim().toLowerCase();
  return normalized === 'pending' || normalized === 'running' || normalized === 'cancelling';
}

/** True for expired signed URLs (410). Pure. */
export function isQualityExpiredError(
  error: { readonly code?: string; readonly status?: number } | undefined | null,
): boolean {
  if (error === undefined || error === null) {
    return false;
  }
  return error.code === 'URL_EXPIRED' || error.status === 410;
}

/** Parses the counts-only quality projection defensively. Never throws. Pure. */
export function parseQualityProjection(raw: unknown): { blocked: number; failed: number; codes: readonly string[] } {
  const record = toRecord(raw);
  if (record === undefined) {
    return { blocked: 0, failed: 0, codes: [] };
  }
  const blockedRaw = pick(record, 'blockedCount', 'BlockedCount', 'blocked', 'Blocked');
  const failedRaw = pick(record, 'failedCount', 'FailedCount', 'failed', 'Failed');
  const blocked = typeof blockedRaw === 'number' && Number.isFinite(blockedRaw) ? Math.max(0, Math.round(blockedRaw)) : 0;
  const failed = typeof failedRaw === 'number' && Number.isFinite(failedRaw) ? Math.max(0, Math.round(failedRaw)) : 0;
  return { blocked, failed, codes: toStringArray(pick(record, 'codes', 'Codes')) };
}
