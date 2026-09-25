import { useQuery } from '@tanstack/react-query';
import type { QueryClient, UseMutationResult, UseQueryResult } from '@tanstack/react-query';
import { apiClient } from '../../api/client/index.js';
import type { VoicePreviewRequest } from '../../api/client/index.js';
import { normalizeError } from '../../api/errors/index.js';
import type { AppError } from '../../api/errors/index.js';
import { useAppMutation } from '../../api/hooks.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { useIsAuthenticated } from '../auth/useSession.js';
import { SPEAKER_MAX_PAGES, SPEAKER_PAGE_SIZE, parseAvailableVoices, parseSpeaker, parseSpeakerListItems } from './types.js';
import type { AvailableVoicesView, SpeakerView } from './types.js';

/**
 * Speaker + voice read and mutation surface (Task 029).
 *
 * - `useSpeakers(projectId)` is the single speaker query on the speakers
 *   list factory entry. It pages `listSpeakers` (page size 100, capped at 10
 *   pages) until `hasMore` is false, dedupes by id, and sorts by speaker key.
 * - `useAvailableVoices(projectId, speakerId)` renders the backend
 *   compatibility response only (compatible `voices[]`); excluded voices are
 *   never selectable and never rendered (R1).
 * - Assignment uses the frozen bundle shape (`PUT voice-assignment` with
 *   `{ voiceId, reason? }`) over PUT only; preview creation uses the bundle
 *   `VoicePreviewRequest` (`{ voiceId, text }`) plus the backend-required
 *   `speakerId` additive field (bundle omits it, backend requires it for
 *   ownership checks — sent as an extra JSON property, never as a query
 *   param). Mutations send a per-attempt `Idempotency-Key` via
 *   `useAppMutation`.
 */

async function fetchAllSpeakers(projectId: string, signal?: AbortSignal): Promise<SpeakerView[]> {
  const merged: SpeakerView[] = [];
  let page = 1;
  for (let fetched = 0; fetched < SPEAKER_MAX_PAGES; fetched += 1) {
    const response = await apiClient.listSpeakers(
      { path: { projectId }, query: { page, pageSize: SPEAKER_PAGE_SIZE } },
      signal !== undefined ? { signal } : undefined,
    );
    merged.push(...parseSpeakerListItems(response as unknown));
    const record = response as unknown as { hasMore?: boolean };
    if (record.hasMore !== true) {
      break;
    }
    page += 1;
  }
  const deduped = new Map<string, SpeakerView>();
  for (const speaker of merged) {
    deduped.set(speaker.id, speaker);
  }
  return [...deduped.values()].sort((a, b) => a.speakerKey.localeCompare(b.speakerKey) || a.id.localeCompare(b.id));
}

async function fetchSpeakers(projectId: string, signal: AbortSignal | undefined): Promise<SpeakerView[]> {
  try {
    return await fetchAllSpeakers(projectId, signal);
  } catch (error) {
    throw normalizeError(error, { method: 'GET' });
  }
}

export function useSpeakers(projectId: string): UseQueryResult<readonly SpeakerView[], AppError> {
  const enabled = useIsAuthenticated() && projectId !== '';
  return useQuery<readonly SpeakerView[], AppError>({
    queryKey: queryKeys.speakers.list(projectId),
    queryFn: async ({ signal }): Promise<readonly SpeakerView[]> => fetchSpeakers(projectId, signal),
    enabled,
  });
}

export function useSpeaker(
  projectId: string,
  speakerId: string | undefined,
): UseQueryResult<SpeakerView | undefined, AppError> {
  const enabled = useIsAuthenticated() && projectId !== '' && speakerId !== undefined && speakerId !== '';
  return useQuery<SpeakerView | undefined, AppError>({
    queryKey: queryKeys.speakers.detail(projectId, speakerId ?? ''),
    queryFn: async (): Promise<SpeakerView | undefined> => {
      try {
        const detail = await apiClient.getSpeaker({ path: { projectId, speakerId: speakerId ?? '' } });
        return parseSpeaker(detail as unknown);
      } catch (error) {
        throw normalizeError(error, { method: 'GET' });
      }
    },
    enabled,
  });
}

async function fetchAvailableVoices(
  projectId: string,
  speakerId: string,
): Promise<AvailableVoicesView> {
  try {
    const response = await apiClient.listAvailableVoices({ path: { projectId, speakerId } });
    return parseAvailableVoices(response as unknown);
  } catch (error) {
    throw normalizeError(error, { method: 'GET' });
  }
}

export function useAvailableVoices(
  projectId: string,
  speakerId: string | undefined,
): UseQueryResult<AvailableVoicesView, AppError> {
  const enabled = useIsAuthenticated() && projectId !== '' && speakerId !== undefined && speakerId !== '';
  return useQuery<AvailableVoicesView, AppError>({
    queryKey: queryKeys.voices.available(projectId, speakerId ?? ''),
    queryFn: async (): Promise<AvailableVoicesView> => fetchAvailableVoices(projectId, speakerId ?? ''),
    enabled,
  });
}

export interface AssignVoiceVariables {
  readonly speakerId: string;
  readonly voiceId: string;
  readonly reason?: string;
}

export interface AssignVoiceResult {
  readonly changed: boolean;
  readonly outputStale: boolean;
  readonly voiceId: string;
  readonly warningCode: string | undefined;
  readonly unusedSpeaker: boolean;
}

/**
 * Voice-assignment mutation (PUT `voice-assignment` with
 * `{ voiceId, reason? }`). Success invalidates the speakers and translation
 * scopes via `invalidateSpeakers`; 403/409 surfaces normalized for the
 * revoked/conflict banner (selection cleared, refetch, never silent).
 */
export function useAssignSpeakerVoice(
  projectId: string,
): UseMutationResult<AssignVoiceResult, AppError, AssignVoiceVariables> {
  return useAppMutation({
    mutationFn: async (variables, ctx) => {
      try {
        const response = await apiClient.assignSpeakerVoice(
          { path: { projectId, speakerId: variables.speakerId } },
          variables.reason === undefined || variables.reason === ''
            ? { voiceId: variables.voiceId }
            : { voiceId: variables.voiceId, reason: variables.reason },
          { idempotencyKey: ctx.idempotencyKey },
        );
        const record = response as unknown as Record<string, unknown>;
        const changed = record['changed'] === true;
        const outputStale = record['outputStale'] === true;
        const voiceId =
          typeof record['voiceId'] === 'string' && record['voiceId'] !== ''
            ? (record['voiceId'] as string)
            : variables.voiceId;
        const warningCode =
          typeof record['warningCode'] === 'string' && record['warningCode'] !== ''
            ? (record['warningCode'] as string)
            : undefined;
        const unusedSpeaker = record['unusedSpeaker'] === true;
        return { changed, outputStale, voiceId, warningCode, unusedSpeaker };
      } catch (error) {
        throw normalizeError(error, { method: 'PUT' });
      }
    },
  }) as UseMutationResult<AssignVoiceResult, AppError, AssignVoiceVariables>;
}

export interface RequestPreviewVariables {
  readonly speakerId: string;
  readonly voiceId: string;
  readonly text: string;
}

export interface RequestPreviewResult {
  readonly previewId: string;
  readonly status: string;
  readonly isDuplicate: boolean;
}

/**
 * Voice-preview creation (POST `voice-previews`). The bundle declares
 * `{ voiceId, text }`; the backend additionally requires `speakerId` for
 * ownership checks, so the request carries it as an additive JSON property
 * (never a query param, never logged). Returns the `vpv_` preview id for
 * the signed-URL detail fetch.
 */
export function useRequestVoicePreview(
  projectId: string,
): UseMutationResult<RequestPreviewResult, AppError, RequestPreviewVariables> {
  return useAppMutation({
    mutationFn: async (variables, ctx) => {
      try {
        const body = {
          voiceId: variables.voiceId,
          text: variables.text,
          speakerId: variables.speakerId,
        } as unknown as VoicePreviewRequest;
        const response = await apiClient.requestVoicePreview(
          { path: { projectId } },
          body,
          { idempotencyKey: ctx.idempotencyKey },
        );
        const record = response as unknown as Record<string, unknown>;
        const previewId = typeof record['previewId'] === 'string' ? (record['previewId'] as string) : '';
        const status = typeof record['status'] === 'string' ? (record['status'] as string) : '';
        return { previewId, status, isDuplicate: record['isDuplicate'] === true };
      } catch (error) {
        throw normalizeError(error, { method: 'POST' });
      }
    },
  }) as UseMutationResult<RequestPreviewResult, AppError, RequestPreviewVariables>;
}

export interface PreviewDetail {
  readonly previewId: string;
  readonly status: string;
  readonly downloadUrl: string | undefined;
}

/** Fetches one preview detail (status + short-lived signed URL when Completed). */
export async function fetchPreviewDetail(projectId: string, previewId: string): Promise<PreviewDetail> {
  try {
    const response = await apiClient.getVoicePreview({ path: { projectId, previewId } });
    const record = response as unknown as Record<string, unknown>;
    const status = typeof record['status'] === 'string' ? (record['status'] as string) : '';
    const downloadUrl =
      typeof record['downloadUrl'] === 'string' && record['downloadUrl'] !== ''
        ? (record['downloadUrl'] as string)
        : undefined;
    const resolvedId = typeof record['previewId'] === 'string' ? (record['previewId'] as string) : previewId;
    return { previewId: resolvedId, status, downloadUrl };
  } catch (error) {
    throw normalizeError(error, { method: 'GET' });
  }
}

/**
 * Invalidates the speaker list + one speaker detail + its compatible-voice
 * entry after an assignment, plus the translations list (voice chips render
 * the assigned voice). Prefix scoping keeps project-detail invalidation
 * cascading to workspace/progress via the shared factory.
 */
export async function invalidateSpeakers(
  queryClient: QueryClient,
  projectId: string,
  speakerId?: string,
): Promise<void> {
  await queryClient.invalidateQueries({ queryKey: queryKeys.speakers.list(projectId) });
  if (speakerId !== undefined && speakerId !== '') {
    await queryClient.invalidateQueries({ queryKey: queryKeys.speakers.detail(projectId, speakerId) });
    await queryClient.invalidateQueries({ queryKey: queryKeys.voices.available(projectId, speakerId) });
  } else {
    await queryClient.invalidateQueries({ queryKey: queryKeys.voices.all(projectId) });
  }
  await queryClient.invalidateQueries({ queryKey: queryKeys.translations.list(projectId) });
}
