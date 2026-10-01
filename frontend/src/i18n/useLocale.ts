import { useCallback } from 'react';
import i18n from './i18n.js';
import { applyDirection, currentDirectionOverride, isRtlLocale, resolveDirection } from './direction.js';
import type { Direction } from './direction.js';
import { formatDate as formatDateImpl, formatNumber as formatNumberImpl, resolveTimeZone } from './format.js';
import type { FormatDateOptions, FormatNumberOptions } from './format.js';
import { useAppStore } from '../stores/index.js';

export interface LocaleApi {
  /** BCP-47 locale tag, e.g. `en`, `ar`, `en-US`. */
  readonly locale: string;
  /** True when the locale reads right-to-left, ignoring any `?dir=` override. */
  readonly isRtl: boolean;
  /** Effective writing direction actually applied to `<html dir>`. */
  readonly direction: Direction;
  /** `?dir=` manual override, or `undefined` when there is none. */
  readonly directionOverride: Direction | undefined;
  /** Effective tenant timezone (preference → browser → UTC). */
  readonly timeZone: string;
  readonly setLocale: (locale: string) => void;
  readonly formatDate: (value: string | number | Date, options?: Omit<FormatDateOptions, 'locale' | 'timeZone'>) => string;
  readonly formatNumber: (value: number, options?: Omit<FormatNumberOptions, 'locale'>) => string;
}

/**
 * Locale API (Task 018; direction + timezone resolution from Task 045): active
 * locale from the store, tenant timezone from preferences (Task 035 writes
 * `tenantTimezone`; defaults to the browser zone), RTL derived from the tag.
 * Switching locales syncs i18next, `document.dir`/`lang`, and the persisted
 * store value.
 *
 * The two `format*` helpers are the Task 045 implementations with the two
 * arguments a caller never has (locale, timezone) bound in. Nothing here formats
 * anything itself.
 *
 * `setLocale` re-reads the `?dir=` override and passes it to `applyDirection`, so
 * the manual override survives a locale switch: it is a property of the URL, not
 * of the locale.
 *
 * An empty tag falls back to `en` rather than being stored: an empty `lang`
 * attribute makes assistive technology fall back to its own default, which is
 * not the locale the rest of the app is rendering.
 */
export function useLocale(): LocaleApi {
  const locale = useAppStore((s) => s.locale);
  const tenantTimezone = useAppStore((s) => s.tenantTimezone);
  const setStoredLocale = useAppStore((s) => s.setLocale);
  const timeZone = resolveTimeZone(tenantTimezone);
  const directionOverride = currentDirectionOverride();

  const setLocale = useCallback(
    (next: string) => {
      const normalized = next === '' ? 'en' : next;
      setStoredLocale(normalized);
      applyDirection(normalized, currentDirectionOverride());
      void i18n.changeLanguage(normalized);
    },
    [setStoredLocale],
  );

  const formatDateLocal = useCallback(
    (value: string | number | Date, options?: Omit<FormatDateOptions, 'locale' | 'timeZone'>): string =>
      formatDateImpl(value, { ...options, locale, timeZone }),
    [locale, timeZone],
  );

  const formatNumberLocal = useCallback(
    (value: number, options?: Omit<FormatNumberOptions, 'locale'>): string =>
      formatNumberImpl(value, { ...options, locale }),
    [locale],
  );

  return {
    locale,
    isRtl: isRtlLocale(locale),
    direction: resolveDirection(locale, directionOverride),
    directionOverride,
    timeZone,
    setLocale,
    formatDate: formatDateLocal,
    formatNumber: formatNumberLocal,
  };
}