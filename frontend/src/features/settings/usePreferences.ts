import { useQuery } from '@tanstack/react-query';
import type { QueryClient, UseQueryResult } from '@tanstack/react-query';
import { apiClient } from '../../api/client/index.js';
import { normalizeError } from '../../api/errors/index.js';
import type { AppError } from '../../api/errors/index.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { useIsAuthenticated } from '../auth/useSession.js';

/**
 * Identity preferences read model (Task 035) over Task 006
 * (`GET /me/preferences`). The generated map is flat
 * (`Record<string, string>`); every value is an opaque string and parsing
 * happens in `types.ts` per key. Saves invalidate the preferences prefix
 * plus locale/theme consumers (the store slices own the applied values).
 */

async function fetchPreferencesMap(): Promise<Record<string, string>> {
  try {
    const response = await apiClient.getPreferences();
    return (response ?? {}) as Record<string, string>;
  } catch (error) {
    throw normalizeError(error, { method: 'GET' });
  }
}

/** Preferences query on the shared factory scope. */
export function usePreferencesQuery(): UseQueryResult<Record<string, string>, AppError> {
  const enabled = useIsAuthenticated();
  return useQuery<Record<string, string>, AppError>({
    queryKey: queryKeys.preferences.list(),
    queryFn: fetchPreferencesMap,
    enabled,
    staleTime: 60_000,
    refetchInterval: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    retry: false,
  });
}

/** Invalidates the preferences prefix (all keys). */
export async function invalidatePreferences(queryClient: QueryClient): Promise<void> {
  await queryClient.invalidateQueries({ queryKey: queryKeys.preferences.all });
}
