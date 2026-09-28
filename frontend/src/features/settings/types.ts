/**
 * Settings + preferences domain view (Task 035B).
 *
 * Pure parsing + derivation over Task 006 preferences
 * (`GET|PUT /me/preferences`, whitelisted keys only). Cost/quota helpers
 * live in `features/cost` (Task 035A) and must not be duplicated here.
 * Every value is an opaque string on the wire; parsing happens per key.
 * Nothing here touches the network, the store, or the DOM.
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

export type PreferenceKey =
  | 'locale'
  | 'timezone'
  | 'theme'
  | 'defaultProjectFilters'
  | 'timelineZoom'
  | 'notificationPreferences';

export type PreferenceMap = Record<string, string>;

/** Mirrors `SUPPORTED_LOCALES` in `src/i18n/i18n.ts` (Task 045). */
export const LOCALE_OPTIONS: readonly string[] = ['en', 'ar', 'ru'];

export type ThemePreference = 'light' | 'dark' | 'system';

/** Selectable theme values (Task 035B, R3). `system` follows the OS setting. */
export const THEME_OPTIONS: readonly string[] = ['light', 'dark', 'system'];

/**
 * Curated IANA timezone list for the select (Task 035B). `UTC` is always
 * first and is the fallback for unknown values. Kept short on purpose:
 * free-text entry is not offered (invalid zones map to inline errors).
 */
export const TIMEZONE_OPTIONS: readonly string[] = [
  'UTC',
  'America/New_York',
  'America/Chicago',
  'America/Denver',
  'America/Los_Angeles',
  'Europe/London',
  'Europe/Paris',
  'Europe/Berlin',
  'Europe/Moscow',
  'Asia/Dubai',
  'Asia/Karachi',
  'Asia/Kolkata',
  'Asia/Singapore',
  'Asia/Tokyo',
  'Australia/Sydney',
  'Pacific/Auckland',
];

/** Delivery channels shown in the form. Email/webhook are future-only. */
export const NOTIFICATION_CHANNELS: readonly string[] = ['in-app', 'email', 'webhook'];

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

/**
 * Normalizes a theme value to the effective light/dark store value
 * (`light` fallback, `system` resolves via the OS setting). Kept for
 * backward compatibility; new code prefers `normalizeThemePreference`.
 * Pure (never touches `matchMedia` — see `resolveEffectiveTheme`).
 */
export function normalizeTheme(raw: unknown): 'light' | 'dark' {
  if (raw === 'dark') {
    return 'dark';
  }
  return 'light';
}

/** Normalizes a theme preference (`light`/`dark`/`system`, `light` fallback). Pure. */
export function normalizeThemePreference(raw: unknown): ThemePreference {
  if (raw === 'dark' || raw === 'system' || raw === 'light') {
    return raw;
  }
  if (typeof raw === 'string') {
    const trimmed = raw.trim().toLowerCase();
    if (trimmed === 'dark' || trimmed === 'system' || trimmed === 'light') {
      return trimmed as ThemePreference;
    }
  }
  return 'light';
}

/** True when the raw value is a selectable theme preference. Pure. */
export function isValidThemePreference(raw: unknown): boolean {
  return raw === 'light' || raw === 'dark' || raw === 'system';
}

/**
 * Resolves a theme preference to the effective light/dark store value.
 * `system` follows `prefers-color-scheme` when available, else `light`.
 * Never throws; safe in jsdom (no `matchMedia`).
 * Pure apart from the guarded `matchMedia` read.
 */
export function resolveEffectiveTheme(preference: ThemePreference): 'light' | 'dark' {
  if (preference === 'dark') {
    return 'dark';
  }
  if (preference === 'light') {
    return 'light';
  }
  try {
    const matcher =
      typeof window !== 'undefined' && typeof window.matchMedia === 'function'
        ? window.matchMedia('(prefers-color-scheme: dark)')
        : undefined;
    if (matcher !== undefined && matcher.matches) {
      return 'dark';
    }
  } catch {
    // OS query unavailable: fall through to light.
  }
  return 'light';
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

/** Normalizes a timezone select value (valid IANA or `UTC`). Pure. */
export function normalizeTimezoneOption(raw: unknown): string {
  const candidate = typeof raw === 'string' ? raw.trim() : '';
  if (candidate !== '' && isValidTimezone(candidate)) {
    return candidate;
  }
  return 'UTC';
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

/** Serializes a locale value (stored raw, validated on read). Pure. */
export function serializeLocale(locale: string): string {
  return normalizeLocale(locale);
}

/** Serializes a timezone value (valid IANA or `UTC`). Pure. */
export function serializeTimezone(timezone: string): string {
  const trimmed = timezone.trim();
  if (trimmed === '') {
    return 'UTC';
  }
  return trimmed.slice(0, 80);
}

/** Serializes a theme preference value. Pure. */
export function serializeThemePreference(theme: ThemePreference): string {
  return normalizeThemePreference(theme);
}

export interface PreferenceDraft {
  readonly locale: string;
  readonly timezone: string;
  readonly theme: ThemePreference;
  readonly filterStatus: string;
  readonly filterArchived: string;
  readonly zoom: number;
  readonly notificationPreferences: string;
}

/** Builds the editable draft from the server snapshot. Pure. */
export function draftFromServerMap(serverMap: PreferenceMap): PreferenceDraft {
  const filters = parseDefaultProjectFilters(serverMap['defaultProjectFilters']);
  return {
    locale: normalizeLocale(serverMap['locale']),
    timezone: normalizeTimezoneOption(
      (() => {
        const raw = serverMap['timezone'];
        if (typeof raw !== 'string' || raw === '') {
          return 'UTC';
        }
        try {
          const parsed = JSON.parse(raw) as unknown;
          if (typeof parsed === 'string' && parsed.trim() !== '') {
            return parsed;
          }
        } catch {
          // Stored raw (already a plain zone name).
        }
        const stripped = raw.startsWith('"') && raw.endsWith('"') ? raw.slice(1, -1) : raw;
        return stripped;
      })(),
    ),
    theme: normalizeThemePreference(serverMap['theme']),
    filterStatus: filters.status,
    filterArchived: filters.archived,
    zoom: parseTimelineZoom(serverMap['timelineZoom']),
    notificationPreferences:
      typeof serverMap['notificationPreferences'] === 'string' ? serverMap['notificationPreferences'] : '',
  };
}

/** Serializes the draft to wire values (whitelisted keys only). Pure. */
export function serializeDraft(draft: PreferenceDraft): PreferenceMap {
  return {
    locale: serializeLocale(draft.locale),
    timezone: serializeTimezone(draft.timezone),
    theme: serializeThemePreference(draft.theme),
    defaultProjectFilters: serializeDefaultProjectFilters({
      status: draft.filterStatus,
      archived: draft.filterArchived,
    }),
    timelineZoom: serializeTimelineZoom(draft.zoom),
    notificationPreferences: draft.notificationPreferences,
  };
}

/**
 * Collects whitelisted keys whose serialized draft differs from the server
 * snapshot. Empty means clean. Sending only dirty keys gives per-key
 * last-write-wins (never silent cross-key overwrite). Pure.
 */
export function collectDirtyKeys(draft: PreferenceDraft, serverMap: PreferenceMap): PreferenceKey[] {
  const serialized = serializeDraft(draft);
  const serverNormalized = draftFromServerMap(serverMap);
  const serverSerialized = serializeDraft(serverNormalized);
  const dirty: PreferenceKey[] = [];
  for (const key of ALLOWED_PREFERENCE_KEYS as unknown as PreferenceKey[]) {
    if ((serialized[key] ?? '') !== (serverSerialized[key] ?? '')) {
      dirty.push(key);
    }
  }
  return dirty;
}

/** True when the draft differs from the server snapshot. Pure. */
export function isDraftDirty(draft: PreferenceDraft, serverMap: PreferenceMap): boolean {
  return collectDirtyKeys(draft, serverMap).length > 0;
}

/**
 * Builds the PUT body for dirty keys only. Never includes unlisted keys —
 * callers cannot smuggle unknown keys through this helper. Pure.
 */
export function buildPreferencePayload(draft: PreferenceDraft, serverMap: PreferenceMap): PreferenceMap {
  const dirty = collectDirtyKeys(draft, serverMap);
  const serialized = serializeDraft(draft);
  const body: PreferenceMap = {};
  for (const key of dirty) {
    if (isAllowedPreferenceKey(key)) {
      body[key] = serialized[key] ?? '';
    }
  }
  return body;
}

export interface SaveErrorShape {
  readonly code?: string;
  readonly status?: number;
  readonly message?: string;
  readonly details?: Record<string, unknown>;
}

/** True for save conflicts (409 — concurrent edit in another tab). Pure. */
export function isPreferenceConflictError(error: SaveErrorShape | undefined | null): boolean {
  if (error === undefined || error === null) {
    return false;
  }
  if (error.status === 409) {
    return true;
  }
  const code = error.code ?? '';
  return (
    code === 'CONFLICT' ||
    code === 'SETTINGS_VERSION_CONFLICT' ||
    code === 'REVIEW_VERSION_CONFLICT' ||
    code.endsWith('_CONFLICT')
  );
}

/** True for offline/network failures (queued-save, never silent loss). Pure. */
export function isPreferenceOfflineError(error: SaveErrorShape | undefined | null): boolean {
  if (error === undefined || error === null) {
    return false;
  }
  if (error.code === 'NETWORK_ERROR') {
    return true;
  }
  const message = (error.message ?? '').toLowerCase();
  return (
    message.includes('network unavailable') ||
    message.includes('failed to fetch') ||
    message.includes('fetch failed') ||
    message.includes('offline') ||
    message.includes('load failed')
  );
}

/** True for tenant-scoped forbidden saves (403 — never bypass). Pure. */
export function isPreferenceForbiddenError(error: SaveErrorShape | undefined | null): boolean {
  if (error === undefined || error === null) {
    return false;
  }
  return error.status === 403 || error.code === 'FORBIDDEN' || error.code === 'USER_DISABLED';
}

/**
 * Extracts per-field messages from a server `details` bag. Only whitelisted
 * keys survive; anything else is dropped (UI never renders unknown fields).
 * Never throws. Pure.
 */
export function fieldErrorsFromDetails(details: unknown): Partial<Record<PreferenceKey, string>> {
  const record = toRecord(details);
  if (record === undefined) {
    return {};
  }
  const out: Partial<Record<PreferenceKey, string>> = {};
  for (const key of ALLOWED_PREFERENCE_KEYS as unknown as PreferenceKey[]) {
    const value = record[key];
    if (typeof value === 'string' && value !== '') {
      out[key] = value.slice(0, 500);
      continue;
    }
    const nested = toRecord(value);
    if (nested !== undefined) {
      const message = pick(nested, 'message', 'error', 'reason');
      if (typeof message === 'string' && message !== '') {
        out[key] = message.slice(0, 500);
      }
    }
  }
  return out;
}
