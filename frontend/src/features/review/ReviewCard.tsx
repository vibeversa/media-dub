import { useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import { Alert } from '../../components/Alert/Alert.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { MediaPlayer } from '../timeline/MediaPlayer.js';
import { useTimelinePlayerStore } from '../timeline/playerStore.js';
import {
  REVIEW_MAX_REASON_LENGTH,
  actorFromDetails,
  allowedTokenFor,
  currentVersionFromDetails,
  isAlreadyResolved,
  isDispositionAllowed,
  isReviewConflict,
  sortHistoryNewestFirst,
  validateEditText,
  validateReason,
} from './types.js';
import type { DispositionAction } from './types.js';
import { useDisposition } from './useDisposition.js';
import { useReviewContext } from './useReviewQueue.js';

export interface ReviewCardProps {
  readonly projectId: string;
  readonly reviewId: string;
  readonly onSettled?: (reviewId: string) => void;
}

/**
 * Single-screen review context + dispositions (Task 031).
 *
 * One card shows media excerpt (Task 030 compact player at the issue
 * timestamp), transcript segment, translation + candidates, voice assignment,
 * audio excerpt, sync/QC summary, provider/model/versions, gated actions, and
 * the chronological audit trail — without navigating away. Every disposition
 * sends `Idempotency-Key` + `expectedVersion` + reason; 409/412 shows the
 * stale banner, refetches, preserves reason text, and retries with the same
 * key. Disallowed actions are hidden (not disabled); the rationale stays
 * discoverable in history. Reasons render as plain text only.
 */
export function ReviewCard({ projectId, reviewId, onSettled }: ReviewCardProps): ReactNode {
  const contextQuery = useReviewContext(reviewId);
  const disposition = useDisposition({ projectId, reviewId });
  const [reason, setReason] = useState('');
  const [editText, setEditText] = useState('');
  const [staleBanner, setStaleBanner] = useState<string | null>(null);
  const [resolvedBy, setResolvedBy] = useState<string | null>(null);
  const [fieldError, setFieldError] = useState<string | null>(null);

  const context = contextQuery.data;

  const historyNewest = useMemo(
    () => (context === undefined ? [] : sortHistoryNewestFirst(context.history)),
    [context],
  );

  useEffect(() => {
    if (context?.segmentStartMs !== undefined) {
      useTimelinePlayerStore.getState().requestSeek(context.segmentStartMs);
    }
  }, [context?.segmentStartMs, reviewId]);

  useEffect(() => {
    setReason('');
    setEditText('');
    setStaleBanner(null);
    setResolvedBy(null);
    setFieldError(null);
  }, [reviewId]);

  if (contextQuery.isPending) {
    return (
      <section data-testid={`review-card-${reviewId}`} aria-label="Review detail">
        <div data-testid="review-card-loading">
          <Skeleton lines={8} />
        </div>
      </section>
    );
  }

  if (contextQuery.isError || context === undefined) {
    return (
      <section data-testid={`review-card-${reviewId}`} aria-label="Review detail">
        <div data-testid="review-card-error">
          <ErrorState
            title="Review unavailable"
            message={contextQuery.error?.message ?? 'This review could not be loaded. No data was changed.'}
            correlationId={contextQuery.error?.correlationId}
            onRetry={() => {
              void contextQuery.refetch();
            }}
          />
        </div>
      </section>
    );
  }

  const pending = disposition.isPending;
  const expectedVersion = context?.version ?? 0;

  function runDisposition(action: DispositionAction): void {
    const reasonCheck = validateReason(reason, action);
    if (!reasonCheck.valid) {
      setFieldError(reasonCheck.error ?? 'Check the reason and try again.');
      return;
    }
    if (action === 'resolve-with-edit') {
      const editCheck = validateEditText(editText);
      if (!editCheck.valid) {
        setFieldError(editCheck.error ?? 'Check the edit and try again.');
        return;
      }
    }
    setFieldError(null);
    setStaleBanner(null);
    disposition.mutate(
      { action, expectedVersion, reason, editText: action === 'resolve-with-edit' ? editText : undefined },
      {
        onSuccess: () => {
          onSettled?.(reviewId);
        },
        onError: (error) => {
          if (isAlreadyResolved(error)) {
            const actor = actorFromDetails(error.details);
            setResolvedBy(actor ?? 'another reviewer');
            setStaleBanner(`Already resolved by ${actor ?? 'another reviewer'}. Refreshed — this item leaves the active filter.`);
            return;
          }
          if (isReviewConflict(error)) {
            const current = currentVersionFromDetails(error.details);
            const actor = actorFromDetails(error.details);
            if (actor !== undefined) {
              setResolvedBy(actor);
            }
            setStaleBanner(
              current !== undefined
                ? `This review changed while you worked (now v${String(current)}). Reason text preserved — review and retry with the same key.`
                : 'This review changed while you worked. Reason text preserved — review and retry with the same key.',
            );
            return;
          }
          if (error.code === 'REVIEW_REASON_REQUIRED' || error.code === 'REVIEW_EDIT_EMPTY') {
            setFieldError(error.message);
            return;
          }
          setFieldError(error.message);
        },
      },
    );
  }

  const actionDefs: readonly { action: DispositionAction; testid: string; label: string }[] = [
    { action: 'approve', testid: `review-approve-${reviewId}`, label: 'Approve' },
    { action: 'reject', testid: `review-reject-${reviewId}`, label: 'Reject' },
    { action: 'requeue', testid: `review-requeue-${reviewId}`, label: 'Requeue' },
    { action: 'resolve-with-edit', testid: `review-resolve-edit-${reviewId}`, label: 'Resolve with edit' },
  ];

  return (
    <section data-testid={`review-card-${reviewId}`} aria-label="Review detail" data-status={context.status} data-version={context.version}>
      <header>
        <h3 data-testid={`review-card-title-${reviewId}`}>
          {context.type} · {context.severity} · {context.status} · v{String(context.version)}
        </h3>
        <p data-testid={`review-card-project-${reviewId}`} className="dp-muted">
          {context.projectName !== '' ? context.projectName : context.projectId} · run {context.runStatus}
        </p>
      </header>

      {staleBanner !== null ? (
        <div data-testid={`review-stale-${reviewId}`}>
          <Alert tone="warning" title="Review changed elsewhere" details={contextQuery.dataUpdatedAt ? undefined : undefined}>
            <p>{staleBanner}</p>
            <button
              type="button"
              data-testid={`review-refresh-${reviewId}`}
              className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
              onClick={() => {
                void contextQuery.refetch();
              }}
            >
              Refresh review
            </button>
          </Alert>
        </div>
      ) : null}

      {resolvedBy !== null ? (
        <p data-testid={`review-resolved-by-${reviewId}`} className="dp-muted">
          Resolved by {resolvedBy}
        </p>
      ) : null}

      <div data-testid={`review-media-${reviewId}`}>
        <h4>Media excerpt</h4>
        <MediaPlayer
          projectId={projectId}
          compact
          segments={
            context.segmentId !== undefined && context.segmentStartMs !== undefined && context.segmentEndMs !== undefined
              ? [{ id: context.segmentId, startMs: context.segmentStartMs, endMs: context.segmentEndMs }]
              : []
          }
        />
        {context.segmentStartMs !== undefined ? (
          <button
            type="button"
            data-testid={`review-jump-${reviewId}`}
            className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
            onClick={() => {
              useTimelinePlayerStore.getState().requestSeek(context.segmentStartMs ?? 0);
            }}
          >
            Jump to issue timestamp
          </button>
        ) : null}
      </div>

      <div data-testid={`review-transcript-${reviewId}`}>
        <h4>Transcript segment</h4>
        {context.segmentId !== undefined ? (
          <p className="dp-muted">
            Segment {context.segmentId}
            {context.segmentStartMs !== undefined && context.segmentEndMs !== undefined
              ? ` · ${String(context.segmentStartMs)}–${String(context.segmentEndMs)} ms`
              : ''}
            {context.segmentSpeakerId !== undefined ? ` · speaker ${context.segmentSpeakerId}` : ''}
          </p>
        ) : (
          <p className="dp-muted">Project-level review (no segment scope).</p>
        )}
        {context.transcript.length === 0 ? (
          <p className="dp-muted">No transcript versions.</p>
        ) : (
          <ul>
            {context.transcript.map((row) => (
              <li key={row.id} data-testid={`review-transcript-version-${row.id}`}>
                <p>{row.text}</p>
                <p data-testid={`review-transcript-provider-${row.id}`} className="dp-muted">
                  {row.provider}/{row.model}
                  {row.isSelected ? ' · selected' : ''}
                </p>
              </li>
            ))}
          </ul>
        )}
      </div>

      <div data-testid={`review-translation-${reviewId}`}>
        <h4>Translation + candidates</h4>
        {context.translation.length === 0 ? (
          <p className="dp-muted">No translation candidates.</p>
        ) : (
          <ul>
            {context.translation.map((row) => (
              <li key={row.id} data-testid={`review-translation-version-${row.id}`}>
                <p>{row.text}</p>
                <p data-testid={`review-translation-provider-${row.id}`} className="dp-muted">
                  {row.provider}/{row.model}
                  {row.isSelected ? ' · selected' : ''}
                </p>
              </li>
            ))}
          </ul>
        )}
        <p data-testid={`review-selection-version-${reviewId}`} className="dp-muted">
          Selection v{String(context.selectionVersion)}
        </p>
      </div>

      <div data-testid={`review-voice-${reviewId}`}>
        <h4>Voice assignment</h4>
        <p className="dp-muted">
          Speaker {context.voiceSpeakerId ?? '—'} · voice {context.voiceId ?? context.voiceProfileId ?? '—'} · consent {context.voiceConsentState}
        </p>
      </div>

      <div data-testid={`review-audio-${reviewId}`}>
        <h4>Audio excerpt</h4>
        <p className="dp-muted">
          {context.audioArtifactId !== undefined ? `Preview artifact ${context.audioArtifactId}` : 'No preview artifact (IDs only; URLs mint at serve time).'}
        </p>
      </div>

      <div data-testid={`review-sync-${reviewId}`}>
        <h4>Sync / QC summary</h4>
        <p className="dp-muted">
          Offset {context.syncOffsetMs !== undefined ? `${String(context.syncOffsetMs)} ms` : '—'}
          {context.syncDrift ? ' · drift' : ' · in sync'}
        </p>
        {context.qcIssues.length === 0 ? (
          <p className="dp-muted">No QC flags.</p>
        ) : (
          <ul>
            {context.qcIssues.map((issue) => (
              <li key={issue.id} data-testid={`review-qc-${issue.id}`}>
                <span>{issue.code}</span>
                {' · '}
                <span>{issue.severity}</span>
                {issue.message !== '' ? (
                  <>
                    {' · '}
                    <span>{issue.message}</span>
                  </>
                ) : null}
              </li>
            ))}
          </ul>
        )}
      </div>

      <div data-testid={`review-versions-${reviewId}`}>
        <h4>Providers / versions</h4>
        <p className="dp-muted">
          Transcript {String(context.transcript.length)} version(s) · translation {String(context.translation.length)} version(s)
          {context.truncated ? ' · truncated' : ''}
        </p>
      </div>

      <div data-testid={`review-actions-${reviewId}`}>
        <h4>Dispositions</h4>
        <label htmlFor={`review-reason-${reviewId}`}>Reason (required for reject/requeue, note otherwise)</label>
        <textarea
          id={`review-reason-${reviewId}`}
          data-testid={`review-reason-${reviewId}`}
          value={reason}
          maxLength={REVIEW_MAX_REASON_LENGTH + 100}
          onChange={(event) => {
            setReason(event.target.value);
            if (fieldError !== null) {
              setFieldError(null);
            }
          }}
        />
        <label htmlFor={`review-edit-${reviewId}`}>Corrected text (resolve-with-edit only)</label>
        <textarea
          id={`review-edit-${reviewId}`}
          data-testid={`review-edit-${reviewId}`}
          value={editText}
          onChange={(event) => {
            setEditText(event.target.value);
            if (fieldError !== null) {
              setFieldError(null);
            }
          }}
        />
        {fieldError !== null ? (
          <p data-testid={`review-field-error-${reviewId}`} role="alert">
            {fieldError}
          </p>
        ) : null}
        <div style={{ display: 'flex', gap: 'var(--space-2)', flexWrap: 'wrap' }}>
          {actionDefs.map((def) =>
            isDispositionAllowed(context, def.action) ? (
              <button
                key={def.action}
                type="button"
                data-testid={def.testid}
                data-action={allowedTokenFor(def.action)}
                className="dp-btn dp-btn-primary dp-btn-md dp-focus-ring"
                disabled={pending}
                onClick={() => {
                  runDisposition(def.action);
                }}
              >
                {def.label}
              </button>
            ) : null,
          )}
        </div>
        <p data-testid={`review-permissions-${reviewId}`} className="dp-muted" hidden>
          {`canResolve:${context.canResolve ? 'true' : 'false'} canEdit:${context.canEdit ? 'true' : 'false'} allowed:${context.allowedActions.join(',')}`}
        </p>
      </div>

      <div data-testid={`review-history-${reviewId}`}>
        <h4>Audit trail</h4>
        {pending ? (
          <p data-testid={`review-history-pending-${reviewId}`} role="status">
            Saving disposition…
          </p>
        ) : null}
        {historyNewest.length === 0 ? (
          <p className="dp-muted">No audit entries yet.</p>
        ) : (
          <ol reversed>
            {historyNewest.map((entry) => (
              <li key={entry.id} data-testid={`review-history-entry-${entry.id}`}>
                <p>
                  {entry.action} by {entry.actor}
                </p>
                {entry.reason !== '' ? <p>{entry.reason}</p> : null}
                {entry.createdAt !== '' ? <p className="dp-muted">{entry.createdAt}</p> : null}
              </li>
            ))}
          </ol>
        )}
      </div>

      <div hidden>
        <span data-testid={`review-context-key-${reviewId}`}>{JSON.stringify(queryKeys.reviewContext.detail(reviewId))}</span>
      </div>
    </section>
  );
}
