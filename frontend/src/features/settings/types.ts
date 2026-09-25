/**
 * Settings + cost/quota domain view (Task 035).
 *
 * Pure parsing + derivation over Task 006 preferences
 * (`GET|PUT /me/preferences`, whitelisted keys), the Task 007 dashboard
 * aggregate (`cost/quota/storage` sections), and the Task 008 workspace
 * aggregate (`cost` + `media` sections). Every cost figure distinguishes
 * estimated vs actual; every estimate renders with the `Estimate` label.
 * Reservation ids, provider-internal cost keys, and raw telemetry never
 * survive parsing.
 */

export const MAX_PREFERENCE_BYTES = 4096;

export const ALLOWED_PREFERENCE_KEYS: readonly string[] = [
  'locale',
  'timezone',
  'theme',
  'defaultProjectFilters',
  'timelineZoom',
  'notificationPreferences',
];

export const LOCALE_OPTIONS: readonly string[] = ['en', 'ar', 'ru', 'en-US'];

export const THEME_OPTIONS: readonly string[] = ['light', 'dark'];

export type QuotaState = 'available' | 'near' | 'exceeded' | 'reserved';

export interface CostBreakdown {
  readonly estimatedUsd: number;
  readonly actualRunUsd: number | undefined;
  readonly actualMonthUsd: number | undefined;
  readonly currency: string;
  readonly durationMs: number | undefined;
  readonly storageUsedBytes: number | undefined;
  readonly storageQuotaBytes: number | undefined;
}

export interface QuotaView {
  readonly state: QuotaState;
  readonly remaining: number | undefined;
  readonly resetsAt: string | undefined;
  readonly storageRatio: number;
  readonly reservedUsd: number | undefined;
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

function toFiniteNumber(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

function toNonEmptyString(value: unknown): string | undefined {
  return typeof value === 'string' && value !== '' ? value : undefined;
}

const SECRET_FRAGMENTS: readonly string[] = [
  'secret',
  'password',
  'passwd',
  'pwd',
  'token',
  'credential',
  'private_key',
  'api_key',
  'apikey',
  'client_secret',
];

/** True for whitelisted preference keys. Pure. */
export function isAllowedPreferenceKey(key: string): boolean {
  return (ALLOWED_PREFERENCE_KEYS as readonly string[]).includes(key);
}

/** UTF-8 byte length of a string value. Pure. */
export function preferenceByteLength(value: string): number {
  try {
    return new TextEncoder().encode(value).length;
  } catch {
    return value.length;
  }
}

/** True when the value exceeds the 4KB client cap. Pure. */
export function isPreferenceValueTooLarge(value: string): boolean {
  return preferenceByteLength(value) > MAX_PREFERENCE_BYTES;
}

/** True when a JSON object value carries secret-looking properties. Pure. */
export function hasSecretMaterial(raw: string): boolean {
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw) as unknown;
  } catch {
    return false;
  }
  const record = toRecord(parsed);
  if (record === undefined) {
    return false;
  }
  for (const key of Object.keys(record)) {
    const lowered = key.toLowerCase();
    for (const fragment of SECRET_FRAGMENTS) {
      if (lowered.includes(fragment)) {
        return true;
      }
    }
  }
  return false;
}

/** Normalizes a locale to a supported option (`en` fallback). Pure. */
export function normalizeLocale(raw: unknown): string {
  const candidate = typeof raw === 'string' ? raw.trim() : '';
  if ((LOCALE_OPTIONS as readonly string[]).includes(candidate)) {
    return candidate;
  }
  const base = candidate.split('-')[0]?.toLowerCase() ?? '';
  if (base === 'ar') {
    return 'ar';
  }
  if (base === 'ru') {
    return 'ru';
  }
  if (base === 'en') {
    return 'en';
  }
  return 'en';
}

/** Normalizes a theme value (`light` fallback). Pure. */
export function normalizeTheme(raw: unknown): 'light' | 'dark' {
  return raw === 'dark' ? 'dark' : 'light';
}

/** True when the value is a valid IANA timezone. Pure (never throws). */
export function isValidTimezone(value: string): boolean {
  if (value.trim() === '') {
    return false;
  }
  try {
    new Intl.DateTimeFormat('en', { timeZone: value });
    return true;
  } catch {
    return false;
  }
}

/**
 * Resolves a stored timezone: valid IANA values pass through, anything else
 * falls back to `UTC` with `fellBack: true` so the UI shows the inline
 * warning. Pure.
 */
export function resolveStoredTimezone(raw: unknown): { timeZone: string; fellBack: boolean } {
  const candidate = typeof raw === 'string' ? raw.trim() : '';
  if (candidate !== '' && isValidTimezone(candidate)) {
    return { timeZone: candidate, fellBack: false };
  }
  if (candidate === '') {
    return { timeZone: 'UTC', fellBack: false };
  }
  return { timeZone: 'UTC', fellBack: true };
}

export interface DefaultProjectFiltersView {
  readonly status: string;
  readonly archived: string;
}

/** Parses the `defaultProjectFilters` string value defensively. Pure. */
export function parseDefaultProjectFilters(raw: unknown): DefaultProjectFiltersView {
  const fallback: DefaultProjectFiltersView = { status: '', archived: '' };
  if (typeof raw !== 'string' || raw === '') {
    return fallback;
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw) as unknown;
  } catch {
    return fallback;
  }
  const record = toRecord(parsed);
  if (record === undefined) {
    return fallback;
  }
  const status = toNonEmptyString(pick(record, 'status', 'Status')) ?? '';
  const archived = toNonEmptyString(pick(record, 'archived', 'Archived')) ?? '';
  return { status: status.slice(0, 40), archived: archived.slice(0, 40) };
}

/** Serializes project filters to the stored string value. Pure. */
export function serializeDefaultProjectFilters(filters: DefaultProjectFiltersView): string {
  return JSON.stringify({ status: filters.status, archived: filters.archived });
}

/** Parses the `timelineZoom` string value (1..4, default 1). Pure. */
export function parseTimelineZoom(raw: unknown): number {
  if (typeof raw !== 'string' || raw === '') {
    return 1;
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw) as unknown;
  } catch {
    const direct = Number(raw);
    return Number.isFinite(direct) ? Math.min(4, Math.max(1, Math.round(direct))) : 1;
  }
  const candidate = typeof parsed === 'number' ? parsed : Number(parsed);
  if (!Number.isFinite(candidate)) {
    return 1;
  }
  return Math.min(4, Math.max(1, Math.round(candidate as number)));
}

/** Serializes a timeline zoom level. Pure. */
export function serializeTimelineZoom(zoom: number): string {
  const safe = Number.isFinite(zoom) ? Math.min(4, Math.max(1, Math.round(zoom))) : 1;
  return JSON.stringify(safe);
}

/** Storage usage ratio in [0, +Infinity). Zero on unknown quota. Pure. */
export function storageUsageRatio(usedBytes: number | undefined, quotaBytes: number | undefined): number {
  if (usedBytes === undefined || quotaBytes === undefined || quotaBytes <= 0 || usedBytes <= 0) {
    return 0;
  }
  return usedBytes / quotaBytes;
}

/**
 * Derives the quota state (Task 035, R3). Priority: `exceeded` (remaining
 * exhausted or storage full) wins, then `reserved` (a held amount awaiting
 * reconciliation, informational only), then `near` (low remaining or
 * storage at warning threshold), else `available`. Pure.
 */
export function deriveQuotaState(input: {
  readonly remaining?: number;
  readonly usedBytes?: number;
  readonly quotaBytes?: number;
  readonly reservedUsd?: number;
}): QuotaState {
  const ratio = storageUsageRatio(input.usedBytes, input.quotaBytes);
  const remaining = input.remaining;
  if ((remaining !== undefined && remaining <= 0) || ratio >= 1) {
    return 'exceeded';
  }
  if (input.reservedUsd !== undefined && input.reservedUsd > 0) {
    return 'reserved';
  }
  if ((remaining !== undefined && remaining <= 3) || ratio >= 0.8) {
    return 'near';
  }
  return 'available';
}

/** Alert tone per quota state (distinct visual treatments, never color alone). Pure. */
export function toneForQuotaState(state: QuotaState): 'success' | 'warning' | 'error' | 'info' {
  switch (state) {
    case 'exceeded':
      return 'error';
    case 'near':
      return 'warning';
    case 'reserved':
      return 'info';
    default:
      return 'success';
  }
}

/** Glyph per quota state (text, never color alone). Pure. */
export function iconForQuotaState(state: QuotaState): string {
  switch (state) {
    case 'available':
      return '✓';
    case 'near':
      return '⚠';
    case 'exceeded':
      return '✕';
    case 'reserved':
      return '◷';
    default:
      return '•';
  }
}

/** True when costly actions must block (only `exceeded` blocks). Pure. */
export function isQuotaBlocking(state: QuotaState): boolean {
  return state === 'exceeded';
}

/**
 * Builds a quota view from dashboard + workspace slices. Missing sections
 * stay undefined (callers render `UnavailableState`, never zero-fill).
 * Reserved amounts are informational only — reservation ids never enter
 * this shape. Pure.
 */
export function buildQuotaView(input: {
  readonly remaining?: unknown;
  readonly resetsAt?: unknown;
  readonly usedBytes?: unknown;
  readonly quotaBytes?: unknown;
  readonly reservedUsd?: unknown;
}): QuotaView {
  const remaining = toFiniteNumber(input.remaining);
  const resetsAt = toNonEmptyString(input.resetsAt);
  const usedBytes = toFiniteNumber(input.usedBytes);
  const quotaBytes = toFiniteNumber(input.quotaBytes);
  const reservedRaw = toFiniteNumber(input.reservedUsd);
  const reservedUsd = reservedRaw !== undefined && reservedRaw > 0 ? reservedRaw : undefined;
  const ratio = storageUsageRatio(usedBytes, quotaBytes);
  const state = deriveQuotaState({ remaining, usedBytes, quotaBytes, reservedUsd });
  return { state, remaining, resetsAt, storageRatio: ratio, reservedUsd };
}

/**
 * Builds the cost breakdown from dashboard actuals plus the workspace run
 * actual and media/storage context. The estimate is always a planning
 * figure supplied by the caller (preflight mirror) and must render with
 * the `Estimate` label; actuals are metered server values. A missing cost
 * section yields undefined actuals (callers show `UnavailableState`).
 * Never carries reservation ids. Pure.
 */
export function buildCostBreakdown(input: {
  readonly estimatedUsd: unknown;
  readonly actualRunUsd?: unknown;
  readonly actualMonthUsd?: unknown;
  readonly currency?: unknown;
  readonly durationMs?: unknown;
  readonly storageUsedBytes?: unknown;
  readonly storageQuotaBytes?: unknown;
}): CostBreakdown {
  const estimatedRaw = toFiniteNumber(input.estimatedUsd);
  const estimatedUsd = estimatedRaw !== undefined && estimatedRaw >= 0 ? estimatedRaw : 0;
  const actualRunRaw = toFiniteNumber(input.actualRunUsd);
  const actualMonthRaw = toFiniteNumber(input.actualMonthUsd);
  const currency = toNonEmptyString(input.currency) ?? 'USD';
  const durationRaw = toFiniteNumber(input.durationMs);
  const usedRaw = toFiniteNumber(input.storageUsedBytes);
  const quotaRaw = toFiniteNumber(input.storageQuotaBytes);
  return {
    estimatedUsd,
    actualRunUsd: actualRunRaw,
    actualMonthUsd: actualMonthRaw,
    currency,
    durationMs: durationRaw !== undefined && durationRaw > 0 ? durationRaw : undefined,
    storageUsedBytes: usedRaw !== undefined && usedRaw >= 0 ? usedRaw : undefined,
    storageQuotaBytes: quotaRaw !== undefined && quotaRaw > 0 ? quotaRaw : undefined,
  };
}

/** True when any rendered cost text would leak a reservation id. Pure. */
export function containsReservationId(values: readonly string[]): boolean {
  for (const value of values) {
    const lowered = value.toLowerCase();
    if (lowered.includes('res_') || lowered.includes('reservation')) {
      return true;
    }
  }
  return false;
}
