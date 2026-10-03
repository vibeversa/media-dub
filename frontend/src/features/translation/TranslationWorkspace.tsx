import { useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import { Alert } from '../../components/Alert/Alert.js';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Modal } from '../../components/Modal/Modal.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { useToast } from '../../components/Toast/useToast.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { TranslationEditor } from './TranslationEditor.js';
import { formatTimestamp, isTranslationConflict, syncToneFor, windowDurationMs } from './types.js';
import type { TranslationSegmentView } from './types.js';
import { invalidateTranslations, useCreateManualTranslationVersion, useSelectTranslationVersion, useTranslations } from './useTranslations.js';
import { useDirtyGuard } from './useDirtyGuard.js';
import { useTranslation } from 'react-i18next';

export interface TranslationWorkspaceProps {
  readonly projectId: string;
}

/**
 * Translation review workspace (Task 028).
 *
 * Side-by-side layout per selected segment: read-only source (currently-
 * selected transcript version with version label) | selected translation +
 * controlled draft editor | immutable alternatives. Header per segment shows
 * speaker, time window, duration, sync status, glossary hits, and assigned
 * voice (missing data renders as `—` with an explanatory tooltip, never
 * blank). All writes are versioned POSTs with `expectedVersion` (409 →
 * stale banner, refetch, draft preserved, never silent overwrite, never
 * auto-resubmit). Candidates stay immutable snapshots — selection or
 * manual-version only, no inline edit affordance on candidates. Dirty drafts
 * gate every segment/route/tab leave via `useDirtyGuard`
 * (save/discard/cancel).
 */
export function TranslationWorkspace({ projectId }: TranslationWorkspaceProps): ReactNode {
    const { t } = useTranslation();
const { push } = useToast();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const translationsQuery = useTranslations(projectId);
  const selectMutation = useSelectTranslationVersion(projectId);
  const manualMutation = useCreateManualTranslationVersion(projectId);

  const [selectedId, setSelectedId] = useState<string | undefined>(undefined);
  const [draft, setDraft] = useState('');
  const [staleMessage, setStaleMessage] = useState<string | null>(null);
  const [sourceNotice, setSourceNotice] = useState<{ previous: string; current: string } | null>(null);
  const baselineSourceRef = useRef<string | undefined>(undefined);

  const segments = useMemo(() => translationsQuery.data ?? [], [translationsQuery.data]);

  useEffect(() => {
    if (segments.length > 0 && selectedId === undefined) {
      setSelectedId(segments[0]?.id);
    }
  }, [segments, selectedId]);

  useEffect(() => {
    if (segments.length === 0 || selectedId === undefined) {
      return;
    }
    const stillExists = segments.some((segment) => segment.id === selectedId);
    if (!stillExists) {
      const fallback = segments[0]?.id;
      setSelectedId(fallback);
      setDraft('');
      setSourceNotice(null);
      baselineSourceRef.current = undefined;
      if (fallback !== undefined) {
        push('error', 'Segment was removed. Selection moved to the nearest surviving segment.');
      }
    }
  }, [segments, selectedId, push]);

  const selected: TranslationSegmentView | undefined =
    selectedId !== undefined ? segments.find((s) => s.id === selectedId) : undefined;

  const isDirty = draft !== '';

  // Captures the source baseline when a draft starts; a mid-edit source
  // version change raises the rebase notice (dirty guard + rebase option).
  useEffect(() => {
    if (!isDirty || selected === undefined) {
      if (!isDirty) {
        baselineSourceRef.current = undefined;
        // Keep an existing notice only while dirty; clear on clean.
        if (sourceNotice !== null && draft === '') {
          setSourceNotice(null);
        }
      }
      return;
    }
    if (baselineSourceRef.current === undefined) {
      baselineSourceRef.current = selected.sourceVersionId;
      return;
    }
    if (selected.sourceVersionId !== baselineSourceRef.current) {
      setSourceNotice({
        previous: baselineSourceRef.current ?? 'unknown',
        current: selected.sourceVersionId ?? 'unknown',
      });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isDirty, selected?.sourceVersionId]);

  // Browser-tab guard: dirty drafts warn on unload (route/tab guard covers
  // in-app navigation via `requestLeave`).
  useEffect(() => {
    if (!isDirty) {
      return;
    }
    const handler = (event: BeforeUnloadEvent): void => {
      event.preventDefault();
    };
    window.addEventListener('beforeunload', handler);
    return () => {
      window.removeEventListener('beforeunload', handler);
    };
  }, [isDirty]);

  async function handleManualSave(): Promise<boolean> {
    if (selected === undefined) {
      return false;
    }
    const text = draft.trim();
    if (text === '') {
      push('error', 'Manual text cannot be empty.');
      return false;
    }
    const previousDraft = draft;
    try {
      await manualMutation.mutateAsync({
        segmentId: selected.id,
        text,
        expectedVersion: selected.selectionVersion,
      });
      await invalidateTranslations(queryClient, projectId, selected.id);
      setDraft('');
      baselineSourceRef.current = undefined;
      setSourceNotice(null);
      push('success', 'Manual translation created.');
      return true;
    } catch (error) {
      const appError = error as { code?: string; status?: number; message?: string };
      if (isTranslationConflict(appError)) {
        setStaleMessage(
          `Segment changed elsewhere (v${String(selected.selectionVersion)}). Refresh to load the current version. Your draft was kept.`,
        );
        await invalidateTranslations(queryClient, projectId, selected.id);
        setDraft(previousDraft);
        return false;
      }
      setDraft(previousDraft);
      push('error', appError.message ?? t('translation:translationWorkspace.manual-translation-failed-draft-kept'));
      return false;
    }
  }

  async function handleSelect(versionId: string): Promise<void> {
    if (selected === undefined) {
      return;
    }
    try {
      await selectMutation.mutateAsync({
        segmentId: selected.id,
        versionId,
        expectedVersion: selected.selectionVersion,
      });
      await invalidateTranslations(queryClient, projectId, selected.id);
      push('success', 'Translation selected.');
    } catch (error) {
      const appError = error as { code?: string; status?: number; message?: string };
      if (isTranslationConflict(appError)) {
        setStaleMessage(
          `Segment changed elsewhere (v${String(selected.selectionVersion)}). Refresh to load the current version.`,
        );
        await invalidateTranslations(queryClient, projectId, selected.id);
        return;
      }
      push('error', appError.message ?? t('translation:translationWorkspace.translation-selection-failed'));
    }
  }

  const guard = useDirtyGuard({
    isDirty,
    onSave: handleManualSave,
    onDiscard: () => {
      setDraft('');
      baselineSourceRef.current = undefined;
      setSourceNotice(null);
    },
  });

  function handleRequestSegment(nextId: string): void {
    if (nextId === selectedId) {
      return;
    }
    guard.requestLeave(
      () => {
        setSelectedId(nextId);
        setDraft('');
        baselineSourceRef.current = undefined;
        setSourceNotice(null);
      },
      `segment ${nextId}`,
    );
  }

  function handleLeaveProject(): void {
    guard.requestLeave(
      () => {
        navigate(`/projects/${projectId}`);
      },
      'leave',
    );
  }

  async function handleStaleRefresh(): Promise<void> {
    setStaleMessage(null);
    await invalidateTranslations(queryClient, projectId, selectedId);
  }

  function handleRebase(): void {
    if (selected !== undefined) {
      baselineSourceRef.current = selected.sourceVersionId;
    }
    setSourceNotice(null);
    push('success', 'Draft rebased onto the current source version.');
  }

  if (translationsQuery.isPending && segments.length === 0) {
    return (
      <section data-testid="translation-workspace" aria-label={t('translation:translationWorkspace.translation-workspace')}>
        <div data-testid="translation-loading">
          <Skeleton lines={8} />
        </div>
      </section>
    );
  }

  if (translationsQuery.isError) {
    return (
      <section data-testid="translation-workspace" aria-label={t('translation:translationWorkspace.translation-workspace2')}>
        <div data-testid="translation-error">
          <ErrorState
            title={t('translation:translationWorkspace.translation-unavailable')}
            message={translationsQuery.error?.message ?? t('translation:translationWorkspace.the-translation-could-not-be-loaded')}
            correlationId={translationsQuery.error?.correlationId}
            onRetry={() => {
              void translationsQuery.refetch();
            }}
          />
        </div>
      </section>
    );
  }

  if (segments.length === 0) {
    return (
      <section data-testid="translation-workspace" aria-label={t('translation:translationWorkspace.translation-workspace3')}>
        <div data-testid="translation-empty">
          <EmptyState
            title={t('translation:translationWorkspace.no-translations-yet')}
            description={t('translation:translationWorkspace.start-processing-to-generate-translations-review')}
          />
          <Link data-testid="translation-empty-progress-link" to={`/projects/${projectId}`}>
            {t('translation:translationWorkspace.go-to-processing')}
          </Link>
        </div>
      </section>
    );
  }

  const durationMs = selected !== undefined ? windowDurationMs(selected) : 0;
  const syncTone = selected !== undefined ? syncToneFor(selected, selected.selectedText, durationMs) : 'unknown';

  return (
    <section data-testid="translation-workspace" aria-label={t('translation:translationWorkspace.translation-workspace4')}>
      {staleMessage !== null ? (
        <div data-testid="translation-stale-banner">
          <Alert tone="warning" title={t('translation:translationWorkspace.segment-changed-elsewhere')}>
            <p>{staleMessage}</p>
            <button
              type="button"
              data-testid="translation-stale-refresh"
              className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
              onClick={() => {
                void handleStaleRefresh();
              }}
            >
              {t('translation:translationWorkspace.refresh-translations')}
            </button>
          </Alert>
        </div>
      ) : null}
      {sourceNotice !== null ? (
        <div data-testid="translation-source-changed">
          <Alert tone="info" title={t('translation:translationWorkspace.source-transcript-changed')}>
            <p data-testid="translation-source-changed-text">
              {`The source transcript moved from ${sourceNotice.previous} to ${sourceNotice.current} while your draft was open.`}
            </p>
            <button
              type="button"
              data-testid="translation-rebase"
              className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
              onClick={handleRebase}
            >
              {t('translation:translationWorkspace.rebase-onto-current-source')}
            </button>
          </Alert>
        </div>
      ) : null}
      <div style={{ display: 'flex', gap: '1rem', alignItems: 'flex-start' }}>
        <div style={{ flex: '0 0 300px' }}>
          <div
            data-testid="translation-list-scroll"
            role="listbox"
            aria-label={t('translation:translationWorkspace.translation-segments')}
            tabIndex={0}
            onKeyDown={(event) => {
              if (segments.length === 0) {
                return;
              }
              const currentIndex = selectedId !== undefined ? segments.findIndex((s) => s.id === selectedId) : -1;
              if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
                event.preventDefault();
                const next =
                  event.key === 'ArrowDown'
                    ? segments[Math.min(segments.length - 1, currentIndex + 1)]
                    : segments[Math.max(0, currentIndex <= 0 ? 0 : currentIndex - 1)];
                if (next !== undefined) {
                  handleRequestSegment(next.id);
                }
              }
            }}
            style={{ overflowY: 'auto', maxHeight: '640px' }}
          >
            <div data-testid="translation-list" data-total={String(segments.length)} data-rendered={String(segments.length)}>
              {segments.map((segment) => {
                const isSelected = segment.id === selectedId;
                return (
                  <div
                    key={segment.id}
                    role="option"
                    aria-selected={isSelected}
                    data-testid={`translation-row-${segment.id}`}
                    data-selected={isSelected ? 'true' : 'false'}
                    data-segment-id={segment.id}
                    style={{
                      display: 'flex',
                      gap: '0.5rem',
                      alignItems: 'flex-start',
                      padding: '0.5rem 0.75rem',
                      borderInlineStart: isSelected ? '3px solid var(--color-brand)' : '3px solid transparent',
                    }}
                  >
                    <button
                      type="button"
                      data-testid={`translation-select-${segment.id}`}
                      className="dp-focus-ring"
                      style={{ flexGrow: 1, textAlign: 'start', background: 'none', border: 'none', padding: 0, cursor: 'pointer' }}
                      onClick={() => {
                        handleRequestSegment(segment.id);
                      }}
                      aria-label={t('translation:translationWorkspace.select-segment', { v0: segment.id })}
                    >
                      <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'baseline', flexWrap: 'wrap' }}>
                        <span data-testid={`translation-time-${segment.id}`} className="dp-muted">
                          {formatTimestamp(segment.startMs)}
                        </span>
                        <span data-testid={`translation-speaker-${segment.id}`}>{segment.speakerLabel}</span>
                      </div>
                      <p data-testid={`translation-text-${segment.id}`} style={{ margin: '0.25rem 0' }}>
                        {segment.selectedText === '' ? '(no translation yet)' : segment.selectedText}
                      </p>
                      <div style={{ display: 'flex', gap: '0.35rem', flexWrap: 'wrap' }}>
                        <span data-testid={`translation-badge-selected-${segment.id}`}>
                          {segment.selectedVersionId !== undefined ? `selected ${segment.selectedVersionId.slice(0, 8)}` : 'selected'}
                        </span>
                        {segment.manualVersionId !== undefined ? (
                          <span data-testid={`translation-badge-manual-${segment.id}`}>{t('translation:translationWorkspace.manual')}</span>
                        ) : null}
                      </div>
                    </button>
                  </div>
                );
              })}
            </div>
          </div>
          <button
            type="button"
            data-testid="translation-back-link"
            className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
            onClick={handleLeaveProject}
          >
            {t('translation:translationWorkspace.back-to-project')}
          </button>
        </div>
        <div data-testid="translation-detail" style={{ flexGrow: 1, minWidth: 0 }}>
          {selected === undefined ? (
            <p data-testid="translation-detail-empty">{t('translation:translationWorkspace.select-a-segment-to-review-its')}</p>
          ) : (
            <>
              <div data-testid="translation-header" data-segment-id={selected.id}>
                <span data-testid="translation-speaker" title={t('translation:translationWorkspace.speaker-for-this-segment')}>
                  {selected.speakerLabel}
                </span>{' '}
                <span
                  data-testid="translation-time"
                  title={t('translation:translationWorkspace.window', { v0: formatTimestamp(selected.startMs), v1: formatTimestamp(selected.endMs) })}
                >
                  {`${formatTimestamp(selected.startMs)} → ${formatTimestamp(selected.endMs)}`}
                </span>{' '}
                <span data-testid="translation-duration" title={t('translation:translationWorkspace.dub-window-duration-read-only-timing')}>
                  {`${String(durationMs)} ms`}
                </span>{' '}
                <span
                  data-testid="translation-sync"
                  title={
                    selected.syncStatus !== undefined
                      ? `Sync status: ${selected.syncStatus}.`
                      : 'Sync status unavailable for this segment.'
                  }
                >
                  {selected.syncStatus ?? '—'}
                  {syncTone !== 'unknown' ? ` (${syncTone})` : ''}
                </span>{' '}
                <span
                  data-testid="translation-glossary"
                  title={
                    selected.glossaryHits.length > 0
                      ? selected.glossaryHits
                          .map((hit) => (hit.definition !== undefined ? `${hit.term}: ${hit.definition}` : hit.term))
                          .join('; ')
                      : 'No glossary matches for this segment.'
                  }
                >
                  {selected.glossaryHits.length > 0
                    ? selected.glossaryHits.map((hit) => hit.term).join(', ')
                    : '—'}
                </span>{' '}
                {selected.assignedVoice !== undefined ? (
                  <Link
                    data-testid="translation-voice"
                    to={`/projects/${projectId}/voices`}
                    title={t('translation:translationWorkspace.assigned-voice-open-voice-assignment', { v0: selected.assignedVoice.label })}
                  >
                    {selected.assignedVoice.label}
                  </Link>
                ) : (
                  <span data-testid="translation-voice" title={t('translation:translationWorkspace.no-voice-assigned-yet-assign-in')}>
                    —
                  </span>
                )}
              </div>
              <div style={{ display: 'flex', gap: '1rem', alignItems: 'flex-start' }}>
                <div data-testid="translation-side-source" style={{ flex: 1, minWidth: 0 }}>
                  <h3>{t('translation:translationWorkspace.source')}</h3>
                  <p data-testid="translation-source">{selected.sourceText === '' ? '(empty source)' : selected.sourceText}</p>
                  <span data-testid="translation-source-version" title={t('translation:translationWorkspace.currently-selected-transcript-version')}>
                    {selected.sourceVersionLabel}
                  </span>
                </div>
                <div data-testid="translation-side-selected" style={{ flex: 1, minWidth: 0 }}>
                  <h3>{t('translation:translationWorkspace.selected-translation')}</h3>
                  <p data-testid="translation-selected">
                    {selected.selectedText === '' ? '(no translation yet)' : selected.selectedText}
                  </p>
                  <TranslationEditor
                    projectId={projectId}
                    segment={selected}
                    draft={draft}
                    onDraftChange={setDraft}
                    onSave={() => {
                      void handleManualSave();
                    }}
                    isSaving={manualMutation.isPending}
                  />
                </div>
                <div data-testid="translation-side-alternatives" style={{ flex: 1, minWidth: 0 }}>
                  <h3>{t('translation:translationWorkspace.alternatives')}</h3>
                  {selected.versions.length === 0 ? (
                    <div data-testid="translation-no-candidates">
                      <EmptyState
                        title={t('translation:translationWorkspace.translation-pending')}
                        description={t('translation:translationWorkspace.no-candidates-yet-for-this-segment')}
                      />
                      <Link data-testid="translation-no-candidates-progress-link" to={`/projects/${projectId}`}>
                        {t('translation:translationWorkspace.go-to-processing2')}
                      </Link>
                    </div>
                  ) : (
                    <ul data-testid="translation-candidate-list">
                      {selected.versions.map((version) => (
                        <li
                          key={version.id}
                          data-testid={`translation-candidate-${version.id}`}
                          data-selected={version.id === selected.selectedVersionId ? 'true' : 'false'}
                        >
                          <p data-testid={`translation-candidate-text-${version.id}`}>
                            {version.text === '' ? '(empty)' : version.text}
                          </p>
                          <span data-testid={`translation-candidate-meta-${version.id}`}>
                            {`${version.provider}/${version.model} · v${String(version.versionNumber)}${version.isManual ? ' · manual' : ''}${version.isSelected ? ' · selected' : ''}`}
                          </span>
                          {version.score !== undefined ? (
                            <span data-testid={`translation-candidate-score-${version.id}`}>{`score ${String(version.score)}`}</span>
                          ) : null}
                          {version.id === selected.selectedVersionId ? null : (
                            <button
                              type="button"
                              data-testid={`translation-select-version-${version.id}`}
                              className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
                              disabled={selectMutation.isPending}
                              onClick={() => {
                                void handleSelect(version.id);
                              }}
                            >
                              {t('translation:translationWorkspace.select-this-translation')}
                            </button>
                          )}
                        </li>
                      ))}
                    </ul>
                  )}
                </div>
              </div>
            </>
          )}
        </div>
      </div>
      {/*
        Task 041C: the unsaved-draft guard is a modal interruption - it blocks
        the navigation the user just asked for and demands a decision - but it
        was an inline `role="dialog"` panel with no focus trap, no Escape and no
        focus restore. It is the `Modal` primitive now. The testid stays on the
        inner content so the existing 028 suites keep addressing it.
      */}
      {guard.dialogOpen ? (
        <Modal open title={t('translation:translationWorkspace.unsaved-translation-draft')} onClose={guard.confirmCancel}>
          <div data-testid="translation-dirty-dialog">
          <Alert tone="warning" title={t('translation:translationWorkspace.unsaved-translation-draft2')}>
            <p data-testid="translation-dirty-text">
              {guard.pendingLabel !== undefined
                ? `You have an unsaved draft (navigating to ${guard.pendingLabel}). Save it as a manual version, discard it, or stay.`
                : 'You have an unsaved draft. Save it as a manual version, discard it, or stay.'}
            </p>
            <div style={{ display: 'flex', gap: '0.5rem' }}>
              <button
                type="button"
                data-testid="translation-dirty-save"
                className="dp-btn dp-btn-primary dp-btn-md dp-focus-ring"
                disabled={manualMutation.isPending}
                onClick={() => {
                  void guard.confirmSave();
                }}
              >
                {t('translation:translationWorkspace.save-draft')}
              </button>
              <button
                type="button"
                data-testid="translation-dirty-discard"
                className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
                onClick={guard.confirmDiscard}
              >
                {t('translation:translationWorkspace.discard-draft')}
              </button>
              <button
                type="button"
                data-testid="translation-dirty-cancel"
                className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
                onClick={guard.confirmCancel}
              >
                {t('translation:translationWorkspace.stay')}
              </button>
            </div>
          </Alert>
          </div>
        </Modal>
      ) : null}
      <div hidden>
        <span data-testid="translation-query-key">{JSON.stringify(queryKeys.translations.list(projectId))}</span>
      </div>
    </section>
  );
}
