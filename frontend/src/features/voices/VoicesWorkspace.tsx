import { useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import { queryKeys } from '../../api/queryKeys/index.js';
import { SpeakerList } from './SpeakerList.js';
import { VoiceSelector } from './VoiceSelector.js';
import { useSpeakers } from './useVoices.js';

export interface VoicesWorkspaceProps {
  readonly projectId: string;
}

/**
 * Voice assignment workspace (Task 029).
 *
 * Speaker list plus compatible-voice assignment for the selected speaker
 * with signed-URL previews. The list auto-selects the first speaker; an
 * empty cast renders the list empty state with a pipeline link (no detail
 * pane). Assignment invalidates the speakers and translations scopes so
 * voice chips refresh; all failures surface through the structured error
 * path as banners (no ad-hoc toasts for errors).
 */
export function VoicesWorkspace({ projectId }: VoicesWorkspaceProps): ReactNode {
  const speakersQuery = useSpeakers(projectId);
  const [selectedId, setSelectedId] = useState<string | undefined>(undefined);

  const speakers = useMemo(() => speakersQuery.data ?? [], [speakersQuery.data]);

  useEffect(() => {
    if (speakers.length > 0 && selectedId === undefined) {
      setSelectedId(speakers[0]?.id);
    }
  }, [speakers, selectedId]);

  useEffect(() => {
    if (speakers.length === 0 || selectedId === undefined) {
      return;
    }
    const stillExists = speakers.some((speaker) => speaker.id === selectedId);
    if (!stillExists) {
      setSelectedId(speakers[0]?.id);
    }
  }, [speakers, selectedId]);

  const selected = selectedId !== undefined ? speakers.find((speaker) => speaker.id === selectedId) : undefined;

  return (
    <section data-testid="voices-workspace" aria-label="Voice assignment workspace">
      <div style={{ display: 'flex', gap: '1rem', alignItems: 'flex-start' }}>
        <div style={{ flex: '0 0 320px' }}>
          <SpeakerList projectId={projectId} selectedSpeakerId={selectedId} onSelect={setSelectedId} />
        </div>
        <div data-testid="voices-detail" style={{ flexGrow: 1, minWidth: 0 }}>
          {selected === undefined ? (
            <p data-testid="voices-detail-empty">Select a speaker to review compatible voices.</p>
          ) : (
            <VoiceSelector
              projectId={projectId}
              speaker={selected}
              onAssigned={() => {
                // Assignment invalidates via the selector; selection stays.
              }}
            />
          )}
        </div>
      </div>
      <div hidden>
        <span data-testid="voices-query-key">{JSON.stringify(queryKeys.speakers.list(projectId))}</span>
      </div>
    </section>
  );
}
