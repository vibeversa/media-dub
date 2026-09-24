import type { ReactNode } from 'react';

// Locale/i18n provider implementation lands in Task 018; this stub fixes the
// provider slot so App.tsx is stable.
interface LocaleProviderProps {
  readonly children: ReactNode;
}

export function LocaleProvider({ children }: LocaleProviderProps): ReactNode {
  return <>{children}</>;
}
