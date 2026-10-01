/**
 * Locale- and timezone-aware date helpers (Task 045, R2).
 *
 * THIS IS THE SINGLE DATE IMPLEMENTATION
 * --------------------------------------
 * `src/i18n/format.ts` (Task 018) re-exports everything below rather than
 * keeping a second copy. That is deliberate: two date formatters is how the
 * transcript timestamps get `dateStyle: 'medium'` and the export list gets
 * `'short'` forever, and only one of them has tests.
 *
 * THE FALLBACK CHAIN
 * ------------------
 *   locale:  caller → `FALLBACK_LOCALE` (`en`)
 *   zone:    preference (`timezone` from Task 006/035) → browser zone → `UTC`
 *
 * Both fallbacks are *silent by design at call sites* and *noisy in dev*: an
 * invalid preference is a client bug or a hand-edited preference row, never a
 * reason to throw out of render. `Intl` throws `RangeError` for both an unknown
 * locale tag and an unknown IANA zone, so every public function here catches and
 * degrades.
 *
 * NO PII, NO NETWORK
 * -----------------
 * Pure `Intl` over a caller-supplied value. Nothing here fetches a timezone
 * database, calls a geolocation API, or formats a name: the zone is always an
 * explicit preference, never inferred from the user.
 */

/** IANA zone used whenever the preference is absent, blank or unparseable. */
export const FALLBACK_TIME_ZONE = 'UTC';

/** Locale used whenever the preference is absent, blank or unparseable. */
export const FALLBACK_LOCALE = 'en';

export type DateStyleLevel = 'full' | 'long' | 'medium' | 'short';

/** Anything a caller can hand to a formatter. */
export type DateLike = string | number | Date;

export interface FormatDateOptions {
  /** BCP-47 tag. Blank/unknown tags fall back to `en`. */
  readonly locale?: string;
  /** IANA zone. Blank/unknown zones fall back to `UTC` (via the browser zone). */
  readonly timeZone?: string;
  readonly dateStyle?: DateStyleLevel;
  readonly timeStyle?: DateStyleLevel;
}

/** `Date`, or `undefined` for anything `Date` cannot read. Never throws. */
export function toDate(value: DateLike): Date | undefined {
  if (value instanceof Date) {
    return Number.isNaN(value.getTime()) ? undefined : value;
  }
  if (typeof value === 'number') {
    const fromNumber = new Date(value);
    return Number.isNaN(fromNumber.getTime()) ? undefined : fromNumber;
  }
  if (typeof value !== 'string' || value.trim() === '') {
    return undefined;
  }
  const fromString = new Date(value);
  return Number.isNaN(fromString.getTime()) ? undefined : fromString;
}

/**
 * True when `locale` is a tag `Intl` can actually format with.
 *
 * Exists so the locale fallback is a *check* rather than a `try`/`catch` that
 * silently re-runs the formatter, and so callers can decide whether to warn
 * before they format.
 */
export function isSupportedLocaleTag(locale: string | undefined): boolean {
  if (locale === undefined || locale.trim() === '') {
    return false;
  }
  try {
    new Intl.DateTimeFormat(locale);
    return true;
  } catch {
    return false;
  }
}

/** True when `timeZone` is a zone `Intl` can actually format with. */
export function isValidTimeZone(timeZone: string | undefined): boolean {
  if (timeZone === undefined || timeZone.trim() === '') {
    return false;
  }
  try {
    new Intl.DateTimeFormat('en', { timeZone });
    return true;
  } catch {
    return false;
  }
}

/** Caller tag → a tag `Intl` accepts, else `en`. Pure. */
export function resolveLocale(locale: string | undefined): string {
  return isSupportedLocaleTag(locale) ? (locale as string) : FALLBACK_LOCALE;
}

/**
 * The effective IANA timezone: explicit preference first, browser zone second,
 * `UTC` when the preference is unknown/invalid (`Intl` throws `RangeError` for
 * those, and that must never propagate out of render).
 */
export function resolveTimeZone(preferred?: string): string {
  if (isValidTimeZone(preferred)) {
    return preferred as string;
  }
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || FALLBACK_TIME_ZONE;
  } catch {
    return FALLBACK_TIME_ZONE;
  }
}

/**
 * The zone used for *storage-grade* decisions: a preference or `UTC`, never the
 * browser zone. Anything that renders an authoritative instant (an audit row, an
 * export manifest) uses this, because two operators in two browsers must see
 * the same string for the same event.
 */
export function resolveExplicitTimeZone(preferred?: string): string {
  return isValidTimeZone(preferred) ? (preferred as string) : FALLBACK_TIME_ZONE;
}

function formatter(options: FormatDateOptions, extra: Intl.DateTimeFormatOptions): Intl.DateTimeFormat {
  return new Intl.DateTimeFormat(resolveLocale(options.locale), {
    timeZone: resolveTimeZone(options.timeZone),
    ...extra,
  });
}

/**
 * Locale-aware date **and** time, `medium`/`short` by default (Task 018's
 * contract, preserved verbatim). An unreadable value renders as its own raw
 * text so a broken timestamp is visible rather than silently blank.
 */
export function formatDate(value: DateLike, options?: FormatDateOptions): string {
  const date = toDate(value);
  if (date === undefined) {
    return String(value);
  }
  try {
    return formatter(options ?? {}, {
      dateStyle: options?.dateStyle ?? 'medium',
      timeStyle: options?.timeStyle ?? 'short',
    }).format(date);
  } catch {
    return date.toISOString();
  }
}

/** Date only, `medium` by default. Unreadable values render raw. */
export function formatDateOnly(value: DateLike, options?: FormatDateOptions): string {
  const date = toDate(value);
  if (date === undefined) {
    return String(value);
  }
  try {
    return formatter(options ?? {}, { dateStyle: options?.dateStyle ?? 'medium' }).format(date);
  } catch {
    return date.toISOString();
  }
}

/** Time only, `short` by default. Unreadable values render raw. */
export function formatTimeOnly(value: DateLike, options?: FormatDateOptions): string {
  const date = toDate(value);
  if (date === undefined) {
    return String(value);
  }
  try {
    return formatter(options ?? {}, {
      timeStyle: options?.timeStyle ?? 'short',
    }).format(date);
  } catch {
    return date.toISOString();
  }
}

/**
 * The Task 045 "timezone-aware timestamp display": date, time **and** the
 * resolved zone, e.g. `15 Jan 2024, 13:00 GMT+1`.
 *
 * The zone is in the string on purpose. A transcript editor showing two people's
 * edits at the same instant must be able to tell, from the row alone, which zone
 * it is in — otherwise "13:00" in the same column means two different moments.
 * Use `resolveExplicitTimeZone` for anything that is persisted or exported.
 *
 * THE ZONE NAME IS A SECOND `Intl` CALL, NOT A COMBINED OPTION
 * ----------------------------------------------------------
 * `timeZoneName` cannot be passed alongside `dateStyle`/`timeStyle`: the `*Style`
 * shortcuts and the individual component options are mutually exclusive, and
 * asking for both throws a `TypeError` rather than merging. The catch would then
 * have returned `toISOString()`, i.e. `2024-01-15T12:00:00.000Z` — the "never
 * throws" guarantee quietly turning into a machine string on screen. This is
 * asserted in `dates.spec.ts` because nothing about the call site looks wrong.
 */
export function formatTimestamp(value: DateLike, options?: FormatDateOptions): string {
  const date = toDate(value);
  if (date === undefined) {
    return String(value);
  }
  const locale = resolveLocale(options?.locale);
  const timeZone = resolveTimeZone(options?.timeZone);
  try {
    const stamp = new Intl.DateTimeFormat(locale, {
      dateStyle: options?.dateStyle ?? 'medium',
      timeStyle: options?.timeStyle ?? 'short',
      timeZone,
    }).format(date);
    const zoneName = new Intl.DateTimeFormat(locale, { timeZone, timeZoneName: 'short' })
      .formatToParts(date)
      .find((part) => part.type === 'timeZoneName')?.value;
    // U+00A0 rather than a normal space: the zone label is a unit with the time
    // and must not wrap onto its own line in a table cell.
    return zoneName === undefined || zoneName === '' ? stamp : `${stamp}\u00a0${zoneName}`;
  } catch {
    return date.toISOString();
  }
}

/**
 * The instant as an ISO-8601 UTC string, for `datetime` attributes, exports and
 * `Date.parse` round-trips. Independent of both locale and zone by definition —
 * an `<time datetime>` carries an instant, not a wall clock.
 */
export function toIsoString(value: DateLike): string {
  const date = toDate(value);
  return date === undefined ? '' : date.toISOString();
}