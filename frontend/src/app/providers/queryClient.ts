import { QueryClient } from '@tanstack/react-query';

// Single shared client for the whole app (Task 015, R4). Per-component
// construction is a defect: caches, deduping, and the Task 017 GET-only retry
// policy all assume one instance. Retry stays disabled here on purpose:
// `src/api/client/httpClient.ts` owns GET-only retry (network + 502/503/504,
// up to 2x) at the transport, and `useAppMutation` normalizes mutation
// errors to `AppError`; a second retry layer here would multiply attempts.
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
