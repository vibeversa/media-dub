import { useQuery } from '@tanstack/react-query';
import type { QueryClient, UseQueryResult } from '@tanstack/react-query';
import { apiClient, apiFetch } from '../../api/client/index.js';
import { normalizeError } from '../../api/errors/index.js';
import type { AppError } from '../../api/errors/index.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { useIsAuthenticated } from '../auth/useSession.js';
import { PEAKS_ENDPOINT_TOKEN, parseWaveformPeaks } from './types.js';
import type { WaveformPeaksView } from './types.js';

export interface PreviewMediaView {
  readonly url: string;
  readonly expiresAt: string | undefined;
}

/**
 * Timeline media data layer (Task 030).
 *
 * - Playback URLs come from the signed output descriptor
 *   (`GET /projects/{id}/output/download` via the generated client). The URL
 *   lives in the query cache + component state only — never logged, never
 *   embedded in error reports.
 * - Visualization peaks come exclusively from the `WaveformPeaks` lane
 *   (`GET /projects/{id}/media/waveform-peaks`). `fetchWaveformPeaks` is the
 *   single choke point the waveform uses; it never requests archival
 *   originals. Peak samples are clamped/validated before canvas draw.
 */

function previewMediaPath(projectId: string): string {
  return `/projects/${projectId}/output/download`;
}

function peaksApiPath(projectId: string, resolution?: number): string {
  const encoded = encodeURIComponent(projectId);
  if (resolution !== undefined && Number.isFinite(resolution) && resolution > 0) {
    return `/projects/${encoded}/media/${PEAKS_ENDPOINT_TOKEN}?resolution=${String(Math.round(resolution))}`;
  }
  return `/projects/${encoded}/media/${PEAKS_ENDPOINT_TOKEN}`;
}

/** Fetches the signed playback descriptor for `<video>`/`<audio>` only. */
export async function fetchPreviewMedia(projectId: string): Promise<PreviewMediaView> {
  try {
    const response = await apiClient.downloadOutput({ path: { projectId } });
    const record = response as unknown as Record<string, unknown>;
    const url = typeof record['downloadUrl'] === 'string' ? (record['downloadUrl'] as string) : '';
    if (url === '') {
      throw new Error('Output download returned no signed URL.');
    }
    const expiresAt = typeof record['expiresAt'] === 'string' ? (record['expiresAt'] as string) : undefined;
    void previewMediaPath(projectId);
    return { url, expiresAt };
  } catch (error) {
    throw normalizeError(error, { method: 'GET' });
  }
}

/**
 * Fetches `WaveformPeaks` for visualization. This is the only visualization
 * fetch the waveform performs — archival media is never requested here.
 */
export async function fetchWaveformPeaks(projectId: string, resolution?: number): Promise<WaveformPeaksView> {
  try {
    const payload = await apiFetch<unknown>(peaksApiPath(projectId, resolution), { method: 'GET' });
    return parseWaveformPeaks(payload);
  } catch (error) {
    throw normalizeError(error, { method: 'GET' });
  }
}

export function usePreviewMedia(projectId: string): UseQueryResult<PreviewMediaView, AppError> {
  const enabled = useIsAuthenticated() && projectId !== '';
  return useQuery<PreviewMediaView, AppError>({
    queryKey: queryKeys.timeline.media(projectId),
    queryFn: async (): Promise<PreviewMediaView> => fetchPreviewMedia(projectId),
    enabled,
  });
}

export function useWaveformPeaks(
  projectId: string,
  resolution?: number,
): UseQueryResult<WaveformPeaksView, AppError> {
  const enabled = useIsAuthenticated() && projectId !== '';
  return useQuery<WaveformPeaksView, AppError>({
    queryKey: queryKeys.timeline.peaks(projectId, resolution),
    queryFn: async (): Promise<WaveformPeaksView> => fetchWaveformPeaks(projectId, resolution),
    enabled,
  });
}

/** Invalidates timeline media + peaks after an expiry refetch. */
export async function invalidateTimelineMedia(queryClient: QueryClient, projectId: string): Promise<void> {
  await queryClient.invalidateQueries({ queryKey: queryKeys.timeline.media(projectId) });
  await queryClient.invalidateQueries({ queryKey: queryKeys.timeline.peaks(projectId) });
}
