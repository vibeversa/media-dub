import { useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Alert } from '../../components/Alert/Alert.js';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { useToast } from '../../components/Toast/useToast.js';
import {
  EXPORT_FORMAT_ALLOWLIST,
  EXPORT_SCOPE_ALLOWLIST,
  EXPORT_TYPE_ALLOWLIST,
  advertisedScopes,
  formatsForType,
  profileForRequest,
  validateExportRequest,
} from './types.js';
import type { OutputView } from './types.js';
import { invalidateExports, useCreateExport, useProcessingRuns } from './useOutputs.js';

export interface ExportCardProps {
  readonly projectId: string;
  readonly output: OutputView | undefined;
  /** Override for the format allowlist (tests simulate backend misconfig with `[]`). */
  readonly availableFormats?: readonly string[];
  /** Override for the type allowlist (defaults to the backend-mirrored list). */
  readonly availableTypes?: readonly string[];
}

const LATEST_RUN_VALUE = '';

/**
 * Export request dialog (Task 033).
 *
 * Offers only backend-allowlisted `type`/`scope`/`format` values (imported
 * allowlists — no hardcoded options in this module) plus the backend run
 * list for the run selector. Scopes render only where advertised
 * (`advertisedScopes(output)`); formats render only from the allowlist
 * filtered by type. Parameters validate against the allowlists before any
 * network call; the server remains authoritative. Submit posts
 * `{ format, profile?, allowPartial? }` (`profile` is the normalized
 * `type`/`type-scope` kebab); the selected run is informational (the server
 * exports the latest eligible run). Success invalidates `queryKeys.exports`
 * so the generation row appears; 409 collapses to the existing row with a
 * toast and no duplicate request. An empty format allowlist (backend
 * misconfig) shows an `EmptyState` with a support hint and hides submit.
 */
export function ExportCard({ projectId, output, availableFormats, availableTypes }: ExportCardProps): ReactNode {
  const { push } = useToast();
  const queryClient = useQueryClient();
  const createMutation = useCreateExport(projectId);
  const runsQuery = useProcessingRuns(projectId);

  const typeAllowlist = availableTypes ?? EXPORT_TYPE_ALLOWLIST;
  const formatAllowlist = availableFormats ?? EXPORT_FORMAT_ALLOWLIST;
  const advertised = useMemo(() => advertisedScopes(output), [output]);
  const scopeAllowlist = useMemo(
    () => EXPORT_SCOPE_ALLOWLIST.filter((scope) => advertised.includes(scope)),
    [advertised],
  );
  const runs = useMemo(() => runsQuery.data ?? [], [runsQuery.data]);
  const runIds = useMemo(() => runs.map((run) => run.id), [runs]);

  const [open, setOpen] = useState(false);
  const [selectedType, setSelectedType] = useState<string>(typeAllowlist[0] ?? '');
  const [selectedScope, setSelectedScope] = useState<string>(scopeAllowlist[0] ?? '');
  const [selectedRun, setSelectedRun] = useState<string>(LATEST_RUN_VALUE);
  const [selectedFormat, setSelectedFormat] = useState<string>(formatAllowlist[0] ?? '');
  const [allowPartial, setAllowPartial] = useState(false);
  const [submitError, setSubmitError] = useState<string | null>(null);

  useEffect(() => {
    if (!typeAllowlist.includes(selectedType)) {
      setSelectedType(typeAllowlist[0] ?? '');
    }
  }, [typeAllowlist, selectedType]);

  useEffect(() => {
    if (!scopeAllowlist.includes(selectedScope)) {
      setSelectedScope(scopeAllowlist[0] ?? '');
    }
  }, [scopeAllowlist, selectedScope]);

  const offeredFormats = useMemo(() => {
    const byType = selectedType === '' ? [] : formatsForType(selectedType);
    return byType.filter((format) => formatAllowlist.includes(format));
  }, [selectedType, formatAllowlist]);

  useEffect(() => {
    if (!offeredFormats.includes(selectedFormat)) {
      setSelectedFormat(offeredFormats[0] ?? '');
    }
  }, [offeredFormats, selectedFormat]);

  function close(): void {
    setOpen(false);
    setSubmitError(null);
  }

  async function handleSubmit(event: React.FormEvent): Promise<void> {
    event.preventDefault();
    setSubmitError(null);
    const validation = validateExportRequest({
      type: selectedType,
      scope: selectedScope,
      runId: selectedRun,
      format: selectedFormat,
      allowPartial,
      advertisedScopes: advertised,
      availableRunIds: runIds,
    });
    if ('error' in validation) {
      setSubmitError(validation.error);
      return;
    }
    try {
      await createMutation.mutateAsync({
        format: validation.body.format,
        profile: validation.body.profile ?? profileForRequest(selectedType, selectedScope),
        allowPartial: validation.body.allowPartial,
      });
      await invalidateExports(queryClient, projectId);
      push('success', 'Export requested.');
      close();
    } catch (error) {
      const appError = error as { code?: string; status?: number; message?: string; correlationId?: string };
      if (appError.status === 409) {
        push('info', 'Export already generating. Showing the existing job.');
        await invalidateExports(queryClient, projectId);
        close();
        return;
      }
      setSubmitError(appError.message ?? 'Export request failed. No data was changed.');
    }
  }

  if (!open) {
    return (
      <div data-testid="export-card">
        <button
          type="button"
          data-testid="export-open-dialog"
          className="dp-btn dp-btn-primary dp-btn-md dp-focus-ring"
          onClick={() => {
            setOpen(true);
            setSubmitError(null);
          }}
        >
          Request export
        </button>
      </div>
    );
  }

  const formatsEmpty = offeredFormats.length === 0 || formatAllowlist.length === 0;

  return (
    <div data-testid="export-card">
      <button
        type="button"
        data-testid="export-open-dialog"
        className="dp-btn dp-btn-primary dp-btn-md dp-focus-ring"
        onClick={() => {
          setOpen(true);
          setSubmitError(null);
        }}
      >
        Request export
      </button>
      <div data-testid="export-dialog" role="dialog" aria-label="Request export">
        {formatsEmpty ? (
          <div data-testid="export-empty-formats">
            <EmptyState
              title="No export formats available"
              description="The export catalog is empty. Contact support with the project id."
            />
            <button
              type="button"
              data-testid="export-cancel"
              className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
              onClick={close}
            >
              Close
            </button>
          </div>
        ) : (
          <form
            data-testid="export-form"
            onSubmit={(event) => {
              void handleSubmit(event);
            }}
          >
            <label>
              Type
              <select
                data-testid="export-type"
                value={selectedType}
                onChange={(event) => {
                  setSelectedType(event.target.value);
                }}
              >
                {typeAllowlist.map((type) => (
                  <option key={type} value={type} data-testid={`export-type-option-${type}`}>
                    {type}
                  </option>
                ))}
              </select>
            </label>
            <label>
              Scope
              <select
                data-testid="export-scope"
                value={selectedScope}
                onChange={(event) => {
                  setSelectedScope(event.target.value);
                }}
              >
                {scopeAllowlist.map((scope) => (
                  <option key={scope} value={scope} data-testid={`export-scope-option-${scope}`}>
                    {scope}
                  </option>
                ))}
              </select>
            </label>
            <label>
              Run
              <select
                data-testid="export-run"
                value={selectedRun}
                onChange={(event) => {
                  setSelectedRun(event.target.value);
                }}
              >
                <option value={LATEST_RUN_VALUE} data-testid="export-run-option-latest">
                  Latest run
                </option>
                {runs.map((run) => (
                  <option key={run.id} value={run.id} data-testid={`export-run-option-${run.id}`}>
                    {run.id}
                  </option>
                ))}
              </select>
            </label>
            {runsQuery.isError ? (
              <p data-testid="export-runs-error" className="dp-muted">
                {runsQuery.error?.message ?? 'Runs could not be loaded. Latest run will be used.'}
              </p>
            ) : null}
            <label>
              Format
              <select
                data-testid="export-format"
                value={selectedFormat}
                onChange={(event) => {
                  setSelectedFormat(event.target.value);
                }}
              >
                {offeredFormats.map((format) => (
                  <option key={format} value={format} data-testid={`export-format-option-${format}`}>
                    {format}
                  </option>
                ))}
              </select>
            </label>
            <label>
              <input
                type="checkbox"
                data-testid="export-allow-partial"
                checked={allowPartial}
                onChange={(event) => {
                  setAllowPartial(event.target.checked);
                }}
              />
              Allow partial export
            </label>
            {submitError !== null ? (
              <div data-testid="export-error">
                <Alert tone="error" title="Export request failed">
                  <p data-testid="export-error-message">{submitError}</p>
                </Alert>
              </div>
            ) : null}
            <div style={{ display: 'flex', gap: 'var(--space-2)' }}>
              <button
                type="submit"
                data-testid="export-submit"
                className="dp-btn dp-btn-primary dp-btn-md dp-focus-ring"
                disabled={createMutation.isPending}
              >
                Submit export
              </button>
              <button
                type="button"
                data-testid="export-cancel"
                className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
                onClick={close}
              >
                Cancel
              </button>
            </div>
          </form>
        )}
      </div>
    </div>
  );
}
