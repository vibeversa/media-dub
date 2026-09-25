import { useMutation, useQuery } from '@tanstack/react-query';
import type { QueryClient, UseMutationResult, UseQueryResult } from '@tanstack/react-query';
import { apiClient } from '../../api/client/index.js';
import { normalizeError } from '../../api/errors/index.js';
import type { AppError } from '../../api/errors/index.js';
import { newIdempotencyKey } from '../../api/client/index.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { useIsAuthenticated } from '../auth/useSession.js';
import {
  isAllowlistedFormat,
  normalizeExportFormat,
  parseExport,
  parseExports,
  parseOutput,
  parseProcessingRuns,
} from './types.js';
import type { ExportView, OutputView, ProcessingRunView } from './types.js';

export const EXPORTS_PAGE_SIZE = 100;

export const OUTPUTS_POLL_INTERVAL_MS = 15_000;

/**
 * Output + export read and mutation surface (Task 033).
 *
 * - `useOutputs(projectId)` is the output readiness aggregate on
 *   `queryKeys.outputs.detail(projectId)` (`GET /projects/{id}/output`).
 *   Generating aggregates poll on a display cadence; terminal states stop.
 *   Live updates also flow through the Task 026 registry
 *   (`run`/`stage`/`export`/`output` invalidate this key).
 * - `useExports(projectId)` is the export job list on
 *   `queryKeys.exports.list(projectId)` (`GET /projects/{id}/exports`,
 *   newest first). Active generations poll; terminal lists stop.
 * - `useProcessingRuns(projectId)` feeds the ExportCard run selector
 *   (`GET /projects/{id}/processing`, ids + statuses only).
 * - `useCreateExport(projectId)` posts `{ format, profile?, allowPartial? }`
 *   with a per-attempt `Idempotency-Key`. Parameters are validated against
 *   the backend allowlists before any network call; the server remains
 *   authoritative. Success invalidates `queryKeys.exports` (list + detail
 *   prefix) so the new generation row appears; Task 026 events drive
 *   subsequent transitions.
 * - `fetchExportDownloadUrl` is click-time only (never a query, never
 *   cached): it calls the 302 download endpoint once, and on `URL_EXPIRED`
 *   (410) refetches exactly once. Signed URLs live in the click handler
 *   only — never in state, never in filter URLs, never logged.
 */

export async function fetchOutput(projectId: string, signal?: AbortSignal): Promise<OutputView> {
  try {
    const response = await apiClient.getOutput(
      { path: { projectId } },
      signal !== undefined ? { signal } : undefined,
    );
    return parseOutput(response as unknown);
  } catch (error) {
    throw normalizeError(error, { method: 'GET' });
  }
}

export function useOutputs(projectId: string): UseQueryResult<OutputView, AppError> {
  const enabled = useIsAuthenticated() && projectId !== '';
  return useQuery<OutputView, AppError>({
    queryKey: queryKeys.outputs.detail(projectId),
    queryFn: async ({ signal }): Promise<OutputView> => fetchOutput(projectId, signal),
    enabled,
    refetchInterval: (query) => {
      const data = query.state.data as OutputView | undefined;
      if (data !== undefined && data.state === 'Generating') {
        return OUTPUTS_POLL_INTERVAL_MS;
      }
      return false;
    },
  });
}

export async function fetchExports(projectId: string, signal?: AbortSignal): Promise<readonly ExportView[]> {
  try {
    const response = await apiClient.listExports(
      { path: { projectId }, query: { page: 1, pageSize: EXPORTS_PAGE_SIZE } },
      signal !== undefined ? { signal } : undefined,
    );
    return parseExports(response as unknown);
  } catch (error) {
    throw normalizeError(error, { method: 'GET' });
  }
}

export function useExports(projectId: string): UseQueryResult<readonly ExportView[], AppError> {
  const enabled = useIsAuthenticated() && projectId !== '';
  return useQuery<readonly ExportView[], AppError>({
    queryKey: queryKeys.exports.list(projectId),
    queryFn: async ({ signal }): Promise<readonly ExportView[]> => fetchExports(projectId, signal),
    enabled,
    refetchInterval: (query) => {
      const data = query.state.data as readonly ExportView[] | undefined;
      if (data !== undefined && data.some((entry) => entry.displayState === 'queued' || entry.displayState === 'generating')) {
        return OUTPUTS_POLL_INTERVAL_MS;
      }
      return false;
    },
  });
}

export async function fetchProcessingRuns(projectId: string, signal?: AbortSignal): Promise<readonly ProcessingRunView[]> {
  try {
    const response = await apiClient.listProcessingRuns(
      { path: { projectId }, query: { page: 1, pageSize: 20 } },
      signal !== undefined ? { signal } : undefined,
    );
    return parseProcessingRuns(response as unknown);
  } catch (error) {
    throw normalizeError(error, { method: 'GET' });
  }
}

export function useProcessingRuns(projectId: string): UseQueryResult<readonly ProcessingRunView[], AppError> {
  const enabled = useIsAuthenticated() && projectId !== '';
  return useQuery<readonly ProcessingRunView[], AppError>({
    queryKey: queryKeys.runs.list(projectId),
    queryFn: async ({ signal }): Promise<readonly ProcessingRunView[]> => fetchProcessingRuns(projectId, signal),
    enabled,
  });
}

export interface CreateExportVariables {
  readonly format: string;
  readonly profile?: string;
  readonly allowPartial: boolean;
}

export interface CreateExportResult {
  readonly id: string;
  readonly format: string;
  readonly status: string;
  readonly isPartial: boolean;
}

/**
 * Export creation mutation (POST `exports` with `{ format, profile?,
 * allowPartial? }`). Validates the format against the backend allowlist
 * before touching the network (server re-validates). Sends a per-attempt
 * `Idempotency-Key` so double-submits collapse to one job.
 */
export function useCreateExport(projectId: string): UseMutationResult<CreateExportResult, AppError, CreateExportVariables> {
  return useMutation<CreateExportResult, AppError, CreateExportVariables>({
    mutationFn: async (variables): Promise<CreateExportResult> => {
      const normalized = normalizeExportFormat(variables.format);
      if (normalized === undefined || !isAllowlistedFormat(normalized)) {
        throw normalizeError(
          {
            status: 400,
            code: 'VALIDATION_FAILED',
            message: 'Select a valid export format.',
            correlationId: '',
            details: {},
          },
          { method: 'POST' },
        );
      }
      try {
        const body: { format: string; profile?: string; allowPartial?: boolean } = {
          format: normalized,
          allowPartial: variables.allowPartial,
        };
        if (variables.profile !== undefined && variables.profile !== '') {
          body.profile = variables.profile;
        }
        const response = await apiClient.createExport(
          { path: { projectId } },
          body as unknown as Parameters<typeof apiClient.createExport>[1],
          { idempotencyKey: newIdempotencyKey() },
        );
        const parsed = parseExport(response as unknown);
        return {
          id: parsed?.id ?? '',
          format: parsed?.format ?? normalized,
          status: parsed?.status ?? '',
          isPartial: parsed?.isPartial ?? false,
        };
      } catch (error) {
        throw normalizeError(error, { method: 'POST' });
      }
    },
    retry: false,
  });
}

export interface ExportDownloadView {
  readonly url: string;
}

/**
 * Click-time signed-URL fetch for one export (never a query, never cached).
 * Calls the 302 download endpoint; on `URL_EXPIRED` (410) refetches exactly
 * once. Double expiry rejects with the normalized error so the row shows a
 * retry action. The URL lives in the caller handler only.
 *
 * Note: the generated `downloadExport` uses `requestRaw` with
 * `redirect: manual`, which returns the 302 response but throws for every
 * other non-2xx (410/404/409). Both returned 410 responses and thrown 410
 * `ApiError`s refetch exactly once here.
 */
export async function fetchExportDownloadUrl(projectId: string, exportId: string): Promise<ExportDownloadView> {
  async function once(): Promise<Response> {
    return apiClient.downloadExport({ path: { projectId, exportId } });
  }
  function locationOf(response: Response): string | undefined {
    const direct = response.headers.get('Location') ?? response.headers.get('location');
    if (direct !== null && direct !== '') {
      return direct;
    }
    const url = typeof response.url === 'string' ? response.url : '';
    if (url !== '' && url.toLowerCase().startsWith('https://')) {
      return url;
    }
    return undefined;
  }
  function toNormalized(error: unknown): AppError {
    return normalizeError(error, { method: 'GET' });
  }
  function isExpired(value: unknown): boolean {
    const normalized = toNormalized(value);
    return normalized.code === 'URL_EXPIRED' || normalized.status === 410;
  }
  function isMissing(value: unknown): boolean {
    const normalized = toNormalized(value);
    return normalized.status === 404 || normalized.code.includes('NOT_FOUND');
  }
  try {
    const first = await once();
    if (first.status === 410) {
      try {
        const second = await once();
        if (second.status === 410) {
          throw toNormalized({ status: 410, code: 'URL_EXPIRED', message: 'This download link expired. Request a fresh download.', correlationId: '', details: {} });
        }
        const secondUrl = locationOf(second);
        if (secondUrl === undefined || secondUrl === '') {
          throw toNormalized({ status: 410, code: 'URL_EXPIRED', message: 'This download link expired. Request a fresh download.', correlationId: '', details: {} });
        }
        return { url: secondUrl };
      } catch (error) {
        if (isExpired(error)) {
          throw toNormalized({ status: 410, code: 'URL_EXPIRED', message: 'This download link expired. Request a fresh download.', correlationId: '', details: {} });
        }
        throw toNormalized(error);
      }
    }
    if (first.status === 404) {
      throw toNormalized({ status: 404, code: 'NOT_FOUND', message: 'This export no longer exists.', correlationId: '', details: {} });
    }
    if (!first.ok && first.status !== 302 && first.type !== 'opaqueredirect') {
      let code = 'INTERNAL_ERROR';
      let message = 'The download could not be prepared.';
      try {
        const body = (await first.clone().json()) as { error?: { code?: string; message?: string } };
        if (typeof body.error?.code === 'string') {
          code = body.error.code;
        }
        if (typeof body.error?.message === 'string' && body.error.message !== '') {
          message = body.error.message;
        }
      } catch {
        // Keep the generic envelope.
      }
      throw toNormalized({ status: first.status, code, message, correlationId: '', details: {} });
    }
    const url = locationOf(first);
    if (url === undefined || url === '') {
      throw toNormalized({ status: 500, code: 'INTERNAL_ERROR', message: 'The download could not be prepared.', correlationId: '', details: {} });
    }
    return { url };
  } catch (error) {
    const normalized = toNormalized(error);
    if (normalized.code === 'URL_EXPIRED' || normalized.status === 410) {
      try {
        const second = await once();
        const secondUrl = locationOf(second);
        if (secondUrl === undefined || secondUrl === '') {
          throw toNormalized({ status: 410, code: 'URL_EXPIRED', message: 'This download link expired. Request a fresh download.', correlationId: '', details: {} });
        }
        return { url: secondUrl };
      } catch (retryError) {
        const retryNormalized = toNormalized(retryError);
        if (retryNormalized.code === 'URL_EXPIRED' || retryNormalized.status === 410) {
          throw toNormalized({ status: 410, code: 'URL_EXPIRED', message: 'This download link expired. Request a fresh download.', correlationId: '', details: {} });
        }
        if (isMissing(retryError)) {
          throw toNormalized({ status: 404, code: 'NOT_FOUND', message: 'This export no longer exists.', correlationId: '', details: {} });
        }
        throw retryNormalized;
      }
    }
    if (isMissing(error)) {
      throw toNormalized({ status: 404, code: 'NOT_FOUND', message: 'This export no longer exists.', correlationId: '', details: {} });
    }
    throw normalized;
  }
}

/**
 * Click-time output descriptor fetch (never a query, never cached).
 * Used for ready output items that expose no per-asset URL. Single refetch
 * on expiry, same contract as the export path.
 */
export async function fetchOutputDownloadUrl(projectId: string): Promise<ExportDownloadView> {
  try {
    const response = await apiClient.downloadOutput({ path: { projectId } });
    const record = response as unknown as Record<string, unknown>;
    const url = typeof record['downloadUrl'] === 'string' ? (record['downloadUrl'] as string) : '';
    if (url === '') {
      throw normalizeError(
        { status: 500, code: 'INTERNAL_ERROR', message: 'The download could not be prepared.', correlationId: '', details: {} },
        { method: 'GET' },
      );
    }
    return { url };
  } catch (error) {
    throw normalizeError(error, { method: 'GET' });
  }
}

/** Invalidates the output aggregate after an expiry refetch. */
export async function invalidateOutputs(queryClient: QueryClient, projectId: string): Promise<void> {
  await queryClient.invalidateQueries({ queryKey: queryKeys.outputs.detail(projectId) });
}

/** Invalidates the export list (+ single detail) after create/retry/delete. */
export async function invalidateExports(queryClient: QueryClient, projectId: string, exportId?: string): Promise<void> {
  await queryClient.invalidateQueries({ queryKey: queryKeys.exports.list(projectId) });
  if (exportId !== undefined && exportId !== '') {
    await queryClient.invalidateQueries({ queryKey: queryKeys.exports.detail(projectId, exportId) });
  }
}
