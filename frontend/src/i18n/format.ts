export const FALLBACK_TIME_ZONE = 'UTC';

/** Locales that lay out right-to-left. */
const RTL_LOCALES = new Set(['ar', 'he', 'fa', 'ur', 'ps', 'yi', 'dv']);

function baseLanguage(locale: string): string {
  const dash = locale.indexOf('-');
  const base = (dash >= 0 ? locale.slice(0, dash) : locale).toLowerCase();
  return base === '' ? 'en' : base;
}

/** True when the locale lays out right-to-left. */
export function isRtlLocale(locale: string): boolean {
  return RTL_LOCALES.has(baseLanguage(locale));
}

/**
 * Syncs `document.dir`/`document.lang` with the active locale (Task 018, R4
 * companion). Shared CSS is logical-properties-only (Task 016), so flipping
 * `dir` mirrors the whole shell. Safe to call in tests (jsdom has document).
 */
export function applyDirection(locale: string): void {
  if (typeof document === 'undefined') {
    return;
  }
  document.documentElement.setAttribute('dir', isRtlLocale(locale) ? 'rtl' : 'ltr');
  document.documentElement.setAttribute('lang', locale);
}

/**
 * Resolves the effective IANA timezone: explicit tenant preference first,
 * browser zone second, `UTC` when the preference is unknown/invalid
 * (Intl throws RangeError for those — never propagate it to render).
 */
export function resolveTimeZone(preferred?: string): string {
  if (preferred !== undefined && preferred !== '') {
    try {
      new Intl.DateTimeFormat('en', { timeZone: preferred });
      return preferred;
    } catch {
      return FALLBACK_TIME_ZONE;
    }
  }
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone ?? FALLBACK_TIME_ZONE;
  } catch {
    return FALLBACK_TIME_ZONE;
  }
}

export interface FormatDateOptions {
  readonly locale?: string;
  /** Tenant timezone; unknown values fall back to UTC. */
  readonly timeZone?: string;
  readonly dateStyle?: 'full' | 'long' | 'medium' | 'short';
  readonly timeStyle?: 'full' | 'long' | 'medium' | 'short';
}

/** Locale-aware date formatting; invalid input renders the raw value, never throws. */
export function formatDate(value: string | number | Date, options?: FormatDateOptions): string {
  const date = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(date.getTime())) {
    return String(value);
  }
  const locale = options?.locale ?? 'en';
  try {
    return new Intl.DateTimeFormat(locale, {
      dateStyle: options?.dateStyle ?? 'medium',
      timeStyle: options?.timeStyle ?? 'short',
      timeZone: resolveTimeZone(options?.timeZone),
    }).format(date);
  } catch {
    return date.toISOString();
  }
}

export interface FormatNumberOptions {
  readonly locale?: string;
  readonly style?: 'decimal' | 'currency' | 'percent';
  readonly currency?: string;
}

/** Locale-aware number formatting; falls back to a plain string, never throws. */
export function formatNumber(value: number, options?: FormatNumberOptions): string {
  const locale = options?.locale ?? 'en';
  try {
    if (options?.style === 'currency') {
      return new Intl.NumberFormat(locale, {
        style: 'currency',
        currency: options.currency ?? 'USD',
      }).format(value);
    }
    return new Intl.NumberFormat(locale, { style: options?.style ?? 'decimal' }).format(value);
  } catch {
    return String(value);
  }
}

/** ICU plural category for the count in the locale (drives `_one/_few/...` keys). */
export function getPluralCategory(count: number, locale?: string): string {
  try {
    return new Intl.PluralRules(locale ?? 'en').select(count);
  } catch {
    return 'other';
  }
}
