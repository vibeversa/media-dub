import { useEffect, useMemo } from 'react';
import type { ReactNode } from 'react';
import { I18nextProvider } from 'react-i18next';
import i18n from '../../i18n/i18n.js';
import { applyDirection } from '../../i18n/direction.js';
import { currentDirectionOverride } from '../../i18n/direction.js';
import { useAppStore } from '../../stores/index.js';

interface LocaleProviderProps {
  readonly children: ReactNode;
}

/**
 * Locale provider (Task 018; `?dir=` override from Task 045): supplies the
 * initialized i18next instance and syncs `document.dir`/`lang` with the store
 * locale. Changing locales goes through `useLocale().setLocale`, which persists
 * and re-syncs.
 *
 * THE `?dir=` OVERRIDE
 * --------------------
 * Read once, from `window.location.search`, into `useMemo`. It is a property of
 * the URL, so re-reading it on every locale change would be wasted work, and
 * memoising means a client-side navigation that rewrites the query string cannot
 * silently re-mirror the app mid-session — the override lasts as long as the
 * document does.
 *
 * It is presentation-only by construction: the effect below reads it and writes
 * `<html dir>`, and nothing in this file (or anywhere else) lets it reach the
 * store, i18next, auth, or a request.
 */
export function LocaleProvider({ children }: LocaleProviderProps): ReactNode {
  const locale = useAppStore((s) => s.locale);
  const directionOverride = useMemo(() => currentDirectionOverride(), []);
  useEffect(() => {
    applyDirection(locale, directionOverride);
    if (i18n.language !== locale) {
      void i18n.changeLanguage(locale);
    }
  }, [locale, directionOverride]);
  return <I18nextProvider i18n={i18n}>{children}</I18nextProvider>;
}