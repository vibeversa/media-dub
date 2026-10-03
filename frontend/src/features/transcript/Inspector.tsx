import { useState } from 'react';
import type { ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useToast } from '../../components/Toast/useToast.js';
import { deriveLineage, isSelectionConflict } from './types.js';
import type { TranscriptSegmentView } from './types.js';
import { invalidateTranscript, useCreateManualTranscriptVersion, useSelectTranscriptVersion, useTranscriptSegment } from './useTranscript.js';
import { useTranslation } from 'react-i18next';

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
    const { t } = useTranslation();
const { push } = useToast();
  const queryClient = useQueryClient();
  const [draft, setDraft] = useState('');
  const [draftDirty, setDraftDirty] = useState(false);
  const [advancedOpen, setAdvancedOpen] = useState(false);
  // Synchronous per-segment reset (render-phase adjustment, not an effect):
  // the draft must clear when the user selects a different segment, but an
  // async `useEffect(() => reset, [segment?.id])` races with user input —
  // under full-suite load the test/user can type between the commit that
  // mounts the new segment and the effect flush, and the late effect then
  // wipes the draft (flaky `select-version ... keeps the draft on 409`).
  // Adjusting state during render makes the reset commit atomically with the
  // segment change, so a draft typed after the render is never cleared by a
  // stale effect. The 409 paths (`handleSelect`/`handleManualSave`) keep the
  // id stable, so this never clears the preserved draft on conflict.
  const [draftSegmentId, setDraftSegmentId] = useState<string | undefined>(segment?.id);
  if (draftSegmentId !== segment?.id) {
    setDraftSegmentId(segment?.id);
    setDraft('');
    setDraftDirty(false);
    setAdvancedOpen(false);
  }
  const detailQuery = useTranscriptSegment(projectId, segment?.id);
  const selectMutation = useSelectTranscriptVersion(projectId);
  const manualMutation = useCreateManualTranscriptVersion(projectId);

  const full: TranscriptSegmentView | undefined = detailQuery.data ?? segment;
  const lineage = full !== undefined ? deriveLineage(full) : undefined;

  if (segment === undefined || full === undefined) {
    return (
      <aside data-testid="transcript-inspector" aria-label={t('transcript:inspector.segment-inspector')}>
        <p data-testid="transcript-inspector-empty">{t('transcript:inspector.select-a-segment-to-inspect-versions')}</p>
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
      push('error', appError.message ?? t('transcript:inspector.version-selection-failed'));
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
      push('error', appError.message ?? t('transcript:inspector.manual-version-failed-draft-kept'));
    }
  }

  return (
    <aside data-testid="transcript-inspector" aria-label={t('transcript:inspector.segment-inspector2')}>
      <h3 data-testid="transcript-inspector-title">{t('transcript:inspector.segment')} {current.id}</h3>
      <section aria-label={t('transcript:inspector.original-text')}>
        <h4>{t('transcript:inspector.original')}</h4>
        <p data-testid="transcript-original-text">{lineage?.originalText === '' ? '(empty)' : (lineage?.originalText ?? '')}</p>
        <span data-testid="transcript-badge-original-inspector">{t('transcript:inspector.original2')}</span>
      </section>
      <section aria-label={t('transcript:inspector.selected-version')}>
        <h4>{t('transcript:inspector.selected')}</h4>
        <p data-testid="transcript-selected-text">{lineage?.selectedText === '' ? '(empty)' : (lineage?.selectedText ?? '')}</p>
        <span data-testid="transcript-badge-selected-inspector">{lineage?.selectedBadge ?? 'selected'}</span>
        {lineage?.hasManual === true ? <span data-testid="transcript-badge-manual-inspector">{t('transcript:inspector.manual')}</span> : null}
      </section>
      <section aria-label={t('transcript:inspector.manual-edit')}>
        <h4>{t('transcript:inspector.manual-draft')}</h4>
        <label htmlFor="transcript-manual-draft">{t('transcript:inspector.manual-text')}</label>
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
        {draftDirty ? <p data-testid="transcript-draft-dirty">{t('transcript:inspector.unsaved-draft')}</p> : null}
        <button
          type="button"
          data-testid="transcript-manual-save"
          className="dp-btn dp-btn-primary dp-btn-md dp-focus-ring"
          disabled={manualMutation.isPending || draft.trim() === ''}
          onClick={() => {
            void handleManualSave();
          }}
        >
          {t('transcript:inspector.save-manual-version')}
        </button>
        {manualMutation.isPending ? <p data-testid="transcript-manual-saving">{t('transcript:inspector.saving')}</p> : null}
      </section>
      <section aria-label={t('transcript:inspector.version-history')}>
        <h4>{t('transcript:inspector.versions')}</h4>
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
                    <dt>{t('transcript:inspector.provider')}</dt>
                    <dd>{version.provider}</dd>
                  </div>
                  <div>
                    <dt>{t('transcript:inspector.model')}</dt>
                    <dd>{version.model}</dd>
                  </div>
                  <div>
                    <dt>{t('transcript:inspector.version')}</dt>
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
                  {t('transcript:inspector.select-this-version')}
                </button>
              )}
            </li>
          ))}
        </ul>
        {detailQuery.isFetching ? <p data-testid="transcript-detail-refreshing">{t('transcript:inspector.refreshing-versions')}</p> : null}
      </section>
    </aside>
  );
}
