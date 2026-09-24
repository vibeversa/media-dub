import { useEffect, useRef, useState } from 'react';
import type { ChangeEvent, DragEvent, ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Alert } from '../../components/Alert/Alert.js';
import { ConfirmDialog } from '../../components/ConfirmDialog/ConfirmDialog.js';
import { useToast } from '../../components/Toast/useToast.js';
import { UploadProgress } from './UploadProgress.js';
import { useResumableUpload } from './useResumableUpload.js';
import { SERVER_PHASES } from './uploadStates.js';

export interface MediaUploaderProps {
  readonly projectId: string;
  /** Target language recorded in the persisted session metadata. */
  readonly language?: string;
}

/**
 * Resumable media uploader (Task 023).
 *
 * Drag-drop + file picker over `useResumableUpload`: pause aborts in-flight
 * parts, resume re-lists server parts first, cancel confirms then aborts
 * server-side and clears local metadata. Client phases (uploading/paused)
 * render separately from server phases (uploaded → validation → analysis →
 * ready/rejected) so the two are never confused. Rejected sessions map every
 * reason to guidance + a next action (R4). All copy renders as plain text.
 */
export function MediaUploader({ projectId, language }: MediaUploaderProps): ReactNode {
  const { t } = useTranslation();
  const { push } = useToast();
  const upload = useResumableUpload(projectId, language ?? '');
  const [dragging, setDragging] = useState(false);
  const [confirmCancel, setConfirmCancel] = useState(false);
  const lastToastedPhase = useRef<string | null>(null);

  const session = upload.session;
  const phase = session?.phase;

  useEffect(() => {
    if (phase === 'ready' && lastToastedPhase.current !== 'ready') {
      lastToastedPhase.current = 'ready';
      push('success', t('uploads:toasts.ready'));
    } else if (phase === 'rejected' && lastToastedPhase.current !== 'rejected') {
      lastToastedPhase.current = 'rejected';
      push('error', t('uploads:toasts.rejected'));
    } else if (phase === undefined) {
      lastToastedPhase.current = null;
    }
  }, [phase, push, t]);

  function handleFiles(files: FileList | null): void {
    if (files === null || files.length === 0) {
      return;
    }
    const file = files[0];
    if (file === undefined) {
      return;
    }
    if (upload.needsReattach && session !== undefined) {
      void upload.reattach(file);
      return;
    }
    void upload.start(file);
  }

  function handleInputChange(event: ChangeEvent<HTMLInputElement>): void {
    handleFiles(event.target.files);
    event.target.value = '';
  }

  function handleDrop(event: DragEvent<HTMLDivElement>): void {
    event.preventDefault();
    setDragging(false);
    handleFiles(event.dataTransfer.files);
  }

  const doneCount = session?.completedParts.length ?? 0;
  const totalCount = session?.partCount ?? 0;
  const rejectionReason = session?.rejectionReason ?? 'unknown';

  return (
    <section data-testid="upload-uploader" aria-label={t('uploads:title')}>
      <h1 className="text-xl font-semibold">{t('uploads:title')}</h1>
      <p className="mt-2 text-sm text-slate-600">{t('uploads:subtitle')}</p>

      {upload.localError !== null ? (
        <div data-testid="upload-local-error">
          <Alert
            tone="error"
            title={upload.localError === 'empty' ? t('uploads:validation.empty') : upload.localError === 'network' ? t('uploads:validation.network') : upload.localError}
          />
        </div>
      ) : null}

      {upload.fingerprintNotice ? (
        <div data-testid="upload-fingerprint-notice">
          <Alert tone="warning" title={t('uploads:fingerprint.restarted')} />
        </div>
      ) : null}

      {upload.duplicateReady !== null ? (
        <div data-testid="upload-duplicate-ready">
          <Alert tone="info" title={t('uploads:duplicate.title')}>
            <p>{t('uploads:duplicate.description')}</p>
            <Link to={`/projects/${projectId}`} data-testid="upload-use-existing">
              {t('uploads:duplicate.useExisting')}
            </Link>
          </Alert>
        </div>
      ) : null}

      {session === undefined ? (
        <div
          data-testid="upload-dropzone"
          onDragOver={(event) => {
            event.preventDefault();
            setDragging(true);
          }}
          onDragLeave={() => {
            setDragging(false);
          }}
          onDrop={handleDrop}
          aria-busy={dragging}
        >
          <label htmlFor="upload-file-input">{t('uploads:picker.label')}</label>
          <input
            id="upload-file-input"
            data-testid="upload-file-input"
            type="file"
            accept="audio/*,video/*"
            disabled={upload.busy}
            onChange={handleInputChange}
          />
          <p className="dp-muted">{t('uploads:picker.dropHint')}</p>
          <p className="dp-muted">{t('uploads:picker.formats')}</p>
          <p className="dp-muted">{t('uploads:picker.serverNote')}</p>
        </div>
      ) : null}

      {session !== undefined && (phase === 'uploading' || phase === 'paused' || phase === 'error') ? (
        <div data-testid="upload-active">
          <h2 data-testid="upload-phase">{t(`uploads:phases.${phase}`)}</h2>
          {phase === 'paused' ? (
            <div data-testid="upload-resume-prompt">
              <Alert
                tone="info"
                title={t(session.autoPaused ? 'uploads:resumePrompt.networkTitle' : 'uploads:resumePrompt.title')}
              >
                <p>
                  {t(session.autoPaused ? 'uploads:resumePrompt.networkDescription' : 'uploads:resumePrompt.description', {
                    done: String(doneCount),
                    total: String(totalCount),
                  })}
                </p>
              </Alert>
            </div>
          ) : null}
          {phase === 'error' ? (
            <div data-testid="upload-error">
              <Alert
                tone="error"
                title={t('uploads:phases.error')}
                details={
                  session.correlationId !== undefined && session.correlationId !== ''
                    ? `${session.errorMessage ?? ''}\n${t('uploads:rejected.reportRef', { ref: session.correlationId })}`
                    : (session.errorMessage ?? undefined)
                }
              />
            </div>
          ) : null}
          <UploadProgress
            parts={upload.parts}
            bytesUploaded={session.bytesUploaded}
            sizeBytes={session.size}
            onRetryPart={(partNumber) => {
              void upload.retryPart(partNumber);
            }}
          />
          <div data-testid="upload-controls">
            {phase === 'uploading' ? (
              <button
                type="button"
                data-testid="upload-pause"
                className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
                onClick={upload.pause}
              >
                {t('uploads:controls.pause')}
              </button>
            ) : (
              <button
                type="button"
                data-testid="upload-resume"
                className="dp-btn dp-btn-primary dp-btn-md dp-focus-ring"
                disabled={upload.busy || upload.needsReattach}
                onClick={() => {
                  void upload.resume();
                }}
              >
                {t('uploads:controls.resume')}
              </button>
            )}
            <button
              type="button"
              data-testid="upload-cancel"
              className="dp-btn dp-btn-danger dp-btn-md dp-focus-ring"
              onClick={() => {
                setConfirmCancel(true);
              }}
            >
              {t('uploads:controls.cancel')}
            </button>
          </div>
        </div>
      ) : null}

      {upload.needsReattach && session !== undefined ? (
        <div data-testid="upload-reattach-prompt">
          <Alert tone="warning" title={t('uploads:reattachPrompt.title')}>
            <p>{t('uploads:reattachPrompt.description', { done: String(doneCount), total: String(totalCount) })}</p>
            <label htmlFor="upload-reattach-input">{t('uploads:controls.reattach')}</label>
            <input
              id="upload-reattach-input"
              data-testid="upload-reattach-input"
              type="file"
              accept="audio/*,video/*"
              onChange={handleInputChange}
            />
          </Alert>
        </div>
      ) : null}

      {session !== undefined && (phase === 'uploaded' || phase === 'validating' || phase === 'analyzing') ? (
        <div data-testid="upload-server-state">
          <h2 data-testid="upload-phase">{t(`uploads:phases.${phase}`)}</h2>
          <h3>{t('uploads:steps.clientTitle')}</h3>
          <p data-testid="upload-step-client">{t('uploads:steps.uploaded')}</p>
          <h3>{t('uploads:steps.serverTitle')}</h3>
          <ol data-testid="upload-steps">
            {SERVER_PHASES.filter((entry) => entry !== 'ready' && entry !== 'rejected').map((entry) => (
              <li key={entry} data-testid={`upload-step-${entry}`}>
                {t(`uploads:steps.${entry}`)}
              </li>
            ))}
          </ol>
        </div>
      ) : null}

      {phase === 'ready' && session !== undefined ? (
        <div data-testid="upload-ready">
          <Alert tone="success" title={t('uploads:ready.title')}>
            <p>{t('uploads:ready.description')}</p>
            <Link to={`/projects/${projectId}`} data-testid="upload-open-project">
              {t('uploads:ready.openWorkspace')}
            </Link>
          </Alert>
        </div>
      ) : null}

      {phase === 'rejected' && session !== undefined ? (
        <div data-testid={`upload-rejected-${rejectionReason}`}>
          <Alert tone="error" title={t('uploads:rejected.title')}>
            <h3>{t(`uploads:rejected.${rejectionReason}Title`)}</h3>
            <p>{t(`uploads:rejected.${rejectionReason}Guidance`)}</p>
            {session.correlationId !== undefined && session.correlationId !== '' ? (
              <p>{t('uploads:rejected.reportRef', { ref: session.correlationId })}</p>
            ) : null}
            {rejectionReason === 'duplicate' ? (
              <Link to={`/projects/${projectId}`} data-testid="upload-use-existing">
                {t('uploads:duplicate.useExisting')}
              </Link>
            ) : null}
            {rejectionReason === 'expired' ? (
              <button
                type="button"
                data-testid="upload-start-over"
                className="dp-btn dp-btn-primary dp-btn-md dp-focus-ring"
                onClick={() => {
                  void upload.cancel().then(() => upload.clearNotices());
                }}
              >
                {t('uploads:rejected.startOver')}
              </button>
            ) : (
              <label htmlFor="upload-replace-input">{t('uploads:rejected.replace')}</label>
            )}
            {rejectionReason !== 'duplicate' && rejectionReason !== 'expired' ? (
              <input
                id="upload-replace-input"
                data-testid="upload-replace-input"
                type="file"
                accept="audio/*,video/*"
                onChange={handleInputChange}
              />
            ) : null}
          </Alert>
        </div>
      ) : null}

      <ConfirmDialog
        open={confirmCancel}
        title={t('uploads:controls.cancelTitle')}
        description={t('uploads:controls.cancelDescription')}
        confirmLabel={t('uploads:controls.cancelConfirm')}
        onCancel={() => {
          setConfirmCancel(false);
        }}
        onConfirm={() => {
          setConfirmCancel(false);
          void upload.cancel().then(() => push('info', t('uploads:toasts.cancelled')));
        }}
      />
    </section>
  );
}
