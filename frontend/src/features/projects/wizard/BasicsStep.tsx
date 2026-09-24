import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Input } from '../../../components/Input/Input.js';
import { Textarea } from '../../../components/Textarea/Textarea.js';
import type { FieldErrors } from './validation.js';
import type { WizardDraft } from './draft.js';

export interface BasicsStepProps {
  readonly draft: WizardDraft;
  readonly errors: FieldErrors;
  readonly onChange: (patch: Partial<WizardDraft>) => void;
}

/** Step 1: name (required, R1) + description with duplicate-name guidance. */
export function BasicsStep({ draft, errors, onChange }: BasicsStepProps): ReactNode {
  const { t } = useTranslation();
  return (
    <div data-testid="wizard-step-basics">
      <Input
        label={t('projects:create.basics.name')}
        data-testid="wizard-name"
        placeholder={t('projects:create.basics.namePlaceholder')}
        hint={t('projects:create.basics.nameHint')}
        error={errors['name']}
        value={draft.name}
        maxLength={200}
        autoComplete="off"
        required
        onChange={(e) => {
          onChange({ name: e.target.value });
        }}
      />
      <p className="dp-muted" data-testid="wizard-duplicate-hint">
        {t('projects:create.basics.duplicateHint')}
      </p>
      <Textarea
        label={t('projects:create.basics.description')}
        data-testid="wizard-description"
        placeholder={t('projects:create.basics.descriptionPlaceholder')}
        error={errors['description']}
        value={draft.description}
        maxLength={2000}
        onChange={(e) => {
          onChange({ description: e.target.value });
        }}
      />
    </div>
  );
}
