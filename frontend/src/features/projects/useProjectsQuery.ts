import { keepPreviousData, useQuery } from '@tanstack/react-query';
import type { UseQueryResult } from '@tanstack/react-query';
import { normalizeError } from '../../api/errors/index.js';
import type { AppError } from '../../api/errors/index.js';
import type { ProjectListResponse } from '../../api/client/index.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { useIsAuthenticated } from '../auth/useSession.js';
import { fetchProjects, toServerQuery } from './api.js';
import type { ProjectFilters } from './api.js';

/**
 * Server-side project list query (Task 021, R2).
 *
 * - Key: `queryKeys.projects.list` over the server query only, so each
 *   filter/sort/page combination caches independently. Client-only
 *   refinements (target language, date range) stay out of the key — they
 *   filter the fetched page in memory without refetching.
 * - `placeholderData: keepPreviousData` keeps the previous page rendered
 *   during page changes (no scroll jump, no blank table).
 * - Gated on the Task 019 session — no feature query fires before
 *   authentication.
 */
export function useProjectsQuery(filters: ProjectFilters): UseQueryResult<ProjectListResponse, AppError> {
  const enabled = useIsAuthenticated();
  const server = toServerQuery(filters);
  return useQuery<ProjectListResponse, AppError>({
    queryKey: queryKeys.projects.list(server),
    queryFn: async (): Promise<ProjectListResponse> => {
      try {
        return await fetchProjects(server);
      } catch (error) {
        throw normalizeError(error, { method: 'GET' });
      }
    },
    enabled,
    placeholderData: keepPreviousData,
  });
}
