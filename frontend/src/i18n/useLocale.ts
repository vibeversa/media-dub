import { useCallback } from 'react';
import i18n from './i18n.js';
import { applyDirection, formatDate, formatNumber, isRtlLocale, resolveTimeZone } from './format.js';
import type { FormatDateOptions, FormatNumberOptions } from './format.js';
import { useAppStore } from '../stores/index.js';

export interface LocaleApi {
  /** BCP-47 locale tag, e.g. `en`, `ar`. */
  readonly locale: string;
  readonly isRtl: boolean;
  /** Effective tenant timezone (preference → browser → UTC). */
  readonly timeZone: string;
  readonly setLocale: (locale: string) => void;
  readonly formatDate: (value: string | number | Date, options?: Omit<FormatDateOptions, 'locale' | 'timeZone'>) => string;
  readonly formatNumber: (value: number, options?: Omit<FormatNumberOptions, 'locale'>) => string;
}

/**
 * Locale API (Task 018): active locale from the store, tenant timezone from
 * preferences (Task 035 writes `tenantTimezone`; defaults to the browser
 * zone), RTL derived from the tag. Switching locales syncs i18next,
 * `document.dir`/`lang`, and the persisted store value.
 */
export function useLocale(): LocaleApi {
  const locale = useAppStore((s) => s.locale);
  const tenantTimezone = useAppStore((s) => s.tenantTimezone);
  const setStoredLocale = useAppStore((s) => s.setLocale);
  const timeZone = resolveTimeZone(tenantTimezone);

  const setLocale = useCallback(
    (next: string) => {
      const normalized = next === '' ? 'en' : next;
      setStoredLocale(normalized);
      applyDirection(normalized);
      void i18n.changeLanguage(normalized);
    },
    [setStoredLocale],
  );

  const formatDateLocal = useCallback(
    (value: string | number | Date, options?: Omit<FormatDateOptions, 'locale' | 'timeZone'>): string =>
      formatDate(value, { ...options, locale, timeZone }),
    [locale, timeZone],
  );

  const formatNumberLocal = useCallback(
    (value: number, options?: Omit<FormatNumberOptions, 'locale'>): string =>
      formatNumber(value, { ...options, locale }),
    [locale],
  );

  return { locale, isRtl: isRtlLocale(locale), timeZone, setLocale, formatDate: formatDateLocal, formatNumber: formatNumberLocal };
}
