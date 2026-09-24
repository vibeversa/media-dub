import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Alert } from '../../../components/Alert/Alert.js';
import { Select } from '../../../components/Select/Select.js';
import { LANGUAGE_OPTIONS } from './languages.js';
import type { FieldErrors } from './validation.js';
import type { WizardDraft } from './draft.js';

export interface LanguageStepProps {
  readonly draft: WizardDraft;
  readonly errors: FieldErrors;
  readonly onChange: (patch: Partial<WizardDraft>) => void;
}

/**
 * Step 2: source + target selects (R2).
 *
 * The immutability warning renders here and again on the review step; the
 * target value is read-only everywhere else by construction (no other step
 * renders a target input, and no post-create edit UI exists).
 */
export function LanguageStep({ draft, errors, onChange }: LanguageStepProps): ReactNode {
  const { t } = useTranslation();
  const options = LANGUAGE_OPTIONS.map((option) => ({ value: option.value, label: option.label }));
  return (
    <div data-testid="wizard-step-language">
      <Select
        label={t('projects:create.language.source')}
        data-testid="wizard-source"
        value={draft.sourceLanguage}
        error={errors['sourceLanguage']}
        options={options}
        onChange={(e) => {
          onChange({ sourceLanguage: e.target.value });
        }}
      />
      <p className="dp-muted" data-testid="wizard-source-note">
        {t('projects:create.language.sourceHelp')} {t('projects:create.language.autoDetectNote')}
      </p>
      <Select
        label={t('projects:create.language.target')}
        data-testid="wizard-target"
        value={draft.targetLanguage}
        error={errors['targetLanguage']}
        options={options}
        onChange={(e) => {
          onChange({ targetLanguage: e.target.value });
        }}
      />
      <p className="dp-muted" data-testid="wizard-target-help">
        {t('projects:create.language.targetHelp')}
      </p>
      <div data-testid="wizard-immutable-notice">
        <Alert tone="warning" title={t('projects:create.language.immutableWarning')} />
      </div>
    </div>
  );
}
