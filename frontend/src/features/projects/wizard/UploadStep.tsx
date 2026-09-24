import type { ChangeEvent, ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Input } from '../../../components/Input/Input.js';
import type { FieldErrors } from './validation.js';
import type { UploadMode, WizardDraft } from './draft.js';

export interface UploadStepProps {
  readonly draft: WizardDraft;
  readonly errors: FieldErrors;
  /** In-memory file name (null after refresh until re-attached). */
  readonly attachedFileName: string | null;
  readonly onModeChange: (mode: UploadMode) => void;
  readonly onFileSelect: (file: File | null) => void;
}

/**
 * Step 4: media source (Task 023 seam).
 *
 * "Upload later" (default) defers to the workspace media tab after creation.
 * "Attach now" records a file choice in memory only — the draft keeps the
 * name/size/type metadata, never the bytes — so a refresh requires
 * re-attaching while every other entry survives (R3). The real transfer runs
 * from the workspace uploader.
 */
export function UploadStep({ draft, errors, attachedFileName, onModeChange, onFileSelect }: UploadStepProps): ReactNode {
  const { t } = useTranslation();

  function handleFileInput(event: ChangeEvent<HTMLInputElement>): void {
    const files = event.target.files;
    onFileSelect(files !== null && files.length > 0 ? (files[0] as File) : null);
  }

  return (
    <div data-testid="wizard-step-upload">
      <fieldset>
        <legend>{t('projects:create.upload.mode')}</legend>
        <label>
          <input
            type="radio"
            name="upload-mode"
            data-testid="wizard-upload-later"
            checked={draft.uploadMode === 'later'}
            onChange={() => {
              onModeChange('later');
            }}
          />
          {t('projects:create.upload.later')}
        </label>
        <p className="dp-muted">{t('projects:create.upload.laterHelp')}</p>
        <label>
          <input
            type="radio"
            name="upload-mode"
            data-testid="wizard-upload-now"
            checked={draft.uploadMode === 'now'}
            onChange={() => {
              onModeChange('now');
            }}
          />
          {t('projects:create.upload.now')}
        </label>
        <p className="dp-muted">{t('projects:create.upload.nowHelp')}</p>
      </fieldset>
      {draft.uploadMode === 'now' ? (
        <div data-testid="wizard-upload-picker">
          <Input
            label={t('projects:create.upload.file')}
            data-testid="wizard-file"
            type="file"
            accept="audio/*,video/*"
            error={errors['uploadFile']}
            onChange={handleFileInput}
          />
          {attachedFileName !== null ? (
            <p className="dp-muted" data-testid="wizard-file-attached">
              {attachedFileName}
            </p>
          ) : draft.uploadFileName !== '' ? (
            <p className="dp-muted" data-testid="wizard-file-reattach">
              {t('projects:create.upload.reattachNote')}
            </p>
          ) : null}
        </div>
      ) : null}
      <p className="dp-muted">{t('projects:create.upload.workspaceNote')}</p>
    </div>
  );
}
