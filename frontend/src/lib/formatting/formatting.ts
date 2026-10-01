/**
 * Locale-aware number, currency and plural helpers (Task 045, R2).
 *
 * THIS IS THE SINGLE NUMBER IMPLEMENTATION
 * ----------------------------------------
 * `src/i18n/format.ts` (Task 018) re-exports `formatNumber` / `getPluralCategory`
 * from here rather than keeping its own copies — see the note in
 * `../dates/dates.ts` for why a second copy is a defect rather than a
 * convenience.
 *
 * PLURALIZATION
 * -------------
 * i18next already resolves ICU-style `key_one` / `key_few` / `key_other`
 * suffixes through `Intl.PluralRules`; **feature copy must keep using `t()`**.
 * What lives here is the part `t()` cannot answer:
 *
 *   - `requiredPluralCategories()` — every category a locale needs. Used by the
 *     bundle-completeness test so a Russian bundle cannot ship `_one`/`_other`
 *     and silently fall back to English for 2–4 items.
 *   - `pluralKey()` / `pluralKeyCandidates()` — for code that builds a key
 *     (a dynamic badge count), and for asserting a bundle's key set.
 *
 * `getPluralCategory()` is exported because Task 018's callers and tests use it.
 */

import { FALLBACK_LOCALE } from '../dates/dates.js';

/** `Intl.PluralRules` category. Kept loose: ICU adds locales, not categories. */
export type PluralCategory = string;

export interface FormatNumberOptions {
  readonly locale?: string;
  readonly style?: 'decimal' | 'currency' | 'percent';
  readonly currency?: string;
  readonly minimumFractionDigits?: number;
  readonly maximumFractionDigits?: number;
  /** Compact notation (`12.3K`). Locale-aware, never a hand-rolled divide. */
  readonly compact?: boolean;
}

const BYTE_UNITS = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'] as const;

function safeLocale(locale: string | undefined): string {
  if (locale === undefined || locale.trim() === '') {
    return FALLBACK_LOCALE;
  }
  try {
    new Intl.NumberFormat(locale);
    return locale;
  } catch {
    return FALLBACK_LOCALE;
  }
}

/**
 * Locale-aware number formatting. Never throws: an unformattable locale or a
 * non-finite value renders as `String(value)`, which is also what makes this
 * safe to call straight from a render with untrusted payload fields.
 */
export function formatNumber(value: number, options?: FormatNumberOptions): string {
  const locale = safeLocale(options?.locale);
  const currency = options?.currency;
  try {
    const intl: Intl.NumberFormatOptions = { style: options?.style ?? 'decimal' };
    if (intl.style === 'currency') {
      // An invalid currency code makes the whole constructor throw, which would
      // take the number down with it. An unknown currency is not a reason to
      // render nothing, so the code is validated and the format downgraded.
      intl.currency = isCurrencyCode(currency) ? currency : 'USD';
    }
    if (options?.minimumFractionDigits !== undefined) {
      intl.minimumFractionDigits = options.minimumFractionDigits;
    }
    if (options?.maximumFractionDigits !== undefined) {
      intl.maximumFractionDigits = options.maximumFractionDigits;
    }
    if (options?.compact === true) {
      intl.notation = 'compact';
      intl.compactDisplay = 'short';
    }
    const formatted = new Intl.NumberFormat(locale, intl).format(value);
    return formatted === '' ? String(value) : formatted;
  } catch {
    return String(value);
  }
}

/** Currency with an explicit code; `USD` when the caller has none. */
export function formatCurrency(value: number, locale?: string, currency = 'USD'): string {
  return formatNumber(value, { locale, style: 'currency', currency });
}

/**
 * Compact notation (`12.3K`). Locale-aware, and never a hand-rolled
 * "divide by 1000 until it fits" — that gets both the threshold and the
 * rounding wrong in every locale whose grouping rules differ from English.
 */
export function formatCompactNumber(value: number, locale?: string): string {
  return formatNumber(value, { locale, compact: true });
}

/**
 * Ratio → percent. `0.155` renders `15.5%`, never `0.155%` — the callers all
 * hold ratios (usage, quality scores, lipsync confidence) and the multiplication
 * belongs here where it cannot be forgotten in one of them.
 */
export function formatPercent(value: number, locale?: string): string {
  // `style: 'percent'` defaults to zero fraction digits, so a 0.155 quality score
  // renders `16%` and a caller cannot tell 15.5% from 15.9%. One digit is enough
  // for every ratio in this product (usage, quality, lipsync confidence) and
  // `Intl` trims the trailing zero, so `1` still reads `100%`.
  return formatNumber(value, { locale, style: 'percent', maximumFractionDigits: 1 });
}

/**
 * Byte counts with binary units (`1 KB` = 1024 B) and a locale-aware number.
 *
 * Binary, because every size in this platform is a byte count from a file
 * system (`MediaAsset.SizeBytes`), and a storage screen that says 1 MB for
 * 1 048 576 bytes disagrees with every other storage screen.
 */
export function formatBytes(bytes: number, locale?: string, binary = true): string {
  if (!Number.isFinite(bytes)) {
    return String(bytes);
  }
  const negative = bytes < 0;
  let value = Math.abs(bytes);
  const base = binary ? 1024 : 1000;
  let unit = 0;
  while (value >= base && unit < BYTE_UNITS.length - 1) {
    value /= base;
    unit += 1;
  }
  // Both bounds, not just the maximum: `maximumFractionDigits: 1` alone trims to
  // `1 KB`, and a column of sizes that reads `1 KB / 512 B / 1 KB` is one the eye
  // cannot scan. `B` stays integral because a fraction of a byte is not a thing.
  const digits = unit === 0 ? 0 : 1;
  const sign = negative ? '-' : '';
  const size = formatNumber(value, {
    locale,
    minimumFractionDigits: digits,
    maximumFractionDigits: digits,
  });
  return `${sign}${size} ${BYTE_UNITS[unit]}`;
}

/** ICU plural category for `count` in `locale`. Never throws. */
export function getPluralCategory(count: number, locale?: string): string {
  try {
    return new Intl.PluralRules(locale ?? FALLBACK_LOCALE).select(count);
  } catch {
    return 'other';
  }
}

/**
 * Every plural category `locale` defines — `['one','other']` for English,
 * six categories for Arabic, three-plus-`other` for Russian.
 *
 * Read from the runtime rather than a hand-written table: a table is a
 * maintenance bug waiting for the next locale, and `Intl` is already the
 * authority i18next resolves against.
 */
export function requiredPluralCategories(locale?: string): readonly PluralCategory[] {
  try {
    return [...new Intl.PluralRules(safeLocale(locale)).resolvedOptions().pluralCategories];
  } catch {
    return ['other'];
  }
}

/** The suffix i18next appends for `category` (`'one'` → `'_one'`). */
export function pluralSuffix(category: PluralCategory): string {
  return `_${category}`;
}

/** `('items', 'few')` → `'items_few'`. The bundle key for that category. */
export function pluralKey(baseKey: string, category: PluralCategory): string {
  return `${baseKey}${pluralSuffix(category)}`;
}

/**
 * Every candidate key for `count`, most specific first, ending in `_other`.
 *
 * For code that has to *check* a bundle rather than resolve through it (the
 * completeness test) or that formats plural copy without i18next. The list is
 * ordered so the first present key wins, exactly as i18next orders its suffixes.
 */
export function pluralKeyCandidates(baseKey: string, count: number, locale?: string): readonly string[] {
  const selected = getPluralCategory(count, locale);
  const rest = requiredPluralCategories(locale).filter((category) => category !== selected && category !== 'other');
  return [...rest, 'other'].map((category) => pluralKey(baseKey, category)).concat(pluralKey(baseKey, selected));
}

/** True for a syntactically valid ISO-4217-shaped code (`USD`, `EUR`, `GBP`). */
export function isCurrencyCode(code: string | undefined): boolean {
  return typeof code === 'string' && /^[A-Za-z]{3}$/.test(code);
}

/**
 * Percentage delta with an explicit sign, for "storage +12%" style copy.
 * `Intl` has no signed-percent style, and the sign is the whole point.
 */
export function formatSignedPercent(value: number, locale?: string): string {
  const magnitude = formatPercent(Math.abs(value), locale);
  if (value > 0) {
    return `+${magnitude}`;
  }
  return value < 0 ? `-${magnitude}` : magnitude;
}