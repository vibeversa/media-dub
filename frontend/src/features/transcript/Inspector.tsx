import { useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useToast } from '../../components/Toast/useToast.js';
import { deriveLineage, isSelectionConflict } from './types.js';
import type { TranscriptSegmentView } from './types.js';
import { invalidateTranscript, useCreateManualTranscriptVersion, useSelectTranscriptVersion, useTranscriptSegment } from './useTranscript.js';

export interface InspectorProps {
  readonly projectId: string;
  readonly segment: TranscriptSegmentView | undefined;
  readonly onStale: (message: string) => void;
  readonly staleVersion: number | undefined;
}

/**
 * Transcript inspector (Task 027).
 *
 * Shows the selected segment's original text, currently-selected version
 * text, a manual draft editor (optimistic draft, error rollback, success
 * invalidates `queryKeys.transcript`), and an advanced disclosure with
 * provider/model/version per version (metadata only — never secrets). Version
 * history lists every version with a select-version action
 * (`POST transcript-selection` with `expectedVersion`); 409 surfaces the
 * stale banner via `onStale` and preserves the draft (never auto-resubmits).
 */
export function Inspector({ projectId, segment, onStale, staleVersion }: InspectorProps): ReactNode {
  const { push } = useToast();
  const queryClient = useQueryClient();
  const [draft, setDraft] = useState('');
  const [draftDirty, setDraftDirty] = useState(false);
  const [advancedOpen, setAdvancedOpen] = useState(false);
  const detailQuery = useTranscriptSegment(projectId, segment?.id);
  const selectMutation = useSelectTranscriptVersion(projectId);
  const manualMutation = useCreateManualTranscriptVersion(projectId);

  const full: TranscriptSegmentView | undefined = detailQuery.data ?? segment;
  const lineage = full !== undefined ? deriveLineage(full) : undefined;

  useEffect(() => {
    setDraft('');
    setDraftDirty(false);
    setAdvancedOpen(false);
  }, [segment?.id]);

  if (segment === undefined || full === undefined) {
    return (
      <aside data-testid="transcript-inspector" aria-label="Segment inspector">
        <p data-testid="transcript-inspector-empty">Select a segment to inspect versions.</p>
      </aside>
    );
  }

  const current: TranscriptSegmentView = full;

  async function handleSelect(versionId: string): Promise<void> {
    try {
      await selectMutation.mutateAsync({
        segmentId: current.id,
        versionId,
        expectedVersion: current.selectionVersion,
      });
      await invalidateTranscript(queryClient, projectId, current.id);
      push('success', 'Version selected.');
    } catch (error) {
      const appError = error as { code?: string; status?: number; message?: string };
      if (isSelectionConflict(appError)) {
        onStale(`Segment changed elsewhere (v${String(staleVersion ?? current.selectionVersion)}). Refresh to load the current version.`);
        await invalidateTranscript(queryClient, projectId, current.id);
        return;
      }
      push('error', appError.message ?? 'Version selection failed.');
    }
  }

  async function handleManualSave(): Promise<void> {
    const text = draft.trim();
    if (text === '') {
      push('error', 'Manual text cannot be empty.');
      return;
    }
    const previousDraft = draft;
    try {
      await manualMutation.mutateAsync({ segmentId: current.id, text, expectedVersion: current.selectionVersion });
      await invalidateTranscript(queryClient, projectId, current.id);
      setDraft('');
      setDraftDirty(false);
      push('success', 'Manual version created.');
    } catch (error) {
      const appError = error as { code?: string; status?: number; message?: string };
      if (isSelectionConflict(appError)) {
        onStale('Segment changed elsewhere. Your draft was kept — refresh, then reapply it.');
        await invalidateTranscript(queryClient, projectId, current.id);
        setDraft(previousDraft);
        setDraftDirty(true);
        return;
      }
      setDraft(previousDraft);
      setDraftDirty(true);
      push('error', appError.message ?? 'Manual version failed; draft kept.');
    }
  }

  return (
    <aside data-testid="transcript-inspector" aria-label="Segment inspector">
      <h3 data-testid="transcript-inspector-title">Segment {current.id}</h3>
      <section aria-label="Original text">
        <h4>Original</h4>
        <p data-testid="transcript-original-text">{lineage?.originalText === '' ? '(empty)' : (lineage?.originalText ?? '')}</p>
        <span data-testid="transcript-badge-original-inspector">original</span>
      </section>
      <section aria-label="Selected version">
        <h4>Selected</h4>
        <p data-testid="transcript-selected-text">{lineage?.selectedText === '' ? '(empty)' : (lineage?.selectedText ?? '')}</p>
        <span data-testid="transcript-badge-selected-inspector">{lineage?.selectedBadge ?? 'selected'}</span>
        {lineage?.hasManual === true ? <span data-testid="transcript-badge-manual-inspector">manual</span> : null}
      </section>
      <section aria-label="Manual edit">
        <h4>Manual draft</h4>
        <label htmlFor="transcript-manual-draft">Manual text</label>
        <textarea
          id="transcript-manual-draft"
          data-testid="transcript-manual-draft"
          value={draft}
          rows={4}
          onChange={(event) => {
            setDraft(event.target.value);
            setDraftDirty(true);
          }}
        />
        {draftDirty ? <p data-testid="transcript-draft-dirty">Unsaved draft</p> : null}
        <button
          type="button"
          data-testid="transcript-manual-save"
          className="dp-btn dp-btn-primary dp-btn-md dp-focus-ring"
          disabled={manualMutation.isPending || draft.trim() === ''}
          onClick={() => {
            void handleManualSave();
          }}
        >
          Save manual version
        </button>
        {manualMutation.isPending ? <p data-testid="transcript-manual-saving">Saving…</p> : null}
      </section>
      <section aria-label="Version history">
        <h4>Versions</h4>
        <button
          type="button"
          data-testid="transcript-advanced-toggle"
          aria-expanded={advancedOpen}
          className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
          onClick={() => {
            setAdvancedOpen((open) => !open);
          }}
        >
          {advancedOpen ? 'Hide details' : 'Show provider details'}
        </button>
        <ul data-testid="transcript-version-list">
          {current.versions.map((version) => (
            <li key={version.id} data-testid={`transcript-version-${version.id}`}>
              <p data-testid={`transcript-version-text-${version.id}`}>{version.text === '' ? '(empty)' : version.text}</p>
              <span data-testid={`transcript-version-badge-${version.id}`}>
                {version.isManual ? 'manual' : `v${String(version.versionNumber)}`}
                {version.isSelected ? ' · selected' : ''}
              </span>
              {advancedOpen ? (
                <dl data-testid={`transcript-version-meta-${version.id}`}>
                  <div>
                    <dt>Provider</dt>
                    <dd>{version.provider}</dd>
                  </div>
                  <div>
                    <dt>Model</dt>
                    <dd>{version.model}</dd>
                  </div>
                  <div>
                    <dt>Version</dt>
                    <dd>{`v${String(version.versionNumber)}`}</dd>
                  </div>
                </dl>
              ) : null}
              {version.isSelected ? null : (
                <button
                  type="button"
                  data-testid={`transcript-select-version-${version.id}`}
                  className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
                  disabled={selectMutation.isPending}
                  onClick={() => {
                    void handleSelect(version.id);
                  }}
                >
                  Select this version
                </button>
              )}
            </li>
          ))}
        </ul>
        {detailQuery.isFetching ? <p data-testid="transcript-detail-refreshing">Refreshing versions…</p> : null}
      </section>
    </aside>
  );
}
