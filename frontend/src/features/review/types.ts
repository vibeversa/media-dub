/**
 * Manual review studio domain view (Task 031).
 *
 * Pure parsing + derivation over the Task 011 review contract
 * (`GET /projects/{id}/reviews` list rows + `GET /reviews/{id}/context`
 * eleven-section aggregate) and the Task 009 segment shapes. All parsing is
 * defensive (camelCase/PascalCase, missing sections fall back to neutral
 * defaults, never throws for list rows). Texts render as plain text only.
 */

export const REVIEW_PAGE_SIZE = 50;

export const REVIEW_MAX_PAGES = 10;

export const REVIEW_MAX_REASON_LENGTH = 500;

export const REVIEW_MAX_EDIT_LENGTH = 5000;

export const REVIEW_QUEUE_RENDER_LIMIT = 50;

export type DispositionAction = 'approve' | 'reject' | 'requeue' | 'resolve-with-edit';

export const DISPOSITION_ACTIONS: readonly DispositionAction[] = [
  'approve',
  'reject',
  'requeue',
  'resolve-with-edit',
];

/** Hardened endpoint per disposition (Task 011 service-owned idempotency). */
export function dispositionEndpointFor(action: DispositionAction): string {
  switch (action) {
    case 'approve':
      return 'resolve';
    case 'reject':
      return 'dismiss';
    case 'requeue':
      return 'reopen';
    case 'resolve-with-edit':
      return 'resolve-with-edit';
  }
}

/** True for actions that require a reason in the UI (reject/requeue). */
export function dispositionRequiresReason(action: DispositionAction): boolean {
  return action === 'reject' || action === 'requeue';
}

export interface ReviewFilters {
  readonly project: string;
  readonly severity: string;
  readonly status: string;
  readonly type: string;
  readonly speaker: string;
  readonly language: string;
  readonly age: string;
}

export const EMPTY_REVIEW_FILTERS: ReviewFilters = {
  project: '',
  severity: '',
  status: '',
  type: '',
  speaker: '',
  language: '',
  age: '',
};

export function hasActiveReviewFilters(filters: ReviewFilters): boolean {
  return (
    filters.project !== '' ||
    filters.severity !== '' ||
    filters.status !== '' ||
    filters.type !== '' ||
    filters.speaker !== '' ||
    filters.language !== '' ||
    filters.age !== ''
  );
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
 * Maps UI filters to the server-side query for
 * `GET /projects/{id}/reviews`. Every non-empty filter becomes a query
 * param (R1); empty values are omitted. `project` is the path segment and
 * never a query param.
 */
export function toReviewListQuery(filters: ReviewFilters): Record<string, string | number | undefined> {
  const query: Record<string, string | number | undefined> = {};
  const severity = filters.severity.trim();
  const status = filters.status.trim();
  const type = filters.type.trim();
  const speaker = filters.speaker.trim();
  const language = filters.language.trim();
  const age = filters.age.trim();
  if (severity !== '') {
    query['severity'] = severity;
  }
  if (status !== '') {
    query['status'] = status;
  }
  if (type !== '') {
    query['type'] = type;
  }
  if (speaker !== '') {
    query['speakerId'] = speaker;
  }
  if (language !== '') {
    query['language'] = language;
  }
  if (age !== '') {
    query['age'] = age;
  }
  return query;
}

const FILTER_PARAM_KEYS = ['project', 'severity', 'status', 'type', 'speaker', 'language', 'age'] as const;

/** Parses URL search params into review filters (shareable state). Pure. */
export function reviewFiltersFromSearchParams(params: URLSearchParams): ReviewFilters {
  const get = (key: string): string => params.get(key) ?? '';
  return {
    project: get('project'),
    severity: get('severity'),
    status: get('status'),
    type: get('type'),
    speaker: get('speaker'),
    language: get('language'),
    age: get('age'),
  };
}

/** Serializes filters to URL search params (omits empties). Pure. */
export function reviewFiltersToSearchParams(filters: ReviewFilters): URLSearchParams {
  const params = new URLSearchParams();
  for (const key of FILTER_PARAM_KEYS) {
    const value = filters[key].trim();
    if (value !== '') {
      params.set(key, value);
    }
  }
  return params;
}

export interface ReviewQueueItemView {
  readonly id: string;
  readonly projectId: string;
  readonly status: string;
  readonly reason: string;
  readonly type: string | undefined;
  readonly severity: string | undefined;
  readonly speakerId: string | undefined;
  readonly language: string | undefined;
  readonly createdAt: string;
  readonly resolvedAt: string | undefined;
}

/**
 * Builds a queue item from a `ReviewResponse` list row
 * (`{ id, projectId, status, reason, createdAt, resolvedAt }`). Never throws;
 * rows without an id are skipped by the caller. Pure.
 */
export function parseReviewQueueItem(raw: unknown): ReviewQueueItemView | undefined {
  const record = toRecord(raw);
  if (record === undefined) {
    return undefined;
  }
  const id = toNonEmptyString(pick(record, 'id', 'Id', 'reviewId', 'ReviewId')) ?? '';
  if (id === '') {
    return undefined;
  }
  const projectId = toNonEmptyString(pick(record, 'projectId', 'ProjectId')) ?? '';
  const status = toNonEmptyString(pick(record, 'status', 'Status')) ?? 'Open';
  const reason = typeof pick(record, 'reason', 'Reason') === 'string' ? (pick(record, 'reason', 'Reason') as string) : '';
  const createdAt = toNonEmptyString(pick(record, 'createdAt', 'CreatedAt')) ?? '';
  const resolvedRaw = pick(record, 'resolvedAt', 'ResolvedAt');
  const resolvedAt = typeof resolvedRaw === 'string' && resolvedRaw !== '' ? resolvedRaw : undefined;
  return {
    id,
    projectId,
    status,
    reason,
    type: toNonEmptyString(pick(record, 'type', 'Type', 'scopeType', 'ScopeType')),
    severity: toNonEmptyString(pick(record, 'severity', 'Severity')),
    speakerId: toNonEmptyString(pick(record, 'speakerId', 'SpeakerId')),
    language: toNonEmptyString(pick(record, 'language', 'Language', 'targetLanguage', 'TargetLanguage')),
    createdAt,
    resolvedAt,
  };
}

/** Parses a `PaginatedResult` page (`{ items, ... }`). Skips bad rows. Pure. */
export function parseReviewQueueItems(raw: unknown): ReviewQueueItemView[] {
  const record = toRecord(raw);
  const items = record !== undefined ? pick(record, 'items', 'Items') : raw;
  if (!Array.isArray(items)) {
    return [];
  }
  const out: ReviewQueueItemView[] = [];
  for (const entry of items as unknown[]) {
    const parsed = parseReviewQueueItem(entry);
    if (parsed !== undefined) {
      out.push(parsed);
    }
  }
  return out;
}

export interface ReviewVersionRow {
  readonly id: string;
  readonly text: string;
  readonly provider: string;
  readonly model: string;
  readonly isSelected: boolean;
  readonly createdAt: string;
}

export interface ReviewQcIssue {
  readonly id: string;
  readonly code: string;
  readonly severity: string;
  readonly message: string;
}

export interface ReviewHistoryEntry {
  readonly id: string;
  readonly action: string;
  readonly actor: string;
  readonly reason: string;
  readonly createdAt: string;
}

export interface ReviewContextView {
  readonly reviewId: string;
  readonly type: string;
  readonly severity: string;
  readonly status: string;
  readonly version: number;
  readonly projectId: string;
  readonly projectName: string;
  readonly runId: string;
  readonly runStatus: string;
  readonly segmentId: string | undefined;
  readonly segmentStartMs: number | undefined;
  readonly segmentEndMs: number | undefined;
  readonly segmentSpeakerId: string | undefined;
  readonly transcript: readonly ReviewVersionRow[];
  readonly translation: readonly ReviewVersionRow[];
  readonly selectedTranscriptVersionId: string | undefined;
  readonly selectedTranslationVersionId: string | undefined;
  readonly selectionVersion: number;
  readonly voiceSpeakerId: string | undefined;
  readonly voiceProfileId: string | undefined;
  readonly voiceId: string | undefined;
  readonly voiceConsentState: string;
  readonly audioArtifactId: string | undefined;
  readonly syncOffsetMs: number | undefined;
  readonly syncDrift: boolean;
  readonly qcIssues: readonly ReviewQcIssue[];
  readonly qcEvidenceIds: readonly string[];
  readonly allowedActions: readonly string[];
  readonly canResolve: boolean;
  readonly canEdit: boolean;
  readonly history: readonly ReviewHistoryEntry[];
  readonly truncated: boolean;
}

function parseVersionRows(raw: unknown, textKeys: readonly string[]): ReviewVersionRow[] {
  if (!Array.isArray(raw)) {
    return [];
  }
  const out: ReviewVersionRow[] = [];
  for (const entry of raw as unknown[]) {
    const record = toRecord(entry);
    if (record === undefined) {
      continue;
    }
    const id = toNonEmptyString(pick(record, 'id', 'Id')) ?? '';
    if (id === '') {
      continue;
    }
    let text = '';
    for (const key of textKeys) {
      const candidate = record[key];
      if (typeof candidate === 'string' && candidate !== '') {
        text = candidate;
        break;
      }
    }
    out.push({
      id,
      text,
      provider: toNonEmptyString(pick(record, 'provider', 'Provider')) ?? 'unknown',
      model: toNonEmptyString(pick(record, 'model', 'Model')) ?? 'unknown',
      isSelected: pick(record, 'isSelected', 'IsSelected') === true,
      createdAt: toNonEmptyString(pick(record, 'createdAt', 'CreatedAt')) ?? '',
    });
  }
  return out;
}

function parseQcIssues(raw: unknown): ReviewQcIssue[] {
  if (!Array.isArray(raw)) {
    return [];
  }
  const out: ReviewQcIssue[] = [];
  for (const entry of raw as unknown[]) {
    const record = toRecord(entry);
    if (record === undefined) {
      continue;
    }
    const id = toNonEmptyString(pick(record, 'id', 'Id')) ?? '';
    if (id === '') {
      continue;
    }
    out.push({
      id,
      code: toNonEmptyString(pick(record, 'code', 'Code')) ?? 'unknown',
      severity: toNonEmptyString(pick(record, 'severity', 'Severity')) ?? 'medium',
      message: typeof pick(record, 'message', 'Message') === 'string' ? (pick(record, 'message', 'Message') as string) : '',
    });
  }
  return out;
}

function parseHistoryEntries(raw: unknown): ReviewHistoryEntry[] {
  if (!Array.isArray(raw)) {
    return [];
  }
  const out: ReviewHistoryEntry[] = [];
  for (const entry of raw as unknown[]) {
    const record = toRecord(entry);
    if (record === undefined) {
      continue;
    }
    const id =
      toNonEmptyString(pick(record, 'id', 'Id', 'decisionId', 'DecisionId')) ??
      `history-${String(out.length)}`;
    out.push({
      id,
      action: toNonEmptyString(pick(record, 'type', 'Type', 'action', 'Action', 'decisionType', 'DecisionType')) ?? 'unknown',
      actor: toNonEmptyString(pick(record, 'reviewer', 'Reviewer', 'actor', 'Actor')) ?? 'unknown',
      reason: typeof pick(record, 'reason', 'Reason') === 'string' ? (pick(record, 'reason', 'Reason') as string) : '',
      createdAt: toNonEmptyString(pick(record, 'createdAt', 'CreatedAt')) ?? '',
    });
  }
  return out;
}

/**
 * Builds the single-screen context view from a `ReviewContextResponse`
 * (eleven sections: item, project, run, segment, versions, voice, audio,
 * sync, qc, actions/permissions, history). Missing sections fall back to
 * neutral defaults. Never throws. Pure.
 */
export function parseReviewContext(raw: unknown): ReviewContextView | undefined {
  const record = toRecord(raw);
  if (record === undefined) {
    return undefined;
  }
  const item = toRecord(pick(record, 'item', 'Item'));
  const project = toRecord(pick(record, 'project', 'Project'));
  const run = toRecord(pick(record, 'run', 'Run'));
  const segment = toRecord(pick(record, 'segment', 'Segment'));
  const versions = toRecord(pick(record, 'versions', 'Versions'));
  const voice = toRecord(pick(record, 'voice', 'Voice'));
  const audio = toRecord(pick(record, 'audio', 'Audio'));
  const sync = toRecord(pick(record, 'sync', 'Sync'));
  const qc = toRecord(pick(record, 'qc', 'Qc', 'QC'));
  const actions = toRecord(pick(record, 'actions', 'Actions'));
  const permissions = toRecord(pick(record, 'permissions', 'Permissions'));

  const reviewId = toNonEmptyString(pick(item, 'id', 'Id')) ?? toNonEmptyString(pick(record, 'reviewId', 'ReviewId')) ?? '';
  if (reviewId === '') {
    return undefined;
  }
  const transcript = parseVersionRows(pick(versions, 'transcript', 'Transcript'), ['text', 'Text']);
  const translation = parseVersionRows(pick(versions, 'translation', 'Translation'), ['primaryText', 'PrimaryText', 'text', 'Text']);
  const history = parseHistoryEntries(pick(record, 'history', 'History'));

  return {
    reviewId,
    type: toNonEmptyString(pick(item, 'type', 'Type')) ?? 'unknown',
    severity: toNonEmptyString(pick(item, 'severity', 'Severity')) ?? 'medium',
    status: toNonEmptyString(pick(item, 'status', 'Status')) ?? 'Open',
    version: toFiniteNumber(pick(item, 'version', 'Version')) ?? 0,
    projectId: toNonEmptyString(pick(project, 'id', 'Id')) ?? '',
    projectName: toNonEmptyString(pick(project, 'name', 'Name')) ?? '',
    runId: toNonEmptyString(pick(run, 'id', 'Id')) ?? '',
    runStatus: toNonEmptyString(pick(run, 'status', 'Status')) ?? 'unknown',
    segmentId: toNonEmptyString(pick(segment, 'id', 'Id')),
    segmentStartMs: toFiniteNumber(pick(segment, 'startMs', 'StartMs')),
    segmentEndMs: toFiniteNumber(pick(segment, 'endMs', 'EndMs')),
    segmentSpeakerId: toNonEmptyString(pick(segment, 'speakerId', 'SpeakerId')),
    transcript,
    translation,
    selectedTranscriptVersionId: toNonEmptyString(
      pick(versions, 'selectedTranscriptVersionId', 'SelectedTranscriptVersionId'),
    ),
    selectedTranslationVersionId: toNonEmptyString(
      pick(versions, 'selectedTranslationVersionId', 'SelectedTranslationVersionId'),
    ),
    selectionVersion: toFiniteNumber(pick(versions, 'selectionVersion', 'SelectionVersion')) ?? 0,
    voiceSpeakerId: toNonEmptyString(pick(voice, 'speakerId', 'SpeakerId')),
    voiceProfileId: toNonEmptyString(pick(voice, 'voiceProfileId', 'VoiceProfileId')),
    voiceId: toNonEmptyString(pick(voice, 'voiceId', 'VoiceId')),
    voiceConsentState: toNonEmptyString(pick(voice, 'consentState', 'ConsentState')) ?? 'not_applicable',
    audioArtifactId: toNonEmptyString(pick(audio, 'previewArtifactId', 'PreviewArtifactId')),
    syncOffsetMs: toFiniteNumber(pick(sync, 'offsetMs', 'OffsetMs')),
    syncDrift: pick(sync, 'driftFlag', 'DriftFlag') === true,
    qcIssues: parseQcIssues(pick(qc, 'issues', 'Issues')),
    qcEvidenceIds: toStringArray(pick(qc, 'evidenceArtifactIds', 'EvidenceArtifactIds')),
    allowedActions: toStringArray(pick(actions, 'allowed', 'Allowed')),
    canResolve: pick(permissions, 'canResolve', 'CanResolve') === true,
    canEdit: pick(permissions, 'canEdit', 'CanEdit') === true,
    history,
    truncated: pick(record, 'truncated', 'Truncated') === true,
  };
}

/** Newest-first history for the audit panel. Pure. */
export function sortHistoryNewestFirst(entries: readonly ReviewHistoryEntry[]): ReviewHistoryEntry[] {
  return [...entries].sort((a, b) => {
    if (a.createdAt === '' && b.createdAt === '') {
      return 0;
    }
    if (a.createdAt === '') {
      return 1;
    }
    if (b.createdAt === '') {
      return -1;
    }
    return b.createdAt.localeCompare(a.createdAt);
  });
}

/**
 * Backend `Allowed` tokens per hardened action. The server is authoritative;
 * this maps UX dispositions to the tokens the context advertises
 * (`resolve`/`dismiss`/`reopen`/`resolve-with-edit`). Pure.
 */
export function allowedTokenFor(action: DispositionAction): string {
  switch (action) {
    case 'approve':
      return 'resolve';
    case 'reject':
      return 'dismiss';
    case 'requeue':
      return 'reopen';
    case 'resolve-with-edit':
      return 'resolve-with-edit';
  }
}

/** True when the disposition is permitted by the aggregate `actions[]`. Pure. */
export function isDispositionAllowed(context: ReviewContextView, action: DispositionAction): boolean {
  const token = allowedTokenFor(action).toLowerCase();
  return context.allowedActions.some((entry) => entry.toLowerCase() === token);
}

/** True for optimistic-concurrency conflicts (409/412). Pure. */
export function isReviewConflict(error: { readonly code?: string; readonly status?: number } | undefined | null): boolean {
  if (error === undefined || error === null) {
    return false;
  }
  return (
    error.code === 'REVIEW_VERSION_CONFLICT' ||
    error.code === 'REVIEW_ALREADY_RESOLVED' ||
    error.code === 'CONFLICT' ||
    error.code === 'SELECTION_CONFLICT' ||
    error.status === 409 ||
    error.status === 412
  );
}

/** True when the backend reports the item already resolved. Pure. */
export function isAlreadyResolved(error: { readonly code?: string; readonly status?: number } | undefined | null): boolean {
  if (error === undefined || error === null) {
    return false;
  }
  return error.code === 'REVIEW_ALREADY_RESOLVED';
}

/** True when a reason is missing (400). Pure. */
export function isReasonRequired(error: { readonly code?: string; readonly status?: number } | undefined | null): boolean {
  if (error === undefined || error === null) {
    return false;
  }
  return error.code === 'REVIEW_REASON_REQUIRED' || error.status === 400;
}

export interface ReasonValidation {
  readonly valid: boolean;
  readonly error: string | undefined;
}

/**
 * Client-side reason gate: required dispositions reject blank reasons;
 * overlong reasons are blocked with an inline error (never sent, so no 400
 * round-trip). Pure.
 */
export function validateReason(reason: string, action: DispositionAction): ReasonValidation {
  const trimmed = reason.trim();
  if (dispositionRequiresReason(action) && trimmed === '') {
    return { valid: false, error: 'A reason is required for this action.' };
  }
  if (trimmed.length > REVIEW_MAX_REASON_LENGTH) {
    return { valid: false, error: `Reason must be ${String(REVIEW_MAX_REASON_LENGTH)} characters or fewer.` };
  }
  return { valid: true, error: undefined };
}

/**
 * Client-side resolve-with-edit gate: edit text must be non-empty and within
 * the backend window. Pure.
 */
export function validateEditText(editText: string): ReasonValidation {
  const trimmed = editText.trim();
  if (trimmed === '') {
    return { valid: false, error: 'Enter the corrected text before resolving with an edit.' };
  }
  if (trimmed.length > REVIEW_MAX_EDIT_LENGTH) {
    return { valid: false, error: `Edit must be ${String(REVIEW_MAX_EDIT_LENGTH)} characters or fewer.` };
  }
  return { valid: true, error: undefined };
}

export interface DispositionPayload {
  readonly expectedVersion: number;
  readonly reason: string;
  readonly editText?: string;
}

/**
 * Builds the hardened mutation body (`{ expectedVersion, reason, editText? }`).
 * Empty optional notes fall back to a neutral audit string so the backend
 * reason gate (required for all hardened mutations) never 400s on an
 * explicitly-optional UI field. Pure.
 */
export function buildDispositionPayload(
  action: DispositionAction,
  expectedVersion: number,
  reason: string,
  editText?: string,
): DispositionPayload {
  const trimmed = reason.trim();
  const fallback = action === 'approve' ? 'Approved via review studio.' : 'Reviewed via review studio.';
  const resolvedReason = trimmed !== '' ? trimmed : fallback;
  if (action === 'resolve-with-edit') {
    return { expectedVersion, reason: resolvedReason, editText: (editText ?? '').trim() };
  }
  return { expectedVersion, reason: resolvedReason };
}

/** Extracts the authoritative version from a 409 details envelope. Pure. */
export function currentVersionFromDetails(details: Record<string, unknown> | undefined): number | undefined {
  if (details === undefined) {
    return undefined;
  }
  const candidate = details['currentVersion'] ?? details['currentSelectionVersion'] ?? details['version'];
  return typeof candidate === 'number' && Number.isFinite(candidate) ? Math.floor(candidate) : undefined;
}

/** Extracts the resolving actor from a 409 details envelope when present. Pure. */
export function actorFromDetails(details: Record<string, unknown> | undefined): string | undefined {
  if (details === undefined) {
    return undefined;
  }
  const candidate = details['resolvedBy'] ?? details['actor'] ?? details['reviewer'];
  return typeof candidate === 'string' && candidate !== '' ? candidate : undefined;
}
