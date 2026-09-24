import { useEffect } from 'react';
import type { ReactNode } from 'react';
import { applyDirection } from '../../i18n/format.js';
import i18n from '../../i18n/i18n.js';
import { useAppStore } from '../../stores/index.js';
import { readE2eSessionSeed } from '../session/e2eSeed.js';

interface StoreProviderProps {
  readonly children: ReactNode;
}

/**
 * Store provider (Task 018): rehydrates persisted UI preferences (theme,
 * locale, telemetry opt-out — already read by the store initializer) into
 * their runtime side-effects (DOM theme attribute, `document.dir`/`lang`,
 * i18next language) on first mount, and honors the E2E session seed when
 * present (Playwright `@shell`; absent in production). Zustand itself needs
 * no context; this slot keeps the `App.tsx` nesting order stable for
 * Task 019 session work.
 */
export function StoreProvider({ children }: StoreProviderProps): ReactNode {
  const theme = useAppStore((s) => s.theme);
  const locale = useAppStore((s) => s.locale);
  useEffect(() => {
    document.documentElement.setAttribute('data-theme', theme);
    applyDirection(locale);
    if (i18n.language !== locale) {
      void i18n.changeLanguage(locale);
    }
    const seed = readE2eSessionSeed();
    if (seed !== undefined) {
      useAppStore.getState().setSession(seed.status, seed.permissions);
    }
  }, [theme, locale]);
  return <>{children}</>;
}
