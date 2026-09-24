import type { ReactNode } from 'react';

// Theme provider implementation (tokens, dark mode, CSS variables) lands in
// Task 016/018; this stub fixes the provider slot so App.tsx is stable.
interface ThemeProviderProps {
  readonly children: ReactNode;
}

export function ThemeProvider({ children }: ThemeProviderProps): ReactNode {
  return <>{children}</>;
}
