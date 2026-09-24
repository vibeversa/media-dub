import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Input } from '../../../components/Input/Input.js';
import { Select } from '../../../components/Select/Select.js';
import { Textarea } from '../../../components/Textarea/Textarea.js';
import type { FieldErrors } from './validation.js';
import type { WizardDraft, WizardGlossaryEntry } from './draft.js';

export interface SettingsStepProps {
  readonly draft: WizardDraft;
  readonly errors: FieldErrors;
  readonly onChange: (patch: Partial<WizardDraft>) => void;
  readonly onGlossaryChange: (glossary: readonly WizardGlossaryEntry[]) => void;
}

function Section({ title, children }: { readonly title: string; readonly children: ReactNode }): ReactNode {
  return (
    <details className="dp-wizard-section">
      <summary className="dp-focus-ring">{title}</summary>
      <div>{children}</div>
    </details>
  );
}

/**
 * Step 3: progressive disclosure over the v1 processing-settings shape.
 *
 * Defaults arrive preselected from the draft; each collapsed section carries
 * plain-language help. The glossary editor manages `{sourceTerm, targetTerm,
 * notes}` rows within the backend limits (1000 entries, 256-char terms,
 * 1024-char notes). No step here asks about hidden execution concerns — only
 * the seven documented refinements exist.
 */
export function SettingsStep({ draft, errors, onChange, onGlossaryChange }: SettingsStepProps): ReactNode {
  const { t } = useTranslation();
  return (
    <div data-testid="wizard-step-settings">
      <p className="dp-muted">{t('projects:create.settings.intro')}</p>
      <Section title={t('projects:create.settings.separation')}>
        <Select
          label={t('projects:create.settings.separation')}
          data-testid="wizard-separation"
          value={draft.sourceSeparationPolicy}
          options={[
            { value: 'auto', label: t('projects:create.settings.separationAuto') },
            { value: 'dialogue', label: t('projects:create.settings.separationDialogue') },
            { value: 'music', label: t('projects:create.settings.separationMusic') },
          ]}
          onChange={(e) => {
            onChange({ sourceSeparationPolicy: e.target.value });
          }}
        />
        <p className="dp-muted">{t('projects:create.settings.separationHelp')}</p>
      </Section>
      <Section title={t('projects:create.settings.profile')}>
        <Select
          label={t('projects:create.settings.profile')}
          data-testid="wizard-profile"
          value={draft.outputProfile}
          options={[
            { value: 'standard', label: t('projects:create.settings.profileStandard') },
            { value: 'broadcast', label: t('projects:create.settings.profileBroadcast') },
            { value: 'social', label: t('projects:create.settings.profileSocial') },
          ]}
          onChange={(e) => {
            onChange({ outputProfile: e.target.value });
          }}
        />
        <p className="dp-muted">{t('projects:create.settings.profileHelp')}</p>
      </Section>
      <Section title={t('projects:create.settings.timing')}>
        <Select
          label={t('projects:create.settings.timing')}
          data-testid="wizard-timing"
          value={draft.timingStrictness}
          options={[
            { value: 'flexible', label: t('projects:create.settings.timingFlexible') },
            { value: 'balanced', label: t('projects:create.settings.timingBalanced') },
            { value: 'strict', label: t('projects:create.settings.timingStrict') },
          ]}
          onChange={(e) => {
            onChange({ timingStrictness: e.target.value });
          }}
        />
        <p className="dp-muted">{t('projects:create.settings.timingHelp')}</p>
      </Section>
      <Section title={t('projects:create.settings.voice')}>
        <Select
          label={t('projects:create.settings.voice')}
          data-testid="wizard-voice"
          value={draft.voicePolicy}
          options={[
            { value: 'matched', label: t('projects:create.settings.voiceMatched') },
            { value: 'consistent', label: t('projects:create.settings.voiceConsistent') },
            { value: 'expressive', label: t('projects:create.settings.voiceExpressive') },
          ]}
          onChange={(e) => {
            onChange({ voicePolicy: e.target.value });
          }}
        />
        <p className="dp-muted">{t('projects:create.settings.voiceHelp')}</p>
      </Section>
      <Section title={t('projects:create.settings.threshold')}>
        <Input
          label={t('projects:create.settings.threshold')}
          data-testid="wizard-threshold"
          type="number"
          min={0}
          max={1}
          step={0.05}
          hint={t('projects:create.settings.thresholdHelp')}
          error={errors['reviewThreshold']}
          value={String(draft.reviewThreshold)}
          onChange={(e) => {
            const parsed = Number.parseFloat(e.target.value);
            onChange({ reviewThreshold: Number.isFinite(parsed) ? parsed : 0 });
          }}
        />
      </Section>
      <Section title={t('projects:create.settings.glossary')}>
        <p className="dp-muted">{t('projects:create.settings.glossaryHelp')}</p>
        {draft.glossary.length === 0 ? (
          <p className="dp-muted" data-testid="wizard-glossary-empty">
            {t('projects:create.settings.glossaryEmpty')}
          </p>
        ) : null}
        {errors['glossary'] ? (
          <p className="dp-error" role="alert" data-testid="wizard-glossary-error">
            {errors['glossary']}
          </p>
        ) : null}
        <ol data-testid="wizard-glossary-list">
          {draft.glossary.map((entry, index) => (
            <li key={index} data-testid={`wizard-glossary-row-${index}`}>
              <Input
                label={t('projects:create.settings.glossarySource')}
                data-testid={`wizard-glossary-source-${index}`}
                value={entry.sourceTerm}
                maxLength={256}
                error={errors[`glossary.${index}.sourceTerm`]}
                onChange={(e) => {
                  const next = draft.glossary.map((row, rowIndex) =>
                    rowIndex === index ? { ...row, sourceTerm: e.target.value } : row,
                  );
                  onGlossaryChange(next);
                }}
              />
              <Input
                label={t('projects:create.settings.glossaryTarget')}
                data-testid={`wizard-glossary-target-${index}`}
                value={entry.targetTerm}
                maxLength={256}
                error={errors[`glossary.${index}.targetTerm`]}
                onChange={(e) => {
                  const next = draft.glossary.map((row, rowIndex) =>
                    rowIndex === index ? { ...row, targetTerm: e.target.value } : row,
                  );
                  onGlossaryChange(next);
                }}
              />
              <Input
                label={t('projects:create.settings.glossaryNotes')}
                data-testid={`wizard-glossary-notes-${index}`}
                value={entry.notes}
                maxLength={1024}
                onChange={(e) => {
                  const next = draft.glossary.map((row, rowIndex) =>
                    rowIndex === index ? { ...row, notes: e.target.value } : row,
                  );
                  onGlossaryChange(next);
                }}
              />
              <button
                type="button"
                data-testid={`wizard-glossary-remove-${index}`}
                className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
                onClick={() => {
                  onGlossaryChange(draft.glossary.filter((_, rowIndex) => rowIndex !== index));
                }}
              >
                {t('projects:create.settings.glossaryRemove')}
              </button>
            </li>
          ))}
        </ol>
        <button
          type="button"
          data-testid="wizard-glossary-add"
          className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
          onClick={() => {
            onGlossaryChange([...draft.glossary, { sourceTerm: '', targetTerm: '', notes: '' }]);
          }}
        >
          {t('projects:create.settings.glossaryAdd')}
        </button>
      </Section>
      <Section title={t('projects:create.settings.style')}>
        <Textarea
          label={t('projects:create.settings.style')}
          data-testid="wizard-style"
          placeholder={t('projects:create.settings.stylePlaceholder')}
          error={errors['styleInstructions']}
          value={draft.styleInstructions}
          maxLength={4000}
          onChange={(e) => {
            onChange({ styleInstructions: e.target.value });
          }}
        />
        <p className="dp-muted">{t('projects:create.settings.styleHelp')}</p>
      </Section>
    </div>
  );
}
