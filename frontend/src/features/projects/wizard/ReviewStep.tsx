import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Alert } from '../../../components/Alert/Alert.js';
import { SETTINGS_VERSION_NEW, buildHumanSummary, previewConfigHash } from './draft.js';
import type { FieldErrors } from './validation.js';
import type { WizardDraft } from './draft.js';

export interface ReviewStepProps {
  readonly draft: WizardDraft;
  /** Server-mapped field errors (empty when the last submit succeeded or never ran). */
  readonly serverFields: FieldErrors;
  /** Form-level server failure, already suffixed with the correlation ref. */
  readonly serverForm: string | null;
  readonly onEditStep: (step: 'basics' | 'language' | 'settings' | 'upload') => void;
}

function rowLabel(t: (key: string, options?: Record<string, string | number>) => string, id: string): string {
  switch (id) {
    case 'name':
      return t('projects:create.review.rowName');
    case 'description':
      return t('projects:create.review.rowDescription');
    case 'source':
      return t('projects:create.review.rowSource');
    case 'target':
      return t('projects:create.review.rowTarget');
    case 'separation':
      return t('projects:create.review.rowSeparation');
    case 'profile':
      return t('projects:create.review.rowProfile');
    case 'timing':
      return t('projects:create.review.rowTiming');
    case 'voice':
      return t('projects:create.review.rowVoice');
    case 'threshold':
      return t('projects:create.review.rowThreshold');
    case 'glossary':
      return t('projects:create.review.rowGlossary');
    case 'style':
      return t('projects:create.review.rowStyle');
    case 'upload':
      return t('projects:create.review.rowUpload');
    default:
      return id;
  }
}

function rowValue(
  t: (key: string, options?: Record<string, string | number>) => string,
  id: string,
  value: string,
): string {
  if (id === 'glossary') {
    return t('projects:create.review.rowGlossaryCount', { count: value });
  }
  if (id === 'upload') {
    if (value === 'later') {
      return t('projects:create.review.rowUploadLater');
    }
    if (value === 'now-missing') {
      return t('projects:create.upload.missingFile');
    }
    return t('projects:create.review.rowUploadNow', { name: value });
  }
  return value;
}

/**
 * Step 5: review before start (R4).
 *
 * Shows the human summary, the new-project settings version (always 1), and
 * a client-side hash preview labeled as such — the server computes the
 * authoritative hash on create and returns it on the created project.
 * Repeats the target-language immutability notice (R2). Server failures
 * render here with field errors preserved alongside the untouched draft.
 */
export function ReviewStep({ draft, serverFields, serverForm, onEditStep }: ReviewStepProps): ReactNode {
  const { t } = useTranslation();
  const rows = buildHumanSummary(draft);
  const hash = previewConfigHash(draft);
  const fieldEntries = Object.entries(serverFields);
  return (
    <div data-testid="wizard-step-review">
      <h2>{t('projects:create.review.title')}</h2>
      {serverForm !== null ? (
        <div data-testid="wizard-server-error">
          <Alert tone="error" title={t('projects:create.errors.formError')} details={serverForm} />
        </div>
      ) : null}
      {fieldEntries.length > 0 ? (
        <div data-testid="wizard-server-fields">
          <Alert tone="error" title={t('projects:create.errors.fixFields')}>
            <ul>
              {fieldEntries.map(([field, message]) => (
                <li key={field} data-testid={`wizard-server-field-${field}`}>
                  {field}: {message}
                </li>
              ))}
            </ul>
          </Alert>
        </div>
      ) : null}
      <h3>{t('projects:create.review.summaryTitle')}</h3>
      <dl data-testid="wizard-summary">
        {rows.map((row) => (
          <div key={row.id} data-testid={`wizard-summary-${row.id}`}>
            <dt>{rowLabel(t, row.id)}</dt>
            <dd>{rowValue(t, row.id, row.value)}</dd>
          </div>
        ))}
      </dl>
      <dl data-testid="wizard-config">
        <div data-testid="wizard-config-version">
          <dt>{t('projects:create.review.version')}</dt>
          <dd>{SETTINGS_VERSION_NEW}</dd>
        </div>
        <div data-testid="wizard-config-hash">
          <dt>{t('projects:create.review.hash')}</dt>
          <dd>{hash}</dd>
        </div>
      </dl>
      <p className="dp-muted" data-testid="wizard-hash-note">
        {t('projects:create.review.hashPreviewNote')}
      </p>
      <div data-testid="wizard-immutable-repeat">
        <Alert
          tone="warning"
          title={t('projects:create.review.immutableRepeat', { language: draft.targetLanguage })}
        />
      </div>
      <div data-testid="wizard-edit-links">
        {(['basics', 'language', 'settings', 'upload'] as const).map((step) => (
          <button
            key={step}
            type="button"
            data-testid={`wizard-edit-${step}`}
            className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
            onClick={() => {
              onEditStep(step);
            }}
          >
            {t('projects:create.actions.editStep', { step: t(`projects:create.steps.${step}`) })}
          </button>
        ))}
      </div>
    </div>
  );
}
