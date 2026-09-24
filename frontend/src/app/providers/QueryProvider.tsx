import { QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { queryClient } from './queryClient.js';

interface QueryProviderProps {
  readonly children: ReactNode;
}

export function QueryProvider({ children }: QueryProviderProps): ReactNode {
  return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
}
