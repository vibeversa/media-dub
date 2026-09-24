import { useEffect } from 'react';
import type { ReactNode } from 'react';
import { useAppStore } from '../../stores/index.js';

interface ThemeProviderProps {
  readonly children: ReactNode;
}

/**
 * Theme provider (Task 018): mirrors the store theme onto
 * `documentElement[data-theme]` so the Task 016 token overrides apply.
 * The switcher lives in `AppShell`; this provider only owns the DOM sync.
 */
export function ThemeProvider({ children }: ThemeProviderProps): ReactNode {
  const theme = useAppStore((s) => s.theme);
  useEffect(() => {
    document.documentElement.setAttribute('data-theme', theme);
  }, [theme]);
  return <>{children}</>;
}
