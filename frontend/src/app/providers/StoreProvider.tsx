import type { ReactNode } from 'react';

// Zustand store root lives in Task 018 (auth/session slices); this stub keeps
// App.tsx compiling and fixes the provider nesting order early.
interface StoreProviderProps {
  readonly children: ReactNode;
}

export function StoreProvider({ children }: StoreProviderProps): ReactNode {
  return <>{children}</>;
}
