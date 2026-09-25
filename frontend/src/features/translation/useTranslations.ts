import { useQuery } from '@tanstack/react-query';
import type { QueryClient, UseMutationResult, UseQueryResult } from '@tanstack/react-query';
import { apiClient } from '../../api/client/index.js';
import { normalizeError } from '../../api/errors/index.js';
import type { AppError } from '../../api/errors/index.js';
import { useAppMutation } from '../../api/hooks.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { useIsAuthenticated } from '../auth/useSession.js';
import { TRANSLATION_MAX_PAGES, TRANSLATION_PAGE_SIZE, mergeTranslationDetail, parseTranslationListItems, parseTranslationSegment } from './types.js';
import type { TranslationSegmentView } from './types.js';

/**
 * Translation read + versioned mutation surface (Task 028).
 *
 * - `useTranslations(projectId)` is the single translation query on
 *   `queryKeys.translations.list(projectId)`. It pages `listSegments`
 *   (default 50/max 200, sort `startMs`) until `hasMore` is false (capped at
 *   10 pages) and hydrates texts via `getSegment` detail only when the list
 *   row carries no translation candidates (list summaries omit version texts
 *   by contract).
 * - `useTranslationSegment` hydrates the selected row (candidates +
 *   provider/model metadata, no secrets).
 * - Mutations use the frozen bundle shapes (`expectedVersion` +
 *   `selectedVersionIds`/`text`) over POST only — never PUT/PATCH — and send
 *   `expectedVersion` on every call; 409 surfaces as `AppError`
 *   (`SELECTION_CONFLICT`) for the stale banner.
 */

async function fetchAllPages(projectId: string, signal?: AbortSignal): Promise<TranslationSegmentView[]> {
  const merged: TranslationSegmentView[] = [];
  let page = 1;
  for (let fetched = 0; fetched < TRANSLATION_MAX_PAGES; fetched += 1) {
    const response = await apiClient.listSegments(
      { path: { projectId }, query: { page, pageSize: TRANSLATION_PAGE_SIZE } },
      signal !== undefined ? { signal } : undefined,
    );
    const items = parseTranslationListItems(response as unknown);
    merged.push(...items);
    const record = response as unknown as { hasMore?: boolean };
    const hasMore = record.hasMore === true;
    if (!hasMore) {
      break;
    }
    page += 1;
  }
  const deduped = new Map<string, TranslationSegmentView>();
  for (const segment of merged) {
    deduped.set(segment.id, segment);
  }
  const unique = [...deduped.values()].sort((a, b) => a.startMs - b.startMs || a.sequence - b.sequence);
  const hydrated: TranslationSegmentView[] = [];
  for (const segment of unique) {
    if (segment.versions.length > 0) {
      hydrated.push(segment);
      continue;
    }
    try {
      const detail = await apiClient.getSegment(
        { path: { projectId, segmentId: segment.id } },
        signal !== undefined ? { signal } : undefined,
      );
      hydrated.push(mergeTranslationDetail(segment, detail as unknown));
    } catch {
      hydrated.push(segment);
    }
    if (signal?.aborted === true) {
      break;
    }
  }
  return hydrated;
}

async function fetchTranslations(projectId: string, signal: AbortSignal | undefined): Promise<TranslationSegmentView[]> {
  try {
    return await fetchAllPages(projectId, signal);
  } catch (error) {
    throw normalizeError(error, { method: 'GET' });
  }
}

export function useTranslations(projectId: string): UseQueryResult<readonly TranslationSegmentView[], AppError> {
  const enabled = useIsAuthenticated() && projectId !== '';
  return useQuery<readonly TranslationSegmentView[], AppError>({
    queryKey: queryKeys.translations.list(projectId),
    queryFn: async ({ signal }): Promise<readonly TranslationSegmentView[]> => fetchTranslations(projectId, signal),
    enabled,
  });
}

export function useTranslationSegment(
  projectId: string,
  segmentId: string | undefined,
): UseQueryResult<TranslationSegmentView | undefined, AppError> {
  const enabled = useIsAuthenticated() && projectId !== '' && segmentId !== undefined && segmentId !== '';
  return useQuery<TranslationSegmentView | undefined, AppError>({
    queryKey: queryKeys.translations.detail(projectId, segmentId ?? ''),
    queryFn: async (): Promise<TranslationSegmentView | undefined> => {
      try {
        const detail = await apiClient.getSegment({ path: { projectId, segmentId: segmentId ?? '' } });
        return parseTranslationSegment(detail as unknown);
      } catch (error) {
        throw normalizeError(error, { method: 'GET' });
      }
    },
    enabled,
  });
}

export interface SelectTranslationVersionVariables {
  readonly segmentId: string;
  readonly versionId: string;
  readonly expectedVersion: number;
}

export interface ManualTranslationVersionVariables {
  readonly segmentId: string;
  readonly text: string;
  readonly expectedVersion: number;
}

/**
 * Select-version mutation (POST `translation-selection` with
 * `expectedVersion`). Success invalidates `queryKeys.translations`; 409 is
 * returned normalized for the stale banner (no silent overwrite, no retry).
 */
export function useSelectTranslationVersion(
  projectId: string,
): UseMutationResult<{ selectionVersion: number }, AppError, SelectTranslationVersionVariables> {
  return useAppMutation({
    mutationFn: async (variables, ctx) => {
      try {
        const response = await apiClient.selectTranslationVersion(
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
  }) as UseMutationResult<{ selectionVersion: number }, AppError, SelectTranslationVersionVariables>;
}

/**
 * Create-manual-version mutation (POST `translation-edits` with
 * `expectedVersion` + edited text). New versions only — never mutates
 * history. Success invalidates `queryKeys.translations`; failure rolls back
 * the optimistic draft in the editor.
 */
export function useCreateManualTranslationVersion(
  projectId: string,
): UseMutationResult<{ selectionVersion: number }, AppError, ManualTranslationVersionVariables> {
  return useAppMutation({
    mutationFn: async (variables, ctx) => {
      try {
        const response = await apiClient.editTranslation(
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
  }) as UseMutationResult<{ selectionVersion: number }, AppError, ManualTranslationVersionVariables>;
}

/** Invalidates the translations list + one detail row after a version write. */
export async function invalidateTranslations(
  queryClient: QueryClient,
  projectId: string,
  segmentId?: string,
): Promise<void> {
  await queryClient.invalidateQueries({ queryKey: queryKeys.translations.list(projectId) });
  if (segmentId !== undefined && segmentId !== '') {
    await queryClient.invalidateQueries({ queryKey: queryKeys.translations.detail(projectId, segmentId) });
  }
}
