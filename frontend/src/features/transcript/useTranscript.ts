import { useQuery } from '@tanstack/react-query';
import type { QueryClient, UseMutationResult, UseQueryResult } from '@tanstack/react-query';
import { apiClient } from '../../api/client/index.js';
import { normalizeError } from '../../api/errors/index.js';
import type { AppError } from '../../api/errors/index.js';
import { useAppMutation } from '../../api/hooks.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { useIsAuthenticated } from '../auth/useSession.js';
import { TRANSCRIPT_MAX_PAGES, TRANSCRIPT_PAGE_SIZE, mergeSegmentDetail, parseTranscriptListItems, parseTranscriptSegment } from './types.js';
import type { TranscriptSegmentView } from './types.js';

/**
 * Transcript read + versioned mutation surface (Task 027).
 *
 * - `useTranscript(projectId)` is the single transcript query on
 *   `queryKeys.transcript.list(projectId)`. It pages `listSegments`
 *   (default 50/max 200, sort `startMs`) until `hasMore` is false (capped at
 *   10 pages) and hydrates texts via `getSegment` detail only when the list
 *   row carries no text (list summaries omit version texts by contract).
 *   The virtualized list slices this array via props and never fetches.
 * - `useTranscriptSegment` hydrates the Inspector's selected row (versions +
 *   provider/model metadata, no secrets).
 * - Mutations use the frozen bundle shapes (`expectedVersion` +
 *   `selectedVersionIds`/`text`) over POST only — never PUT/PATCH — and send
 *   `expectedVersion` on every call; 409 surfaces as `AppError`
 *   (`SELECTION_CONFLICT`) for the stale banner.
 */

async function fetchAllPages(projectId: string, signal?: AbortSignal): Promise<TranscriptSegmentView[]> {
  const merged: TranscriptSegmentView[] = [];
  let page = 1;
  for (let fetched = 0; fetched < TRANSCRIPT_MAX_PAGES; fetched += 1) {
    const response = await apiClient.listSegments(
      { path: { projectId }, query: { page, pageSize: TRANSCRIPT_PAGE_SIZE } },
      signal !== undefined ? { signal } : undefined,
    );
    const items = parseTranscriptListItems(response as unknown);
    merged.push(...items);
    const record = response as unknown as { hasMore?: boolean; total?: number };
    const hasMore = record.hasMore === true;
    if (!hasMore) {
      break;
    }
    page += 1;
  }
  const deduped = new Map<string, TranscriptSegmentView>();
  for (const segment of merged) {
    deduped.set(segment.id, segment);
  }
  const unique = [...deduped.values()].sort((a, b) => a.startMs - b.startMs || a.sequence - b.sequence);
  const hydrated: TranscriptSegmentView[] = [];
  for (const segment of unique) {
    if (segment.text !== '' || segment.versions.length > 0) {
      hydrated.push(segment);
      continue;
    }
    try {
      const detail = await apiClient.getSegment(
        { path: { projectId, segmentId: segment.id } },
        signal !== undefined ? { signal } : undefined,
      );
      hydrated.push(mergeSegmentDetail(segment, detail as unknown));
    } catch {
      hydrated.push(segment);
    }
    if (signal?.aborted === true) {
      break;
    }
  }
  return hydrated;
}

async function fetchTranscript(projectId: string, signal: AbortSignal | undefined): Promise<TranscriptSegmentView[]> {
  try {
    return await fetchAllPages(projectId, signal);
  } catch (error) {
    throw normalizeError(error, { method: 'GET' });
  }
}

export function useTranscript(projectId: string): UseQueryResult<readonly TranscriptSegmentView[], AppError> {
  const enabled = useIsAuthenticated() && projectId !== '';
  return useQuery<readonly TranscriptSegmentView[], AppError>({
    queryKey: queryKeys.transcript.list(projectId),
    queryFn: async ({ signal }): Promise<readonly TranscriptSegmentView[]> => fetchTranscript(projectId, signal),
    enabled,
  });
}

export function useTranscriptSegment(
  projectId: string,
  segmentId: string | undefined,
): UseQueryResult<TranscriptSegmentView | undefined, AppError> {
  const enabled = useIsAuthenticated() && projectId !== '' && segmentId !== undefined && segmentId !== '';
  return useQuery<TranscriptSegmentView | undefined, AppError>({
    queryKey: queryKeys.transcript.detail(projectId, segmentId ?? ''),
    queryFn: async (): Promise<TranscriptSegmentView | undefined> => {
      try {
        const detail = await apiClient.getSegment({ path: { projectId, segmentId: segmentId ?? '' } });
        return parseTranscriptSegment(detail as unknown);
      } catch (error) {
        throw normalizeError(error, { method: 'GET' });
      }
    },
    enabled,
  });
}

export interface SelectTranscriptVersionVariables {
  readonly segmentId: string;
  readonly versionId: string;
  readonly expectedVersion: number;
}

export interface ManualTranscriptVersionVariables {
  readonly segmentId: string;
  readonly text: string;
  readonly expectedVersion: number;
}

/**
 * Select-version mutation (POST `transcript-selection` with
 * `expectedVersion`). Success invalidates `queryKeys.transcript`; 409 is
 * returned normalized for the stale banner (no silent overwrite, no retry).
 */
export function useSelectTranscriptVersion(
  projectId: string,
): UseMutationResult<{ selectionVersion: number }, AppError, SelectTranscriptVersionVariables> {
  return useAppMutation({
    mutationFn: async (variables, ctx) => {
      try {
        const response = await apiClient.selectTranscriptVersion(
          { path: { projectId, segmentId: variables.segmentId } },
          { expectedVersion: variables.expectedVersion, selectedVersionIds: [variables.versionId] },
          { idempotencyKey: ctx.idempotencyKey },
        );
        const selectionVersion =
          typeof (response as { selectionVersion?: unknown }).selectionVersion === 'number'
            ? ((response as { selectionVersion?: number }).selectionVersion ?? variables.expectedVersion)
            : variables.expectedVersion;
        return { selectionVersion };
      } catch (error) {
        throw normalizeError(error, { method: 'POST' });
      }
    },
  }) as UseMutationResult<{ selectionVersion: number }, AppError, SelectTranscriptVersionVariables>;
}

/**
 * Create-manual-version mutation (POST `transcript-edits` with
 * `expectedVersion` + edited text). New versions only — never mutates
 * history. Success invalidates `queryKeys.transcript`; failure rolls back
 * the optimistic draft in the Inspector.
 */
export function useCreateManualTranscriptVersion(
  projectId: string,
): UseMutationResult<{ selectionVersion: number }, AppError, ManualTranscriptVersionVariables> {
  return useAppMutation({
    mutationFn: async (variables, ctx) => {
      try {
        const response = await apiClient.editTranscript(
          { path: { projectId, segmentId: variables.segmentId } },
          { expectedVersion: variables.expectedVersion, text: variables.text },
          { idempotencyKey: ctx.idempotencyKey },
        );
        const selectionVersion =
          typeof (response as { selectionVersion?: unknown }).selectionVersion === 'number'
            ? ((response as { selectionVersion?: number }).selectionVersion ?? variables.expectedVersion)
            : variables.expectedVersion;
        return { selectionVersion };
      } catch (error) {
        throw normalizeError(error, { method: 'POST' });
      }
    },
  }) as UseMutationResult<{ selectionVersion: number }, AppError, ManualTranscriptVersionVariables>;
}

/** Invalidates the transcript list + one detail row after a version write. */
export async function invalidateTranscript(
  queryClient: QueryClient,
  projectId: string,
  segmentId?: string,
): Promise<void> {
  await queryClient.invalidateQueries({ queryKey: queryKeys.transcript.list(projectId) });
  if (segmentId !== undefined && segmentId !== '') {
    await queryClient.invalidateQueries({ queryKey: queryKeys.transcript.detail(projectId, segmentId) });
  }
}
