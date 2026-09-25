/**
 * Voice assignment domain view (Task 029).
 *
 * Pure parsing + derivation over the Task 010 speaker/voice contract. All
 * shapes are parsed defensively (camelCase/PascalCase, missing sections fall
 * back to neutral defaults, never throw for list rows). Backend compatibility
 * is authoritative — the client never invents eligibility, it only renders
 * the backend-reported compatible list. Texts render as plain text only.
 */

export const SPEAKER_PAGE_SIZE = 100;

export const SPEAKER_MAX_PAGES = 10;

/** Default preview line when the caller omits text (backend sample line). */
export const PREVIEW_DEFAULT_TEXT = 'Hello, this is a voice preview.';

/** Backend preview text window (1..500 chars). */
export const PREVIEW_MAX_TEXT_LENGTH = 500;

export type ConsentState = 'valid' | 'unavailable' | 'consent-required' | 'revoked';

export interface AssignedVoiceView {
  readonly voiceProfileId: string;
  readonly voiceId: string;
  readonly provider: string;
  readonly language: string;
  readonly voiceType: string;
}

export interface SpeakerView {
  readonly id: string;
  readonly speakerKey: string;
  readonly displayName: string;
  readonly segmentCount: number;
  readonly firstAppearanceMs: number | undefined;
  readonly lastAppearanceMs: number | undefined;
  readonly confidence: number | undefined;
  readonly assignedVoice: AssignedVoiceView | undefined;
}

export interface VoiceOptionView {
  readonly voiceProfileId: string;
  readonly voiceId: string;
  readonly provider: string;
  readonly language: string;
  readonly voiceType: string;
  readonly cloningEnabled: boolean;
  readonly consentState: ConsentState;
  /** Tenant-policy message from the API, rendered verbatim when present. */
  readonly policyMessage: string | undefined;
  readonly isDefault: boolean;
  /** Cost note from the backend when provided, rendered verbatim. */
  readonly costNote: string | undefined;
  readonly label: string;
}

export interface ExcludedVoiceView {
  readonly voiceId: string;
  readonly reasons: readonly string[];
}

export interface AvailableVoicesView {
  readonly voices: readonly VoiceOptionView[];
  readonly excludedCount: number;
  readonly excluded: readonly ExcludedVoiceView[];
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

function normalizeConsentToken(value: string): ConsentState | undefined {
  const normalized = value.trim().toLowerCase().replace(/[_\s]+/g, '-');
  if (
    normalized === 'valid' ||
    normalized === 'granted' ||
    normalized === 'approved' ||
    normalized === 'ok' ||
    normalized === 'active' ||
    normalized === 'verified'
  ) {
    return 'valid';
  }
  if (
    normalized === 'unavailable' ||
    normalized === 'missing' ||
    normalized === 'not-found' ||
    normalized === 'notfound' ||
    normalized === 'unknown' ||
    normalized === 'none'
  ) {
    return 'unavailable';
  }
  if (
    normalized === 'consent-required' ||
    normalized === 'consentrequired' ||
    normalized === 'requires-consent' ||
    normalized === 'requiresconsent' ||
    normalized === 'pending' ||
    normalized === 'required' ||
    normalized === 'needs-consent' ||
    normalized === 'consent-pending' ||
    normalized === 'awaiting-consent'
  ) {
    return 'consent-required';
  }
  if (
    normalized === 'revoked' ||
    normalized === 'expired' ||
    normalized === 'withdrawn' ||
    normalized === 'denied' ||
    normalized === 'rejected' ||
    normalized === 'invalid'
  ) {
    return 'revoked';
  }
  return undefined;
}

/**
 * Derives the consent state for one voice record. Checks explicit
 * consent-status keys first (verbatim backend vocabulary), then boolean
 * consent flags, defaulting to `valid` when the backend carries no consent
 * signal (compatible-list voices are consent-valid by server construction).
 * Pure.
 */
export function consentStateFor(raw: unknown): ConsentState {
  const record = toRecord(raw);
  if (record === undefined) {
    return 'valid';
  }
  const candidates = [
    pick(
      record,
      'consentStatus',
      'ConsentStatus',
      'consentState',
      'ConsentState',
      'voiceConsentStatus',
      'VoiceConsentStatus',
      'consent',
      'Consent',
    ),
  ];
  for (const candidate of candidates) {
    if (typeof candidate === 'string' && candidate !== '') {
      const parsed = normalizeConsentToken(candidate);
      if (parsed !== undefined) {
        return parsed;
      }
    }
  }
  if (
    record['consentRevoked'] === true ||
    record['revoked'] === true ||
    record['consentWithdrawn'] === true
  ) {
    return 'revoked';
  }
  if (
    record['consentRequired'] === true ||
    record['requiresConsent'] === true ||
    record['needsConsent'] === true
  ) {
    return 'consent-required';
  }
  if (record['consentUnavailable'] === true) {
    return 'unavailable';
  }
  return 'valid';
}

/**
 * Tenant-policy message for a voice record, rendered verbatim (no client
 * legal interpretation). Pure — undefined when the backend provides none.
 */
export function policyMessageFor(raw: unknown): string | undefined {
  const record = toRecord(raw);
  if (record === undefined) {
    return undefined;
  }
  return (
    toNonEmptyString(
      pick(
        record,
        'tenantPolicyMessage',
        'TenantPolicyMessage',
        'policyMessage',
        'PolicyMessage',
        'consentMessage',
        'ConsentMessage',
        'policy',
        'Policy',
      ),
    ) ?? undefined
  );
}

/**
 * Cost note for a voice or assignment record, rendered verbatim when the
 * backend provides it (string) or formatted from a finite number. Pure —
 * undefined when absent.
 */
export function costNoteFor(raw: unknown): string | undefined {
  const record = toRecord(raw);
  if (record === undefined) {
    return undefined;
  }
  const text = toNonEmptyString(
    pick(record, 'costNote', 'CostNote', 'cost', 'Cost', 'estimatedCost', 'EstimatedCost', 'price', 'Price'),
  );
  if (text !== undefined) {
    return text;
  }
  const amount = toFiniteNumber(
    pick(record, 'costUsd', 'CostUsd', 'estimatedCostUsd', 'EstimatedCostUsd', 'amountUsd', 'AmountUsd'),
  );
  if (amount !== undefined) {
    return `Estimated cost ${String(amount)} USD.`;
  }
  return undefined;
}

/** True only for `valid` consent states (the only assignable state). Pure. */
export function isVoiceAssignable(voice: Pick<VoiceOptionView, 'consentState'>): boolean {
  return voice.consentState === 'valid';
}

function parseAssignedVoice(raw: unknown): AssignedVoiceView | undefined {
  const record = toRecord(raw);
  if (record === undefined) {
    return undefined;
  }
  const voiceProfileId =
    toNonEmptyString(pick(record, 'voiceProfileId', 'VoiceProfileId', 'voice_profile_id')) ?? '';
  const voiceId = toNonEmptyString(pick(record, 'voiceId', 'VoiceId', 'voice_id')) ?? '';
  if (voiceProfileId === '' && voiceId === '') {
    return undefined;
  }
  return {
    voiceProfileId: voiceProfileId !== '' ? voiceProfileId : voiceId,
    voiceId: voiceId !== '' ? voiceId : voiceProfileId,
    provider: toNonEmptyString(pick(record, 'provider', 'Provider')) ?? 'unknown',
    language: toNonEmptyString(pick(record, 'language', 'Language')) ?? 'unknown',
    voiceType: toNonEmptyString(pick(record, 'type', 'Type', 'voiceType', 'VoiceType')) ?? 'unknown',
  };
}

/**
 * Builds a speaker view from a list-summary row and/or a detail row. Both
 * shapes are accepted; missing counts fall back to 0, missing names fall
 * back to the speaker key. Never throws. Pure.
 */
export function parseSpeaker(raw: unknown): SpeakerView | undefined {
  const record = toRecord(raw);
  if (record === undefined) {
    return undefined;
  }
  const id = toNonEmptyString(pick(record, 'id', 'Id')) ?? '';
  if (id === '') {
    return undefined;
  }
  const speakerKey = toNonEmptyString(pick(record, 'speakerKey', 'SpeakerKey')) ?? id;
  const displayName =
    toNonEmptyString(pick(record, 'displayName', 'DisplayName', 'name', 'Name')) ?? speakerKey;
  const segmentCount = toFiniteNumber(pick(record, 'segmentCount', 'SegmentCount')) ?? 0;
  const firstAppearanceMs = toFiniteNumber(
    pick(record, 'firstAppearanceMs', 'FirstAppearanceMs', 'firstAppearance', 'FirstAppearance'),
  );
  const lastAppearanceMs = toFiniteNumber(
    pick(record, 'lastAppearanceMs', 'LastAppearanceMs', 'lastAppearance', 'LastAppearance'),
  );
  const confidence = toFiniteNumber(pick(record, 'confidence', 'Confidence'));
  const assignedVoice = parseAssignedVoice(pick(record, 'assignedVoice', 'AssignedVoice'));
  return {
    id,
    speakerKey,
    displayName,
    segmentCount: Math.max(0, Math.floor(segmentCount)),
    firstAppearanceMs,
    lastAppearanceMs,
    confidence,
    assignedVoice,
  };
}

/** Parses a `SpeakerListResponse` page (`{ items, ... }`). Skips bad rows. Pure. */
export function parseSpeakerListItems(raw: unknown): SpeakerView[] {
  const record = toRecord(raw);
  const items = record !== undefined ? pick(record, 'items', 'Items') : raw;
  if (!Array.isArray(items)) {
    return [];
  }
  const out: SpeakerView[] = [];
  for (const entry of items as unknown[]) {
    const parsed = parseSpeaker(entry);
    if (parsed !== undefined) {
      out.push(parsed);
    }
  }
  return out.sort((a, b) => a.speakerKey.localeCompare(b.speakerKey) || a.id.localeCompare(b.id));
}

/**
 * Parses one compatible voice entry (available-voices `voices[]`). Never
 * throws; rows without an id are skipped by the caller. Pure.
 */
export function parseVoiceOption(raw: unknown): VoiceOptionView | undefined {
  const record = toRecord(raw);
  if (record === undefined) {
    return undefined;
  }
  const voiceProfileId =
    toNonEmptyString(pick(record, 'voiceProfileId', 'VoiceProfileId', 'voice_profile_id')) ?? '';
  const voiceId = toNonEmptyString(pick(record, 'voiceId', 'VoiceId', 'voice_id')) ?? '';
  if (voiceProfileId === '' && voiceId === '') {
    return undefined;
  }
  const provider = toNonEmptyString(pick(record, 'provider', 'Provider')) ?? 'unknown';
  const language = toNonEmptyString(pick(record, 'language', 'Language')) ?? 'unknown';
  const voiceType = toNonEmptyString(pick(record, 'type', 'Type', 'voiceType', 'VoiceType')) ?? 'unknown';
  const cloningEnabled = pick(record, 'cloningEnabled', 'CloningEnabled') === true;
  const isDefault =
    pick(record, 'isDefault', 'IsDefault', 'default', 'Default', 'isDefaultVoice', 'IsDefaultVoice') === true;
  const label =
    toNonEmptyString(
      pick(record, 'label', 'Label', 'name', 'Name', 'displayName', 'DisplayName', 'voiceName', 'VoiceName'),
    ) ??
    (voiceId !== '' ? voiceId : voiceProfileId);
  return {
    voiceProfileId: voiceProfileId !== '' ? voiceProfileId : voiceId,
    voiceId: voiceId !== '' ? voiceId : voiceProfileId,
    provider,
    language,
    voiceType,
    cloningEnabled,
    consentState: consentStateFor(record),
    policyMessage: policyMessageFor(record),
    isDefault,
    costNote: costNoteFor(record),
    label,
  };
}

/**
 * Parses an `AvailableVoicesResponse` (`{ voices, excludedCount, excluded }`).
 * Compatible voices only — excluded entries are kept for counts, never as
 * selectable options. Pure.
 */
export function parseAvailableVoices(raw: unknown): AvailableVoicesView {
  const record = toRecord(raw);
  if (record === undefined) {
    return { voices: [], excludedCount: 0, excluded: [] };
  }
  const rawVoices = pick(record, 'voices', 'Voices');
  const voices: VoiceOptionView[] = [];
  if (Array.isArray(rawVoices)) {
    for (const entry of rawVoices as unknown[]) {
      const parsed = parseVoiceOption(entry);
      if (parsed !== undefined) {
        voices.push(parsed);
      }
    }
  }
  voices.sort((a, b) => a.label.localeCompare(b.label) || a.voiceId.localeCompare(b.voiceId));
  const rawExcluded = pick(record, 'excluded', 'Excluded');
  const excluded: ExcludedVoiceView[] = [];
  if (Array.isArray(rawExcluded)) {
    for (const entry of rawExcluded as unknown[]) {
      const entryRecord = toRecord(entry);
      if (entryRecord === undefined) {
        continue;
      }
      const voiceId = toNonEmptyString(pick(entryRecord, 'voiceId', 'VoiceId')) ?? '';
      if (voiceId === '') {
        continue;
      }
      excluded.push({ voiceId, reasons: toStringArray(pick(entryRecord, 'reasons', 'Reasons')) });
    }
  }
  const excludedCount =
    toFiniteNumber(pick(record, 'excludedCount', 'ExcludedCount')) ?? excluded.length;
  return { voices, excludedCount: Math.max(0, Math.floor(excludedCount)), excluded };
}

/** Default voice for reset: the `isDefault` flag, else the first compatible. Pure. */
export function defaultVoiceFor(voices: readonly VoiceOptionView[]): VoiceOptionView | undefined {
  return voices.find((voice) => voice.isDefault) ?? voices[0];
}

/** `mm:ss.mmm` appearance window, or an em dash when timings are absent. Pure. */
export function formatAppearance(firstMs: number | undefined, lastMs: number | undefined): string {
  if (firstMs === undefined && lastMs === undefined) {
    return '—';
  }
  const format = (ms: number): string => {
    const clamped = Math.max(0, Math.round(ms));
    const minutes = Math.floor(clamped / 60_000);
    const seconds = Math.floor((clamped % 60_000) / 1000);
    const millis = clamped % 1000;
    return `${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}.${String(millis).padStart(3, '0')}`;
  };
  if (firstMs !== undefined && lastMs !== undefined) {
    return `${format(firstMs)} → ${format(lastMs)}`;
  }
  return format(firstMs ?? lastMs ?? 0);
}

export type PreviewErrorKind = 'quota' | 'consent' | 'expired' | 'unknown';

/** True for quota failures (429). Pure. */
export function isQuotaError(
  error: { readonly code?: string; readonly status?: number } | undefined | null,
): boolean {
  if (error === undefined || error === null) {
    return false;
  }
  return (
    error.code === 'PREVIEW_QUOTA_EXCEEDED' ||
    error.code === 'QUOTA_EXCEEDED' ||
    error.code === 'PROVIDER_QUOTA_EXHAUSTED' ||
    error.code === 'PROVIDER_RATE_LIMITED' ||
    error.code === 'RATE_LIMITED' ||
    error.status === 429
  );
}

/** True for consent/policy failures (403). Pure. */
export function isConsentError(
  error: { readonly code?: string; readonly status?: number } | undefined | null,
): boolean {
  if (error === undefined || error === null) {
    return false;
  }
  return (
    error.code === 'VOICE_CONSENT_REQUIRED' ||
    error.code === 'CONSENT_REQUIRED' ||
    error.code === 'POLICY_DENIED' ||
    error.status === 403
  );
}

/** True for expired signed URLs (410). Pure. */
export function isExpiredError(
  error: { readonly code?: string; readonly status?: number } | undefined | null,
): boolean {
  if (error === undefined || error === null) {
    return false;
  }
  return error.code === 'URL_EXPIRED' || error.status === 410;
}

/** True for optimistic-concurrency conflicts (409). Pure. */
export function isAssignmentConflict(
  error: { readonly code?: string; readonly status?: number } | undefined | null,
): boolean {
  if (error === undefined || error === null) {
    return false;
  }
  return error.code === 'SELECTION_CONFLICT' || error.status === 409;
}

/** True when the compatibility endpoint reports no diarization (404). Pure. */
export function isVoicesNotFound(
  error: { readonly code?: string; readonly status?: number } | undefined | null,
): boolean {
  if (error === undefined || error === null) {
    return false;
  }
  return error.code === 'NOT_FOUND' || error.code === 'PROJECT_NOT_FOUND' || error.status === 404;
}

/** Maps a normalized preview failure to its distinct UI branch. Pure. */
export function mapPreviewError(
  error: { readonly code?: string; readonly status?: number } | undefined | null,
): PreviewErrorKind {
  if (isQuotaError(error)) {
    return 'quota';
  }
  if (isConsentError(error)) {
    return 'consent';
  }
  if (isExpiredError(error)) {
    return 'expired';
  }
  return 'unknown';
}
