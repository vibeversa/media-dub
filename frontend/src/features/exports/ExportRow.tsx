import { useState } from 'react';
import type { ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Alert } from '../../components/Alert/Alert.js';
import { useToast } from '../../components/Toast/useToast.js';
import { useAppStore } from '../../stores/index.js';
import { fetchExportDownloadUrl, invalidateExports, useCreateExport } from './useOutputs.js';
import { formatFileSize, iconForExportState, isExpiredError, isNotFoundError, patternForExportState } from './types.js';
import type { ExportView } from './types.js';

export interface ExportRowProps {
  readonly projectId: string;
  readonly job: ExportView;
}

/**
 * Export generation row (Task 033).
 *
 * Renders `queued`/`generating`/`ready`/`failed` distinctly with icon + text
 * + pattern (never color alone). Approximate progress shows only where the
 * backend provides it (`completeness` from `completenessJson`); no client
 * ETA is ever computed. Failed rows show the backend reason verbatim plus a
 * retry action only where `export.create` is advertised (otherwise a reason
 * tooltip, never a dead button).
 *
 * Downloads use a plain `<a download>`: the signed URL is fetched at click
 * time (never pre-fetched, never cached, never stored beyond the handler),
 * expired URLs refetch exactly once, double expiry shows an error with a
 * retry action. Deleted rows (404) toast and invalidate so the list drops
 * the row on refetch. File size/format render where the backend provides
 * them; internal paths never render (errors show the backend `message`
 * only).
 */
export function ExportRow({ projectId, job }: ExportRowProps): ReactNode {
  const { push } = useToast();
  const queryClient = useQueryClient();
  const permissions = useAppStore((s) => s.permissions);
  const createMutation = useCreateExport(projectId);
  const [downloadPending, setDownloadPending] = useState(false);
  const [downloadError, setDownloadError] = useState<string | null>(null);
  const [downloadCorrelationId, setDownloadCorrelationId] = useState<string | undefined>(undefined);
  const [retryMessage, setRetryMessage] = useState<string | null>(null);

  const canRetry = permissions.includes('export.create');
  const retryReason = canRetry ? undefined : 'Retry needs the export.create permission.';
  const icon = iconForExportState(job.displayState);
  const pattern = patternForExportState(job.displayState);
  const sizeText = formatFileSize(job.fileSizeBytes);

  async function triggerDownload(url: string): Promise<void> {
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = `${job.id}.${job.format}`;
    anchor.rel = 'noreferrer';
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
  }

  async function handleDownload(event: React.MouseEvent<HTMLAnchorElement>): Promise<void> {
    event.preventDefault();
    if (downloadPending) {
      return;
    }
    setDownloadPending(true);
    setDownloadError(null);
    setDownloadCorrelationId(undefined);
    try {
      const result = await fetchExportDownloadUrl(projectId, job.id);
      await triggerDownload(result.url);
    } catch (error) {
      const appError = error as { code?: string; status?: number; message?: string; correlationId?: string };
      if (isNotFoundError(appError)) {
        push('error', 'This export no longer exists. List refreshed.');
        await invalidateExports(queryClient, projectId, job.id);
        return;
      }
      if (isExpiredError(appError)) {
        setDownloadError(appError.message ?? 'This download link expired. Request a fresh download.');
        setDownloadCorrelationId(appError.correlationId);
        return;
      }
      setDownloadError(appError.message ?? 'The download could not be prepared. No data was changed.');
      setDownloadCorrelationId(appError.correlationId);
    } finally {
      setDownloadPending(false);
    }
  }

  async function handleRetry(): Promise<void> {
    if (!canRetry) {
      return;
    }
    setRetryMessage(null);
    try {
      await createMutation.mutateAsync({ format: job.format, allowPartial: job.isPartial });
      await invalidateExports(queryClient, projectId);
      push('success', 'Export retry requested.');
    } catch (error) {
      const appError = error as { code?: string; status?: number; message?: string; correlationId?: string };
      if (appError.status === 409) {
        push('info', 'Export already generating. Showing the existing job.');
        await invalidateExports(queryClient, projectId);
        return;
      }
      setRetryMessage(appError.message ?? 'Export retry failed. No data was changed.');
    }
  }

  let progress: ReactNode = null;
  if (job.displayState === 'generating' || job.displayState === 'queued') {
    if (job.completeness !== undefined) {
      progress = (
        <p data-testid={`export-progress-${job.id}`} className="dp-muted">
          {`${String(job.completeness.ready)}/${String(job.completeness.total)} approximate`}
        </p>
      );
    } else {
      progress = (
        <p data-testid={`export-progress-${job.id}`} className="dp-muted">
          {job.displayState === 'queued' ? 'Queued — generation has not started.' : 'Generating — live updates apply automatically.'}
        </p>
      );
    }
  }

  return (
    <article
      data-testid={`export-row-${job.id}`}
      aria-label={`Export ${job.id}`}
      data-state={job.displayState}
      data-format={job.format}
      data-partial={job.isPartial ? 'true' : 'false'}
      data-pattern={pattern}
    >
      <header>
        <h4 data-testid={`export-id-${job.id}`}>{job.id}</h4>
        <p data-testid={`export-status-${job.id}`} data-pattern={pattern}>
          <span data-testid={`export-status-icon-${job.id}`} aria-hidden="true">
            {icon}
          </span>{' '}
          <span data-testid={`export-status-label-${job.id}`}>{job.displayState}</span>{' '}
          <span data-testid={`export-status-pattern-${job.id}`}>{pattern}</span>
        </p>
        <p data-testid={`export-format-${job.id}`}>Format: {job.format}</p>
        {job.createdAt !== undefined ? (
          <p data-testid={`export-created-${job.id}`} className="dp-muted">
            Created: {job.createdAt}
          </p>
        ) : null}
        {job.isPartial ? <p data-testid={`export-partial-${job.id}`}>Partial export (incomplete source accepted).</p> : null}
        {job.completeness !== undefined ? (
          <p data-testid={`export-completeness-${job.id}`} className="dp-muted">
            {`${String(job.completeness.ready)}/${String(job.completeness.total)}`}
          </p>
        ) : null}
        {sizeText !== undefined ? <p data-testid={`export-size-${job.id}`}>Size: {sizeText}</p> : null}
      </header>

      {progress}

      {job.displayState === 'failed' ? (
        <div data-testid={`export-error-${job.id}`}>
          <Alert tone="error" title="Export failed">
            <p data-testid={`export-error-message-${job.id}`}>{job.failureReason ?? 'The export failed. No file was produced.'}</p>
            {canRetry ? (
              <button
                type="button"
                data-testid={`export-retry-${job.id}`}
                className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
                disabled={createMutation.isPending}
                title="Retry this export with the same format"
                onClick={() => {
                  void handleRetry();
                }}
              >
                Retry export
              </button>
            ) : (
              <span
                data-testid={`export-retry-unavailable-${job.id}`}
                title={retryReason ?? 'Retry is not advertised for this project.'}
                className="dp-muted"
              >
                Retry unavailable
              </span>
            )}
          </Alert>
          {retryMessage !== null ? <p data-testid={`export-retry-message-${job.id}`}>{retryMessage}</p> : null}
        </div>
      ) : null}

      {job.displayState === 'ready' ? (
        <div>
          <a
            data-testid={`export-download-${job.id}`}
            download
            href="#download"
            onClick={(event) => {
              void handleDownload(event);
            }}
          >
            {downloadPending ? 'Preparing download…' : 'Download'}
          </a>
          {downloadError !== null ? (
            <div data-testid={`export-download-error-${job.id}`}>
              <Alert tone="error" title="Download failed" details={downloadCorrelationId !== undefined && downloadCorrelationId !== '' ? `Ref: ${downloadCorrelationId}` : undefined}>
                <p data-testid={`export-download-error-message-${job.id}`}>{downloadError}</p>
                <button
                  type="button"
                  data-testid={`export-download-retry-${job.id}`}
                  className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
                  onClick={(event) => {
                    event.preventDefault();
                    setDownloadError(null);
                    setDownloadCorrelationId(undefined);
                  }}
                >
                  Retry download
                </button>
              </Alert>
            </div>
          ) : null}
        </div>
      ) : null}

      {job.displayState === 'queued' || job.displayState === 'generating' ? (
        <p data-testid={`export-pending-${job.id}`} className="dp-muted">
          Download available when ready.
        </p>
      ) : null}
    </article>
  );
}
