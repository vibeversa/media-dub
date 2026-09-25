import { useState } from 'react';
import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import { Alert } from '../../components/Alert/Alert.js';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { useToast } from '../../components/Toast/useToast.js';
import { ImpactDialog } from './ImpactDialog.js';
import type { ImpactMode } from './ImpactDialog.js';
import { PreviewPlayer } from './PreviewPlayer.js';
import { defaultVoiceFor, isAssignmentConflict, isConsentError, isVoicesNotFound, isVoiceAssignable } from './types.js';
import type { SpeakerView, VoiceOptionView } from './types.js';
import { invalidateSpeakers, useAssignSpeakerVoice, useAvailableVoices } from './useVoices.js';

export interface VoiceSelectorProps {
  readonly projectId: string;
  readonly speaker: SpeakerView;
  readonly onAssigned?: (voiceId: string) => void;
}

/**
 * Compatible-voice selector (Task 029, R1/R2/R3).
 *
 * Renders only the backend compatibility response (`voices[]`) — excluded
 * voices are never rendered (not disabled-rendered, not rendered). Every
 * assign/replace/reset flows through `ImpactDialog` (R2); cancel never
 * mutates. Non-`valid` consent states disable assignment and show the
 * backend tenant-policy message verbatim (R3). A 403/409 on assignment
 * clears the pending selection, shows a banner, and refetches both scopes.
 * Reset assigns the backend default (`isDefault`, else the first compatible)
 * behind the same confirm.
 */
export function VoiceSelector({ projectId, speaker, onAssigned }: VoiceSelectorProps): ReactNode {
  const { push } = useToast();
  const queryClient = useQueryClient();
  const voicesQuery = useAvailableVoices(projectId, speaker.id);
  const assignMutation = useAssignSpeakerVoice(projectId);

  const [pendingVoice, setPendingVoice] = useState<VoiceOptionView | undefined>(undefined);
  const [impactMode, setImpactMode] = useState<ImpactMode>('assign');
  const [assignMessage, setAssignMessage] = useState<string | null>(null);
  const [assignCorrelationId, setAssignCorrelationId] = useState<string | undefined>(undefined);

  function openImpact(voice: VoiceOptionView, mode: ImpactMode): void {
    setPendingVoice(voice);
    setImpactMode(mode);
    setAssignMessage(null);
    setAssignCorrelationId(undefined);
  }

  function closeImpact(): void {
    setPendingVoice(undefined);
  }

  function modeFor(voice: VoiceOptionView): ImpactMode {
    if (speaker.assignedVoice === undefined) {
      return 'assign';
    }
    if (speaker.assignedVoice.voiceId === voice.voiceId || speaker.assignedVoice.voiceProfileId === voice.voiceProfileId) {
      return 'assign';
    }
    return 'replace';
  }

  async function handleConfirm(): Promise<void> {
    if (pendingVoice === undefined) {
      return;
    }
    const target = pendingVoice;
    try {
      const result = await assignMutation.mutateAsync({
        speakerId: speaker.id,
        voiceId: target.voiceProfileId,
        reason: undefined,
      });
      await invalidateSpeakers(queryClient, projectId, speaker.id);
      setPendingVoice(undefined);
      setAssignMessage(null);
      setAssignCorrelationId(undefined);
      if (result.warningCode !== undefined) {
        push('success', `Voice assigned with warning ${result.warningCode}.`);
      } else {
        push('success', 'Voice assigned.');
      }
      onAssigned?.(result.voiceId);
    } catch (error) {
      const appError = error as { code?: string; status?: number; message?: string; correlationId?: string };
      const isConsent = isConsentError(appError);
      const isConflict = isAssignmentConflict(appError);
      if (isConsent || isConflict) {
        setAssignMessage(appError.message ?? 'Voice assignment was rejected. The list was refreshed.');
        setAssignCorrelationId(appError.correlationId);
        setPendingVoice(undefined);
        await invalidateSpeakers(queryClient, projectId, speaker.id);
        return;
      }
      setAssignMessage(appError.message ?? 'Voice assignment failed. No data was changed.');
      setAssignCorrelationId(appError.correlationId);
    }
  }

  async function handleAssignRefresh(): Promise<void> {
    setAssignMessage(null);
    setAssignCorrelationId(undefined);
    await invalidateSpeakers(queryClient, projectId, speaker.id);
  }

  function handleReset(): void {
    const voices = voicesQuery.data?.voices ?? [];
    const fallback = defaultVoiceFor(voices);
    if (fallback === undefined) {
      return;
    }
    openImpact(fallback, 'reset');
  }

  if (voicesQuery.isPending) {
    return (
      <section data-testid="voices-selector" aria-label="Voice selector">
        <div data-testid="voices-voices-loading">
          <Skeleton lines={4} />
        </div>
      </section>
    );
  }

  if (voicesQuery.isError) {
    const error = voicesQuery.error;
    if (isVoicesNotFound(error)) {
      return (
        <section data-testid="voices-selector" aria-label="Voice selector">
          <div data-testid="voices-no-voices">
            <EmptyState
              title="No compatible voices"
              description="Voice compatibility is unavailable for this project. Run diarization and processing to populate compatible voices."
            />
            <Link data-testid="voices-no-voices-pipeline-link" to={`/projects/${projectId}`}>
              Go to processing
            </Link>
          </div>
        </section>
      );
    }
    return (
      <section data-testid="voices-selector" aria-label="Voice selector">
        <div data-testid="voices-voices-error">
          <ErrorState
            title="Voices unavailable"
            message={error?.message ?? 'Compatible voices could not be loaded. No data was changed.'}
            correlationId={error?.correlationId}
            onRetry={() => {
              void voicesQuery.refetch();
            }}
          />
        </div>
      </section>
    );
  }

  const available = voicesQuery.data;
  const voices = available?.voices ?? [];

  if (voices.length === 0) {
    return (
      <section data-testid="voices-selector" aria-label="Voice selector">
        <div data-testid="voices-no-voices">
          <EmptyState
            title="No compatible voices"
            description="No compatible voices were reported for this speaker. Adjust project language or voice inventory, then refresh."
          />
          <Link data-testid="voices-no-voices-pipeline-link" to={`/projects/${projectId}`}>
            Go to processing
          </Link>
        </div>
      </section>
    );
  }

  const currentVoiceId = speaker.assignedVoice?.voiceId;
  const resetDefault = defaultVoiceFor(voices);

  return (
    <section data-testid="voices-selector" aria-label="Voice selector">
      {assignMessage !== null ? (
        <div data-testid="voices-assign-banner">
          <Alert
            tone="warning"
            title="Voice assignment rejected"
            details={assignCorrelationId !== undefined && assignCorrelationId !== '' ? `Ref: ${assignCorrelationId}` : undefined}
          >
            <p data-testid="voices-assign-message">{assignMessage}</p>
            <button
              type="button"
              data-testid="voices-assign-refresh"
              className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
              onClick={() => {
                void handleAssignRefresh();
              }}
            >
              Refresh voices
            </button>
          </Alert>
        </div>
      ) : null}
      <div data-testid="voices-current">
        <span className="dp-muted">Current voice: </span>
        {speaker.assignedVoice !== undefined ? (
          <span data-testid="voices-current-voice" title={`Assigned voice ${speaker.assignedVoice.voiceId}.`}>
            {speaker.assignedVoice.voiceId}
          </span>
        ) : (
          <span data-testid="voices-current-voice" title="No voice assigned yet.">
            —
          </span>
        )}
        <span data-testid="voices-current-segments" title="Segments affected by a voice change." className="dp-muted">
          {` · ${String(speaker.segmentCount)} segments`}
        </span>
      </div>
      <ul data-testid="voices-voice-list">
        {voices.map((voice) => {
          const assignable = isVoiceAssignable(voice);
          const isCurrent =
            currentVoiceId !== undefined &&
            (voice.voiceId === currentVoiceId || voice.voiceProfileId === currentVoiceId);
          return (
            <li
              key={voice.voiceProfileId}
              data-testid={`voices-option-${voice.voiceId}`}
              data-assignable={assignable ? 'true' : 'false'}
              data-current={isCurrent ? 'true' : 'false'}
            >
              <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'baseline', flexWrap: 'wrap' }}>
                <span data-testid={`voices-option-label-${voice.voiceId}`}>{voice.label}</span>
                <span data-testid={`voices-option-provider-${voice.voiceId}`} title={`Provider ${voice.provider}.`}>
                  {voice.provider}
                </span>
                <span data-testid={`voices-option-type-${voice.voiceId}`} title={`Type ${voice.voiceType}.`}>
                  {voice.voiceType}
                </span>
                <span
                  data-testid={`voices-option-consent-${voice.voiceId}`}
                  title={
                    voice.policyMessage !== undefined
                      ? voice.policyMessage
                      : `Consent state ${voice.consentState}.`
                  }
                >
                  {voice.consentState}
                </span>
              </div>
              {voice.policyMessage !== undefined ? (
                <p data-testid={`voices-option-policy-${voice.voiceId}`}>{voice.policyMessage}</p>
              ) : null}
              {isCurrent ? (
                <span data-testid={`voices-option-current-${voice.voiceId}`}>Current voice</span>
              ) : (
                <button
                  type="button"
                  data-testid={`voices-select-voice-${voice.voiceId}`}
                  className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
                  disabled={!assignable || assignMutation.isPending}
                  title={
                    assignable
                      ? `Assign voice ${voice.label}.`
                      : (voice.policyMessage ?? `Voice ${voice.label} requires consent before assignment.`)
                  }
                  onClick={() => {
                    openImpact(voice, modeFor(voice));
                  }}
                >
                  {speaker.assignedVoice === undefined ? 'Assign voice' : 'Replace voice'}
                </button>
              )}
              <PreviewPlayer projectId={projectId} speakerId={speaker.id} voiceId={voice.voiceId} />
            </li>
          );
        })}
      </ul>
      <div style={{ marginTop: '0.75rem' }}>
        <button
          type="button"
          data-testid="voices-reset"
          className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
          disabled={speaker.assignedVoice === undefined || resetDefault === undefined || assignMutation.isPending}
          title={
            speaker.assignedVoice === undefined
              ? 'No custom voice to reset.'
              : resetDefault === undefined
                ? 'No compatible default voice available.'
                : `Reset to backend default ${resetDefault.label}.`
          }
          onClick={handleReset}
        >
          Reset to default voice
        </button>
      </div>
      {pendingVoice !== undefined ? (
        <ImpactDialog
          open
          speakerLabel={speaker.displayName}
          segmentCount={speaker.segmentCount}
          voiceLabel={pendingVoice.label}
          mode={impactMode}
          costNote={pendingVoice.costNote}
          isPending={assignMutation.isPending}
          onConfirm={() => {
            void handleConfirm();
          }}
          onCancel={closeImpact}
        />
      ) : null}
    </section>
  );
}
