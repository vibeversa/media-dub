import { QueryClient } from '@tanstack/react-query';

// Single shared client for the whole app (Task 015, R4). Per-component
// construction is a defect: caches, deduping, and the Task 017 GET-only retry
// policy all assume one instance. GET-only retry and error normalization land
// in Task 017; the scaffold default is retry: false everywhere.
export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: false,
      staleTime: 30_000,
      gcTime: 5 * 60_000,
      refetchOnWindowFocus: false,
    },
    mutations: {
      retry: false,
    },
  },
});
