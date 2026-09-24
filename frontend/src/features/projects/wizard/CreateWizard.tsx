import { useState } from 'react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { useAppMutation } from '../../../api/hooks.js';
import { apiClient } from '../../../api/client/index.js';
import type { Project, ProjectCreateRequest } from '../../../api/client/index.js';
import { queryKeys } from '../../../api/queryKeys/index.js';
import { queryClient } from '../../../app/providers/queryClient.js';
import { ConfirmDialog } from '../../../components/ConfirmDialog/ConfirmDialog.js';
import { Alert } from '../../../components/Alert/Alert.js';
import { useToast } from '../../../components/Toast/useToast.js';
import { BasicsStep } from './BasicsStep.js';
import { LanguageStep } from './LanguageStep.js';
import { ReviewStep } from './ReviewStep.js';
import { SettingsStep } from './SettingsStep.js';
import { UploadStep } from './UploadStep.js';
import { WIZARD_STEPS, buildCreatePayload } from './draft.js';
import type { WizardStep } from './draft.js';
import { mapServerErrors } from './serverErrors.js';
import { validateBasics, validateLanguages, validateSettings } from './validation.js';
import type { FieldErrors } from './validation.js';
import { useWizardStore } from './wizardStore.js';

/**
 * Project creation wizard (Task 022), mounted at `/projects/new`.
 *
 * Flow `basics → language → settings → upload → review → start` with free
 * backward navigation and validated forward motion. Client validation
 * (zod) blocks Next/Start with inline errors; server 400s map onto the same
 * fields with the draft preserved. Start posts once via `useAppMutation`
 * (idempotent) then clears the draft and opens the workspace — the media
 * tab when the upload was deferred, the overview otherwise. Discard asks
 * for confirmation before clearing. Authentication expiry mid-wizard
 * redirects through login while the `localStorage` draft restores the exact
 * step on return.
 */
export function CreateWizard(): ReactNode {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { push } = useToast();
  const draft = useWizardStore((s) => s.draft);
  const step = useWizardStore((s) => s.step);
  const uploadFile = useWizardStore((s) => s.uploadFile);
  const updateDraft = useWizardStore((s) => s.updateDraft);
  const setStep = useWizardStore((s) => s.setStep);
  const replaceGlossary = useWizardStore((s) => s.replaceGlossary);
  const setUploadFile = useWizardStore((s) => s.setUploadFile);
  const clearWizard = useWizardStore((s) => s.clearWizard);
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({});
  const [serverFields, setServerFields] = useState<FieldErrors>({});
  const [serverForm, setServerForm] = useState<string | null>(null);
  const [discardOpen, setDiscardOpen] = useState(false);

  const createMutation = useAppMutation<Project, ProjectCreateRequest>({
    mutationFn: (body, ctx) =>
      apiClient.createProject({ path: {} }, body, { idempotencyKey: ctx.idempotencyKey }),
  });

  function errorsFor(current: WizardStep): FieldErrors {
    switch (current) {
      case 'basics':
        return validateBasics({ name: draft.name, description: draft.description });
      case 'language':
        return validateLanguages({ sourceLanguage: draft.sourceLanguage, targetLanguage: draft.targetLanguage });
      case 'settings':
        return validateSettings({
          sourceSeparationPolicy: draft.sourceSeparationPolicy,
          outputProfile: draft.outputProfile,
          timingStrictness: draft.timingStrictness,
          voicePolicy: draft.voicePolicy,
          reviewThreshold: draft.reviewThreshold,
          glossary: [...draft.glossary],
          styleInstructions: draft.styleInstructions,
        });
      case 'upload':
        if (draft.uploadMode === 'now' && uploadFile === null) {
          return { uploadFile: t('projects:create.upload.missingFile') };
        }
        return {};
      case 'review':
        return {};
    }
  }

  /** First step with client errors, if any (checked in flow order). */
  function firstFailingStep(): { step: WizardStep; errors: FieldErrors } | null {
    const ordered: readonly WizardStep[] = ['basics', 'language', 'settings', 'upload'];
    for (const candidate of ordered) {
      const errors = errorsFor(candidate);
      if (Object.keys(errors).length > 0) {
        return { step: candidate, errors };
      }
    }
    return null;
  }

  function goTo(next: WizardStep): void {
    setFieldErrors({});
    setStep(next);
  }

  function handleNext(): void {
    const errors = errorsFor(step);
    if (Object.keys(errors).length > 0) {
      setFieldErrors(errors);
      return;
    }
    setFieldErrors({});
    const index = WIZARD_STEPS.indexOf(step);
    const next = WIZARD_STEPS[index + 1];
    if (next !== undefined) {
      setStep(next);
    }
  }

  function handleBack(): void {
    const index = WIZARD_STEPS.indexOf(step);
    const prev = WIZARD_STEPS[index - 1];
    if (prev !== undefined) {
      goTo(prev);
    }
  }

  function refreshCaches(): void {
    void queryClient.invalidateQueries({ queryKey: queryKeys.projects.lists() });
    void queryClient.invalidateQueries({ queryKey: queryKeys.dashboard.summary() });
  }

  function handleStart(): void {
    const failing = firstFailingStep();
    if (failing !== null) {
      setFieldErrors(failing.errors);
      setStep(failing.step);
      return;
    }
    setFieldErrors({});
    setServerFields({});
    setServerForm(null);
    createMutation.mutate(buildCreatePayload(draft), {
      onSuccess: (project) => {
        const deferred = draft.uploadMode === 'later';
        push('success', t('projects:toasts.created'));
        clearWizard();
        refreshCaches();
        navigate(deferred ? `/projects/${project.id}/media` : `/projects/${project.id}`);
      },
      onError: (error) => {
        const mapped = mapServerErrors(error.details, error.message, error.correlationId);
        setServerFields(mapped.fields);
        setServerForm(mapped.form);
        if (Object.keys(mapped.fields).length === 0) {
          push('error', mapped.form ?? error.message);
        }
      },
    });
  }

  const index = WIZARD_STEPS.indexOf(step);
  const isFirst = index <= 0;
  const isReview = step === 'review';
  const pending = createMutation.isPending;

  let body: ReactNode;
  switch (step) {
    case 'basics':
      body = <BasicsStep draft={draft} errors={fieldErrors} onChange={updateDraft} />;
      break;
    case 'language':
      body = <LanguageStep draft={draft} errors={fieldErrors} onChange={updateDraft} />;
      break;
    case 'settings':
      body = (
        <SettingsStep draft={draft} errors={fieldErrors} onChange={updateDraft} onGlossaryChange={replaceGlossary} />
      );
      break;
    case 'upload':
      body = (
        <UploadStep
          draft={draft}
          errors={fieldErrors}
          attachedFileName={uploadFile?.name ?? null}
          onModeChange={(mode) => {
            updateDraft({ uploadMode: mode });
          }}
          onFileSelect={setUploadFile}
        />
      );
      break;
    case 'review':
      body = (
        <ReviewStep
          draft={draft}
          serverFields={{ ...fieldErrors, ...serverFields }}
          serverForm={serverForm}
          onEditStep={goTo}
        />
      );
      break;
  }

  return (
    <section data-testid="page-project-create">
      <h1 className="text-xl font-semibold">{t('projects:create.title')}</h1>
      <p className="mt-2 text-sm text-slate-600">{t('projects:create.subtitle')}</p>
      <ol data-testid="wizard-steps" aria-label={t('projects:create.title')}>
        {WIZARD_STEPS.map((entry, entryIndex) => (
          <li key={entry} data-testid={`wizard-nav-${entry}`}>
            <button
              type="button"
              data-testid={`wizard-goto-${entry}`}
              className="dp-btn dp-btn-ghost dp-btn-sm dp-focus-ring"
              aria-current={entry === step ? 'step' : undefined}
              onClick={() => {
                goTo(entry);
              }}
            >
              {entryIndex + 1}. {t(`projects:create.steps.${entry}`)}
            </button>
          </li>
        ))}
      </ol>
      <p role="status" data-testid="wizard-progress">
        {t('projects:create.step', { current: String(index + 1), total: String(WIZARD_STEPS.length) })}
      </p>
      {Object.keys(fieldErrors).length > 0 && !isReview ? (
        <div data-testid="wizard-field-errors">
          <Alert tone="error" title={t('projects:create.errors.fixFields')} />
        </div>
      ) : null}
      {body}
      <div data-testid="wizard-nav">
        {!isFirst ? (
          <button
            type="button"
            data-testid="wizard-back"
            className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
            disabled={pending}
            onClick={handleBack}
          >
            {t('projects:create.actions.back')}
          </button>
        ) : null}
        {!isReview ? (
          <button
            type="button"
            data-testid="wizard-next"
            className="dp-btn dp-btn-primary dp-btn-md dp-focus-ring"
            disabled={pending}
            onClick={handleNext}
          >
            {t('projects:create.actions.next')}
          </button>
        ) : (
          <button
            type="button"
            data-testid="wizard-start"
            className="dp-btn dp-btn-primary dp-btn-md dp-focus-ring"
            disabled={pending}
            onClick={handleStart}
          >
            {pending ? t('projects:create.actions.starting') : t('projects:create.actions.start')}
          </button>
        )}
        <button
          type="button"
          data-testid="wizard-discard"
          className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
          disabled={pending}
          onClick={() => {
            setDiscardOpen(true);
          }}
        >
          {t('projects:create.actions.discard')}
        </button>
      </div>
      <ConfirmDialog
        open={discardOpen}
        title={t('projects:create.actions.discardTitle')}
        description={t('projects:create.actions.discardDescription')}
        confirmLabel={t('projects:create.actions.discardConfirm')}
        onCancel={() => {
          setDiscardOpen(false);
        }}
        onConfirm={() => {
          setDiscardOpen(false);
          clearWizard();
          setFieldErrors({});
          setServerFields({});
          setServerForm(null);
          navigate('/projects');
        }}
      />
    </section>
  );
}
