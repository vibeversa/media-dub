import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { formatAppearance } from './types.js';
import { useSpeakers } from './useVoices.js';

export interface SpeakerListProps {
  readonly projectId: string;
  readonly selectedSpeakerId?: string;
  readonly onSelect?: (speakerId: string) => void;
}

/**
 * Speaker list (Task 029).
 *
 * Per-speaker row: segment count, appearance window, current voice plus
 * provider/type chips, and a consent badge. Fed by `useSpeakers` on the
 * speakers list factory entry. Zero-segment speakers render `0 segments`
 * (assignment stays allowed; the impact dialog notes no affected segments).
 * A 404 from the speaker list (project without diarization) renders an
 * `EmptyState` with a pipeline link. Missing voice data renders as an em
 * dash with an explanatory tooltip, never blank. All text is plain text.
 */
export function SpeakerList({ projectId, selectedSpeakerId, onSelect }: SpeakerListProps): ReactNode {
  const speakersQuery = useSpeakers(projectId);

  if (speakersQuery.isPending) {
    return (
      <section data-testid="voices-speaker-list" aria-label="Speaker list">
        <div data-testid="voices-loading">
          <Skeleton lines={6} />
        </div>
      </section>
    );
  }

  if (speakersQuery.isError) {
    const error = speakersQuery.error;
    const isMissing = error?.code === 'NOT_FOUND' || error?.status === 404;
    if (isMissing) {
      return (
        <section data-testid="voices-speaker-list" aria-label="Speaker list">
          <div data-testid="voices-empty">
            <EmptyState
              title="No speakers yet"
              description="Speaker diarization has not produced speakers for this project. Start processing to populate the cast."
            />
            <Link data-testid="voices-empty-pipeline-link" to={`/projects/${projectId}`}>
              Go to processing
            </Link>
          </div>
        </section>
      );
    }
    return (
      <section data-testid="voices-speaker-list" aria-label="Speaker list">
        <div data-testid="voices-error">
          <ErrorState
            title="Speakers unavailable"
            message={error?.message ?? 'The speaker list could not be loaded. No data was changed.'}
            correlationId={error?.correlationId}
            onRetry={() => {
              void speakersQuery.refetch();
            }}
          />
        </div>
      </section>
    );
  }

  const speakers = [...(speakersQuery.data ?? [])];

  if (speakers.length === 0) {
    return (
      <section data-testid="voices-speaker-list" aria-label="Speaker list">
        <div data-testid="voices-empty">
          <EmptyState
            title="No speakers yet"
            description="Speaker diarization has not produced speakers for this project. Start processing to populate the cast."
          />
          <Link data-testid="voices-empty-pipeline-link" to={`/projects/${projectId}`}>
            Go to processing
          </Link>
        </div>
      </section>
    );
  }

  return (
    <section data-testid="voices-speaker-list" aria-label="Speaker list">
      <div
        data-testid="voices-list-scroll"
        role="listbox"
        aria-label="Speakers"
        tabIndex={0}
        style={{ overflowY: 'auto', maxHeight: '640px' }}
      >
        <div data-testid="voices-list" data-total={String(speakers.length)} data-rendered={String(speakers.length)}>
          {speakers.map((speaker) => {
            const isSelected = speaker.id === selectedSpeakerId;
            const appearance = formatAppearance(speaker.firstAppearanceMs, speaker.lastAppearanceMs);
            const hasAppearance = appearance !== '—';
            return (
              <div
                key={speaker.id}
                role="option"
                aria-selected={isSelected}
                data-testid={`voices-row-${speaker.id}`}
                data-selected={isSelected ? 'true' : 'false'}
                data-segment-id={speaker.id}
                style={{
                  display: 'flex',
                  gap: '0.5rem',
                  alignItems: 'flex-start',
                  padding: '0.5rem 0.75rem',
                  borderLeft: isSelected ? '3px solid var(--color-brand)' : '3px solid transparent',
                }}
              >
                <button
                  type="button"
                  data-testid={`voices-select-${speaker.id}`}
                  className="dp-focus-ring"
                  style={{ flexGrow: 1, textAlign: 'left', background: 'none', border: 'none', padding: 0, cursor: 'pointer' }}
                  onClick={() => {
                    onSelect?.(speaker.id);
                  }}
                  aria-label={`Select speaker ${speaker.displayName}`}
                >
                  <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'baseline', flexWrap: 'wrap' }}>
                    <span data-testid={`voices-name-${speaker.id}`}>{speaker.displayName}</span>
                    <span data-testid={`voices-key-${speaker.id}`} className="dp-muted" title="Diarization speaker key.">
                      {speaker.speakerKey}
                    </span>
                  </div>
                  <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'baseline', flexWrap: 'wrap' }}>
                    <span
                      data-testid={`voices-segments-${speaker.id}`}
                      title={
                        speaker.segmentCount === 0
                          ? 'This speaker has no segments yet. Assignment is allowed; the impact dialog will note no affected segments.'
                          : `${String(speaker.segmentCount)} segments use this speaker.`
                      }
                    >
                      {`${String(speaker.segmentCount)} segments`}
                    </span>
                    <span
                      data-testid={`voices-appearance-${speaker.id}`}
                      title={
                        hasAppearance
                          ? `First to last appearance ${appearance}.`
                          : 'Appearance window unavailable for this speaker.'
                      }
                    >
                      {appearance}
                    </span>
                  </div>
                  <div style={{ display: 'flex', gap: '0.35rem', flexWrap: 'wrap', marginTop: '0.25rem' }}>
                    {speaker.assignedVoice !== undefined ? (
                      <>
                        <span
                          data-testid={`voices-voice-${speaker.id}`}
                          title={`Assigned voice ${speaker.assignedVoice.voiceId}.`}
                        >
                          {speaker.assignedVoice.voiceId}
                        </span>
                        <span
                          data-testid={`voices-provider-${speaker.id}`}
                          title={`Voice provider ${speaker.assignedVoice.provider}.`}
                        >
                          {speaker.assignedVoice.provider}
                        </span>
                        <span
                          data-testid={`voices-type-${speaker.id}`}
                          title={`Voice type ${speaker.assignedVoice.voiceType}.`}
                        >
                          {speaker.assignedVoice.voiceType}
                        </span>
                      </>
                    ) : (
                      <span data-testid={`voices-voice-${speaker.id}`} title="No voice assigned yet.">
                        —
                      </span>
                    )}
                    <span
                      data-testid={`voices-consent-${speaker.id}`}
                      title={
                        speaker.assignedVoice === undefined
                          ? 'No voice assigned — no consent needed yet.'
                          : speaker.assignedVoice.voiceType.toLowerCase() === 'cloned'
                            ? 'Cloned voice — consent is enforced server-side on assignment and preview.'
                            : 'Stock voice — no consent needed.'
                      }
                    >
                      {speaker.assignedVoice === undefined
                        ? 'unassigned'
                        : speaker.assignedVoice.voiceType.toLowerCase() === 'cloned'
                          ? 'consent-gated'
                          : 'valid'}
                    </span>
                  </div>
                </button>
              </div>
            );
          })}
        </div>
      </div>
    </section>
  );
}
