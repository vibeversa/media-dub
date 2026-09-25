import { useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import { Alert } from '../../components/Alert/Alert.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { apiClient } from '../../api/client/index.js';
import { usePreviewMedia } from '../timeline/useTimelineMedia.js';
import { useTimelinePlayerStore } from '../timeline/playerStore.js';
import { formatPlayerTime } from '../timeline/types.js';
import {
  iconForQualitySeverity,
  iconForQualityStatus,
  labelForQualitySeverity,
  labelForQualityStatus,
  patternForQualityStatus,
} from './types.js';
import type { QualityIssueView } from './types.js';

export interface QualityIssueProps {
  readonly projectId: string;
  readonly issue: QualityIssueView;
}

function drawMiniWaveform(canvas: HTMLCanvasElement, seed: string): void {
  const context = canvas.getContext('2d');
  if (context === null) {
    return;
  }
  const width = 220;
  const height = 48;
  const ratio = typeof window !== 'undefined' && Number.isFinite(window.devicePixelRatio) ? window.devicePixelRatio : 1;
  canvas.width = Math.round(width * ratio);
  canvas.height = Math.round(height * ratio);
  context.save();
  context.scale(ratio, ratio);
  context.clearRect(0, 0, width, height);
  let hash = 0;
  for (let i = 0; i < seed.length; i += 1) {
    hash = (hash * 31 + seed.charCodeAt(i)) % 997;
  }
  const bars = 44;
  const barWidth = width / bars;
  context.fillStyle = 'rgb(100, 116, 139)';
  for (let i = 0; i < bars; i += 1) {
    const sample = ((hash + i * 37) % 100) / 100;
    const barHeight = Math.max(2, Math.round(sample * height));
    const x = Math.floor(i * barWidth);
    const y = Math.floor((height - barHeight) / 2);
    context.fillRect(x, y, Math.max(1, Math.floor(barWidth) - 1), barHeight);
  }
  context.restore();
}

/**
 * QC issue card (Task 032, R2 + R3 + R5).
 *
 * Shows code, severity (icon + label), scope, linked segment id,
 * description, suggested action, and status (icon + label + pattern) — none
 * omitted. Evidence renders inline per issue type: waveform excerpt (mini
 * canvas), timestamp (click jumps the Task 030 playhead), audio excerpt
 * player (signed preview URL, never logged), QC metric readout (as
 * provided), and artifact link (signed URL, single refetch then an
 * unavailable note). Actions are jump-to-timeline, open-in-review, and
 * retry-link where the workspace advertises `processing.retry`; unavailable
 * actions are omitted with a reason tooltip.
 */
export function QualityIssue({ projectId, issue }: QualityIssueProps): ReactNode {
  const queryClient = useQueryClient();
  const preview = usePreviewMedia(projectId);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const [artifactFailed, setArtifactFailed] = useState(false);
  const [artifactRefetched, setArtifactRefetched] = useState(false);

  useEffect(() => {
    if (canvasRef.current !== null && issue.startMs !== undefined) {
      try {
        drawMiniWaveform(canvasRef.current, issue.id);
      } catch {
        // Canvas is progressive enhancement; evidence text stays.
      }
    }
  }, [issue.id, issue.startMs]);

  const severityIcon = iconForQualitySeverity(issue.severity);
  const severityLabel = labelForQualitySeverity(issue.severity);
  const statusIcon = iconForQualityStatus(issue.status);
  const statusLabel = labelForQualityStatus(issue.status);
  const statusPattern = patternForQualityStatus(issue.status);

  async function handleArtifactError(): Promise<void> {
    if (artifactRefetched) {
      setArtifactFailed(true);
      return;
    }
    setArtifactRefetched(true);
    try {
      await queryClient.invalidateQueries({ queryKey: queryKeys.quality.detail(projectId) });
    } finally {
      // Single refetch only: a second failure renders the graceful note.
      setArtifactFailed(true);
    }
  }

  async function handleRetry(): Promise<void> {
    if (issue.segmentId === undefined) {
      return;
    }
    try {
      await apiClient.retrySegment({ path: { projectId, segmentId: issue.segmentId } });
      await queryClient.invalidateQueries({ queryKey: queryKeys.quality.detail(projectId) });
    } catch {
      setArtifactFailed(false);
    }
  }

  const audioSrc = preview.data?.url;
  const showArtifact = issue.artifactUrl !== undefined && issue.artifactUrl !== '' && !artifactFailed;
  const showUnavailable = artifactFailed || (issue.artifactId !== undefined && issue.artifactUrl === undefined);

  return (
    <article
      data-testid={`quality-issue-${issue.id}`}
      aria-label={`Quality issue ${issue.code}`}
      data-status={issue.status}
      data-severity={issue.severity}
      data-scope={issue.scope}
      data-code={issue.code}
    >
      <header>
        <h4 data-testid={`quality-code-${issue.id}`}>{issue.code}</h4>
        <p data-testid={`quality-severity-${issue.id}`} data-pattern={statusPattern}>
          <span data-testid={`quality-severity-icon-${issue.id}`} aria-hidden="true">
            {severityIcon}
          </span>{' '}
          <span data-testid={`quality-severity-label-${issue.id}`}>{issue.severity} ({severityLabel})</span>{' '}
          <span data-testid={`quality-severity-pattern-${issue.id}`}>{statusPattern}</span>
        </p>
        <p data-testid={`quality-status-${issue.id}`} data-pattern={statusPattern}>
          <span data-testid={`quality-status-icon-${issue.id}`} aria-hidden="true">
            {statusIcon}
          </span>{' '}
          <span data-testid={`quality-status-label-${issue.id}`}>{issue.status} ({statusLabel})</span>{' '}
          <span data-testid={`quality-status-pattern-${issue.id}`}>{statusPattern}</span>
        </p>
        <p data-testid={`quality-scope-${issue.id}`}>
          Scope: {issue.scope}
          {issue.scope === 'segment' && issue.segmentId !== undefined ? ` · segment ${issue.segmentId}` : ''}
        </p>
        <p data-testid={`quality-segment-${issue.id}`} className="dp-muted">
          {issue.segmentId !== undefined ? `Segment ${issue.segmentId}` : 'Project-level issue (no segment scope).'}
        </p>
      </header>

      <p data-testid={`quality-description-${issue.id}`}>{issue.description}</p>
      <p data-testid={`quality-action-${issue.id}`} className="dp-muted">
        Suggested action: {issue.suggestedAction}
      </p>

      <div data-testid={`quality-evidence-${issue.id}`}>
        <h5>Evidence</h5>
        {issue.startMs !== undefined && issue.endMs !== undefined ? (
          <div>
            <canvas
              ref={canvasRef}
              data-testid={`quality-evidence-waveform-${issue.id}`}
              data-peaks="44"
              role="img"
              aria-label={`Waveform excerpt for ${issue.code}`}
              style={{ width: '220px', height: '48px', display: 'block' }}
            />
            <button
              type="button"
              data-testid={`quality-evidence-timestamp-${issue.id}`}
              className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
              title={`Jump to ${formatPlayerTime(issue.startMs)} in the timeline`}
              onClick={() => {
                useTimelinePlayerStore.getState().requestSeek(issue.startMs ?? 0);
              }}
            >
              {formatPlayerTime(issue.startMs)}–{formatPlayerTime(issue.endMs)}
            </button>
          </div>
        ) : (
          <p className="dp-muted">No timestamp evidence for this issue.</p>
        )}

        <div>
          {audioSrc !== undefined ? (
            <audio
              data-testid={`quality-evidence-audio-${issue.id}`}
              src={audioSrc}
              controls
              preload="metadata"
              aria-label={`Audio excerpt for ${issue.code}`}
            />
          ) : preview.isPending ? (
            <p data-testid={`quality-evidence-audio-loading-${issue.id}`} className="dp-muted">
              Loading audio excerpt…
            </p>
          ) : (
            <p data-testid={`quality-evidence-audio-missing-${issue.id}`} className="dp-muted">
              Audio excerpt unavailable (preview not ready).
            </p>
          )}
        </div>

        {issue.metricName !== undefined && issue.metricValue !== undefined ? (
          <p data-testid={`quality-evidence-metric-${issue.id}`}>
            {issue.metricName}: {issue.metricValue}
            {issue.metricUnit !== undefined && issue.metricUnit !== '' ? ` ${issue.metricUnit}` : ''}
          </p>
        ) : (
          <p data-testid={`quality-evidence-metric-${issue.id}`} className="dp-muted">
            No metric readout for this issue.
          </p>
        )}

        {showArtifact && issue.artifactUrl !== undefined ? (
          <a
            data-testid={`quality-evidence-artifact-${issue.id}`}
            href={issue.artifactUrl}
            target="_blank"
            rel="noreferrer"
            onClick={() => {
              // Signed URL stays in the anchor only; never logged or copied
              // into shareable filter URLs.
            }}
            onAuxClick={() => undefined}
          >
            Open evidence artifact{issue.artifactId !== undefined ? ` ${issue.artifactId}` : ''}
          </a>
        ) : null}
        {issue.artifactUrl !== undefined && !showArtifact ? (
          <button
            type="button"
            data-testid={`quality-evidence-artifact-retry-${issue.id}`}
            className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
            onClick={() => {
              void handleArtifactError();
            }}
          >
            Retry evidence artifact
          </button>
        ) : null}
        {showUnavailable && issue.artifactId !== undefined ? (
          <p data-testid={`quality-evidence-unavailable-${issue.id}`} className="dp-muted">
            Evidence unavailable for artifact {issue.artifactId} (signed link expired; refreshed once).
          </p>
        ) : null}
        {issue.artifactId === undefined && issue.artifactUrl === undefined ? (
          <p data-testid={`quality-evidence-no-artifact-${issue.id}`} className="dp-muted">
            No linked artifact for this issue.
          </p>
        ) : null}
      </div>

      <div style={{ display: 'flex', gap: 'var(--space-2)', flexWrap: 'wrap' }}>
        {issue.actions.canJump && issue.startMs !== undefined ? (
          <button
            type="button"
            data-testid={`quality-jump-${issue.id}`}
            className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
            title={`Jump to ${formatPlayerTime(issue.startMs)} in the timeline`}
            onClick={() => {
              useTimelinePlayerStore.getState().requestSeek(issue.startMs ?? 0);
            }}
          >
            Jump to timeline
          </button>
        ) : null}
        {issue.actions.canOpenReview ? (
          <Link
            data-testid={`quality-review-link-${issue.id}`}
            to={issue.reviewId !== undefined ? `/review?project=${encodeURIComponent(projectId)}` : `/review?project=${encodeURIComponent(projectId)}`}
          >
            Open in review
          </Link>
        ) : (
          <span
            data-testid={`quality-action-unavailable-${issue.id}`}
            title="No open review for this segment."
            className="dp-muted"
          >
            Review unavailable
          </span>
        )}
        {issue.actions.canRetry && issue.segmentId !== undefined ? (
          <button
            type="button"
            data-testid={`quality-retry-${issue.id}`}
            className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
            title="Retry this segment upstream"
            onClick={() => {
              void handleRetry();
            }}
          >
            Retry segment
          </button>
        ) : (
          <span
            data-testid={`quality-retry-unavailable-${issue.id}`}
            title={issue.actions.retryReason ?? 'Retry is not advertised for this project.'}
            className="dp-muted"
          >
            Retry unavailable
          </span>
        )}
      </div>

      {issue.status === 'Blocked' ? (
        <div data-testid={`quality-blocked-note-${issue.id}`}>
          <Alert tone="error" title="Blocking issue">
            <p>
              <span aria-hidden="true">■</span> blocked · pattern hatched-block — this issue blocks the render.
            </p>
          </Alert>
        </div>
      ) : null}
    </article>
  );
}
