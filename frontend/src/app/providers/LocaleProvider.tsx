import { useEffect } from 'react';
import type { ReactNode } from 'react';
import { I18nextProvider } from 'react-i18next';
import i18n from '../../i18n/i18n.js';
import { applyDirection } from '../../i18n/format.js';
import { useAppStore } from '../../stores/index.js';

interface LocaleProviderProps {
  readonly children: ReactNode;
}

/**
 * Locale provider (Task 018): supplies the initialized i18next instance and
 * syncs `document.dir`/`lang` with the store locale (RTL via `dir`; shared
 * CSS is logical-properties-only from Task 016). Changing locales goes
 * through `useLocale().setLocale`, which persists + re-syncs.
 */
export function LocaleProvider({ children }: LocaleProviderProps): ReactNode {
  const locale = useAppStore((s) => s.locale);
  useEffect(() => {
    applyDirection(locale);
    if (i18n.language !== locale) {
      void i18n.changeLanguage(locale);
    }
  }, [locale]);
  return <I18nextProvider i18n={i18n}>{children}</I18nextProvider>;
}
