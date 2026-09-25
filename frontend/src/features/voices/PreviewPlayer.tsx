import { useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { normalizeError } from '../../api/errors/index.js';
import type { AppError } from '../../api/errors/index.js';
import { Alert } from '../../components/Alert/Alert.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { PREVIEW_DEFAULT_TEXT, isConsentError, isExpiredError, isQuotaError } from './types.js';
import { fetchPreviewDetail, useRequestVoicePreview } from './useVoices.js';

export interface PreviewPlayerProps {
  readonly projectId: string;
  readonly speakerId: string;
  readonly voiceId: string;
  readonly previewText?: string;
}

type PreviewPhase = 'idle' | 'requesting' | 'ready' | 'pending' | 'quota' | 'consent' | 'expired' | 'error';

/**
 * Signed-URL preview playback (Task 029, R4/R5).
 *
 * Requests a preview (`POST voice-previews`), then fetches the detail for
 * the short-lived signed URL and plays it in an `<audio>` element with
 * `referrerPolicy="no-referrer"`. The URL lives in component state only —
 * never persisted, never logged, never placed in query params. Expiry
 * refetches the detail once with a resume prompt; quota (429) shows a
 * friendly retry-later message; consent (403) shows the backend
 * tenant-policy message verbatim.
 */
export function PreviewPlayer({ projectId, speakerId, voiceId, previewText }: PreviewPlayerProps): ReactNode {
  const requestMutation = useRequestVoicePreview(projectId);
  const [phase, setPhase] = useState<PreviewPhase>('idle');
  const [previewUrl, setPreviewUrl] = useState<string | undefined>(undefined);
  const [previewStatus, setPreviewStatus] = useState<string | undefined>(undefined);
  const [failure, setFailure] = useState<AppError | null>(null);
  const [resumePrompt, setResumePrompt] = useState(false);
  const [refetchedOnce, setRefetchedOnce] = useState(false);
  const [activePreviewId, setActivePreviewId] = useState<string | undefined>(undefined);
  const audioRef = useRef<HTMLAudioElement>(null);

  // Signed URLs never leak via Referer: enforce a no-referrer policy on the
  // media element imperatively (keeps the JSX typed across React versions).
  useEffect(() => {
    if (previewUrl !== undefined && audioRef.current !== null) {
      audioRef.current.setAttribute('referrerpolicy', 'no-referrer');
    }
  }, [previewUrl]);

  const text = previewText !== undefined && previewText !== '' ? previewText : PREVIEW_DEFAULT_TEXT;

  async function loadDetail(previewId: string, isRefetch: boolean): Promise<void> {
    try {
      const detail = await fetchPreviewDetail(projectId, previewId);
      setActivePreviewId(detail.previewId);
      setPreviewStatus(detail.status);
      if (detail.status === 'Completed' && detail.downloadUrl !== undefined) {
        setPreviewUrl(detail.downloadUrl);
        setPhase('ready');
        setFailure(null);
        if (isRefetch) {
          setResumePrompt(true);
        }
        return;
      }
      setPhase('pending');
      setFailure(null);
    } catch (error) {
      const appError = error as AppError;
      if (isExpiredError(appError) && !isRefetch && !refetchedOnce) {
        setRefetchedOnce(true);
        try {
          const retry = await fetchPreviewDetail(projectId, previewId);
          if (retry.status === 'Completed' && retry.downloadUrl !== undefined) {
            setPreviewUrl(retry.downloadUrl);
            setPreviewStatus(retry.status);
            setActivePreviewId(retry.previewId);
            setResumePrompt(true);
            setPhase('ready');
            setFailure(null);
            return;
          }
          setPhase('pending');
          return;
        } catch {
          setFailure(appError);
          setPhase('expired');
          return;
        }
      }
      if (isExpiredError(appError)) {
        setFailure(appError);
        setPhase('expired');
        return;
      }
      if (isQuotaError(appError)) {
        setFailure(appError);
        setPhase('quota');
        return;
      }
      if (isConsentError(appError)) {
        setFailure(appError);
        setPhase('consent');
        return;
      }
      setFailure(appError);
      setPhase('error');
    }
  }

  async function handleRequest(): Promise<void> {
    setResumePrompt(false);
    setRefetchedOnce(false);
    setPreviewUrl(undefined);
    setFailure(null);
    setPhase('requesting');
    try {
      const created = await requestMutation.mutateAsync({ speakerId, voiceId, text });
      if (created.previewId === '') {
        setFailure(normalizeError(new Error('Preview request returned no preview id.'), { method: 'POST' }));
        setPhase('error');
        return;
      }
      await loadDetail(created.previewId, false);
    } catch (error) {
      const appError = error as AppError;
      if (isQuotaError(appError)) {
        setFailure(appError);
        setPhase('quota');
        return;
      }
      if (isConsentError(appError)) {
        setFailure(appError);
        setPhase('consent');
        return;
      }
      if (isExpiredError(appError)) {
        setFailure(appError);
        setPhase('expired');
        return;
      }
      setFailure(appError);
      setPhase('error');
    }
  }

  async function handleAudioError(): Promise<void> {
    if (activePreviewId === undefined || refetchedOnce) {
      return;
    }
    setRefetchedOnce(true);
    await loadDetail(activePreviewId, true);
  }

  async function handleRetryDetail(): Promise<void> {
    if (activePreviewId === undefined) {
      return;
    }
    setPhase('requesting');
    await loadDetail(activePreviewId, true);
  }

  return (
    <section data-testid="voices-preview" aria-label="Voice preview">
      {phase === 'idle' ? (
        <button
          type="button"
          data-testid={`voices-preview-request-${voiceId}`}
          className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
          onClick={() => {
            void handleRequest();
          }}
        >
          Preview voice
        </button>
      ) : null}
      {phase === 'requesting' ? (
        <div data-testid="voices-preview-loading">
          <Skeleton lines={2} />
        </div>
      ) : null}
      {phase === 'ready' && previewUrl !== undefined ? (
        <div>
          <audio
            ref={audioRef}
            data-testid={`voices-preview-audio-${voiceId}`}
            src={previewUrl}
            controls
            preload="none"
            onError={() => {
              void handleAudioError();
            }}
          />
          <p data-testid="voices-preview-status" className="dp-muted" title="Preview synthesis status.">
            {previewStatus ?? 'Completed'}
          </p>
          {resumePrompt ? (
            <div data-testid="voices-preview-resume">
              <Alert tone="info" title="Preview link refreshed">
                <p>Preview link expired and was refetched — press play to resume.</p>
              </Alert>
            </div>
          ) : null}
          <button
            type="button"
            data-testid={`voices-preview-request-${voiceId}`}
            className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
            onClick={() => {
              void handleRequest();
            }}
          >
            Preview again
          </button>
        </div>
      ) : null}
      {phase === 'pending' ? (
        <div data-testid="voices-preview-pending">
          <Alert tone="info" title="Preview pending">
            <p>Preview synthesis has not completed yet. Retry to refresh its status.</p>
            <button
              type="button"
              data-testid="voices-preview-retry"
              className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
              onClick={() => {
                void handleRetryDetail();
              }}
            >
              Refresh preview status
            </button>
          </Alert>
        </div>
      ) : null}
      {phase === 'quota' ? (
        <div data-testid="voices-preview-quota">
          <Alert
            tone="warning"
            title="Preview quota exceeded"
            details={failure?.correlationId !== undefined && failure.correlationId !== '' ? `Ref: ${failure.correlationId}` : undefined}
          >
            <p>Preview quota exceeded. Try again later once the quota window resets.</p>
          </Alert>
        </div>
      ) : null}
      {phase === 'consent' ? (
        <div data-testid="voices-preview-consent">
          <Alert
            tone="error"
            title="Voice consent required"
            details={failure?.correlationId !== undefined && failure.correlationId !== '' ? `Ref: ${failure.correlationId}` : undefined}
          >
            <p>{failure?.message ?? 'Voice consent is required for this preview.'}</p>
          </Alert>
        </div>
      ) : null}
      {phase === 'expired' ? (
        <div data-testid="voices-preview-expired">
          <Alert
            tone="warning"
            title="Preview link expired"
            details={failure?.correlationId !== undefined && failure.correlationId !== '' ? `Ref: ${failure.correlationId}` : undefined}
          >
            <p>Preview link expired. Request a fresh preview to continue.</p>
            <button
              type="button"
              data-testid="voices-preview-retry"
              className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
              onClick={() => {
                void handleRequest();
              }}
            >
              Request fresh preview
            </button>
          </Alert>
        </div>
      ) : null}
      {phase === 'error' ? (
        <div data-testid="voices-preview-error">
          <Alert
            tone="error"
            title="Preview failed"
            details={
              failure?.correlationId !== undefined && failure.correlationId !== ''
                ? `${failure.message}\nRef: ${failure.correlationId}`
                : failure?.message
            }
          />
        </div>
      ) : null}
    </section>
  );
}
