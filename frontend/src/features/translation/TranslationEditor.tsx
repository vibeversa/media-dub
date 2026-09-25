import type { ReactNode } from 'react';
import { readingSpeedHint, splitGlossaryRuns, syncToneFor, windowDurationMs } from './types.js';
import type { TranslationSegmentView } from './types.js';

export interface TranslationEditorProps {
  readonly projectId: string;
  readonly segment: TranslationSegmentView | undefined;
  readonly draft: string;
  readonly onDraftChange: (text: string) => void;
  readonly onSave: () => void;
  readonly isSaving: boolean;
}

/**
 * Translation draft editor (Task 028).
 *
 * Controlled draft for the selected translation: glossary-term highlighting
 * (preview with `<mark>`, tooltip definitions where present), read-only
 * duration/sync indicator (over/under the dub window — timing lives in Task
 * 030, never edited here), and a character/reading-speed hint. Save creates
 * a manual version via the workspace handler; candidates are never mutated
 * in place (no edit affordance on candidates — see `TranslationWorkspace`).
 * All text renders as plain text (no raw HTML).
 */
export function TranslationEditor({
  projectId,
  segment,
  draft,
  onDraftChange,
  onSave,
  isSaving,
}: TranslationEditorProps): ReactNode {
  void projectId;
  if (segment === undefined) {
    return (
      <section data-testid="translation-editor" aria-label="Translation editor">
        <p data-testid="translation-editor-empty">Select a segment to edit its translation.</p>
      </section>
    );
  }

  const durationMs = windowDurationMs(segment);
  const effectiveText = draft !== '' ? draft : segment.selectedText;
  const hint = readingSpeedHint(effectiveText, durationMs);
  const tone = syncToneFor(segment, effectiveText, durationMs);
  const isDirty = draft !== '';
  const runs = splitGlossaryRuns(segment.selectedText, segment.glossaryHits);
  const draftRuns = draft !== '' ? splitGlossaryRuns(draft, segment.glossaryHits) : [];

  return (
    <section data-testid="translation-editor" aria-label="Translation editor">
      <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'baseline', flexWrap: 'wrap' }}>
        <span
          data-testid="translation-sync-indicator"
          title={tone === 'unknown' ? 'Sync status unavailable for this segment.' : `Sync ${tone} for the dub window.`}
        >
          {tone === 'unknown' ? '—' : tone}
        </span>
        <span data-testid="translation-duration-display" title="Dub window duration (read-only; timing lives in the timeline).">
          {`${String(durationMs)} ms window`}
        </span>
        <span data-testid="translation-reading-hint" title="Characters and reading speed over the dub window.">
          {hint}
        </span>
      </div>
      <div data-testid="translation-glossary-preview" aria-label="Glossary preview">
        {segment.glossaryHits.length === 0 ? (
          <span data-testid="translation-glossary-preview-empty" title="No glossary matches for this segment.">
            —
          </span>
        ) : (
          <p>
            {runs.map((run, index) =>
              run.term === undefined ? (
                <span key={index}>{run.text}</span>
              ) : run.term.definition !== undefined ? (
                <mark key={index} title={run.term.definition} data-testid={`translation-glossary-mark-${index}`}>
                  {run.text}
                </mark>
              ) : (
                <mark key={index} data-testid={`translation-glossary-mark-${index}`}>
                  {run.text}
                </mark>
              ),
            )}
          </p>
        )}
      </div>
      {draft !== '' ? (
        <div data-testid="translation-draft-preview" aria-label="Draft glossary preview">
          <p>
            {draftRuns.map((run, index) =>
              run.term === undefined ? (
                <span key={index}>{run.text}</span>
              ) : run.term.definition !== undefined ? (
                <mark key={index} title={run.term.definition} data-testid={`translation-draft-mark-${index}`}>
                  {run.text}
                </mark>
              ) : (
                <mark key={index} data-testid={`translation-draft-mark-${index}`}>
                  {run.text}
                </mark>
              ),
            )}
          </p>
        </div>
      ) : null}
      <label htmlFor="translation-draft">Translation draft</label>
      <textarea
        id="translation-draft"
        data-testid="translation-draft"
        value={draft}
        rows={4}
        placeholder={segment.selectedText !== '' ? segment.selectedText : 'Enter the manual translation…'}
        onChange={(event) => {
          onDraftChange(event.target.value);
        }}
      />
      {isDirty ? <p data-testid="translation-draft-dirty">Unsaved draft</p> : null}
      <button
        type="button"
        data-testid="translation-draft-save"
        className="dp-btn dp-btn-primary dp-btn-md dp-focus-ring"
        disabled={isSaving || draft.trim() === ''}
        onClick={onSave}
      >
        Save manual version
      </button>
      {isSaving ? <p data-testid="translation-editor-saving">Saving…</p> : null}
    </section>
  );
}
