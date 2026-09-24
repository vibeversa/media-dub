import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import type { PartView } from './useResumableUpload.js';

export interface UploadProgressProps {
  /** One entry per part, in part-number order. */
  readonly parts: readonly PartView[];
  readonly bytesUploaded: number;
  readonly sizeBytes: number;
  /** Retries a single errored part; rendered only on parts in `error`. */
  readonly onRetryPart?: (partNumber: number) => void;
}

/**
 * Per-part upload progress (Task 023).
 *
 * Byte-granular PUT progress is intentionally not faked: `fetch` exposes no
 * upload-progress events, so each part bar is truthful (empty while pending,
 * active while uploading, full when the server acknowledged it) and the
 * overall bar derives from acknowledged bytes only. Presentational: all
 * orchestration lives in `useResumableUpload`.
 */
export function UploadProgress({ parts, bytesUploaded, sizeBytes, onRetryPart }: UploadProgressProps): ReactNode {
  const { t } = useTranslation();
  const done = parts.filter((part) => part.status === 'done').length;
  return (
    <div data-testid="upload-progress">
      <p data-testid="upload-overall-label">
        {t('uploads:progress.overall', { done: String(done), total: String(parts.length) })}
      </p>
      <progress data-testid="upload-overall" value={bytesUploaded} max={Math.max(1, sizeBytes)}>
        {t('uploads:progress.overall', { done: String(done), total: String(parts.length) })}
      </progress>
      <ol data-testid="upload-parts">
        {parts.map((part) => (
          <li key={part.partNumber} data-testid={`upload-part-${part.partNumber}`}>
            <span>{t('uploads:progress.part', { number: String(part.partNumber) })}</span>{' '}
            <progress
              data-testid={`upload-part-${part.partNumber}-bar`}
              value={part.status === 'done' ? part.bytes : 0}
              max={Math.max(1, part.bytes)}
            >
              {part.status}
            </progress>{' '}
            <span data-testid={`upload-part-${part.partNumber}-status`}>
              {t(`uploads:progress.state.${part.status}`)}
            </span>
            {part.status === 'error' && onRetryPart !== undefined ? (
              <button
                type="button"
                data-testid={`upload-retry-part-${part.partNumber}`}
                className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
                onClick={() => {
                  onRetryPart(part.partNumber);
                }}
              >
                {t('uploads:actions.retryPart')}
              </button>
            ) : null}
          </li>
        ))}
      </ol>
    </div>
  );
}
