import { useQuery } from '@tanstack/react-query';
import type { UseQueryResult } from '@tanstack/react-query';
import { apiFetch } from '../../api/client/index.js';
import { normalizeError } from '../../api/errors/index.js';
import type { AppError } from '../../api/errors/index.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { useIsAuthenticated } from '../auth/useSession.js';
import { parseLocalGpu } from './localGpuTypes.js';
import type { LocalGpuView } from './localGpuTypes.js';

/**
 * Operator-only local-GPU health read (Task 044, §19.3).
 *
 * Separate from the Task 036 provider-health poll on purpose: the provider
 * pool is a multi-provider aggregate and this is one device. They report
 * different subjects, and a reader who sees them merged cannot tell which one
 * degraded. The query key is a sibling under `admin.diagnostics`, so an
 * operator refresh that invalidates the diagnostics prefix covers it too.
 *
 * The endpoint is FRONTEND-ANTICIPATED and admin-gated. `AdminController` has
 * no `/admin/local-gpu` route yet, so the request lands on its catch-all and
 * answers `404 ADMIN_ROUTE_UNKNOWN`. That is a deployment fact, not an error,
 * and the panel renders `NotAvailableState` for it rather than a red box.
 *
 * `retry: false`: a health endpoint that is down must not be turned into a
 * request storm, and the panel already offers an explicit retry.
 */
export function useLocalGpuHealth(enabled: boolean): UseQueryResult<LocalGpuView, AppError> {
  const authenticated = useIsAuthenticated();
  return useQuery<LocalGpuView, AppError>({
    queryKey: queryKeys.diagnostics.localGpu(),
    queryFn: async ({ signal }): Promise<LocalGpuView> => {
      const raw = await apiFetch<unknown>('/admin/local-gpu', signal !== undefined ? { signal } : undefined);
      return parseLocalGpu(raw);
    },
    enabled: authenticated && enabled,
    staleTime: 30_000,
    refetchInterval: 30_000,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    retry: false,
  });
}

/** Normalizes an error thrown by this module's read path. */
export function normalizeAdminGet(error: unknown): AppError {
  return normalizeError(error, { method: 'GET' });
}