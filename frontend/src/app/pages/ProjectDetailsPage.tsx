import { useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams, useSearchParams } from 'react-router-dom';
import { useToast } from '../../components/Toast/useToast.js';
import { useAppStore } from '../../stores/index.js';
import { PreflightDialog } from '../../features/processing/PreflightDialog.js';

/**
 * Project workspace shell (Task 024 adds the processing-start entry; the full
 * workspace lands in Task 025). The Start button is permission-hinted on
 * `processing.start` and always opens the preflight dialog — there is no
 * direct-start path (R1). A `?started=1` landing is silently canonicalized:
 * the host that performed the 202 already pushed the "Run started" toast, so
 * this page only strips the param (no second toast, no re-toast on refresh).
 */
export default function ProjectDetailsPage(): ReactNode {
  const { t } = useTranslation();
  const params = useParams();
  const [searchParams, setSearchParams] = useSearchParams();
  const { push } = useToast();
  const permissions = useAppStore((s) => s.permissions);
  const [preflightOpen, setPreflightOpen] = useState(false);

  const projectId = params['id'] ?? '';
  const canStart = permissions.includes('processing.start');

  useEffect(() => {
    if (searchParams.get('started') === '1') {
      setSearchParams({}, { replace: true });
    }
  }, [searchParams, setSearchParams]);

  return (
    <section data-testid="page-project-details">
      <h1 className="text-xl font-semibold">Project</h1>
      <p className="mt-2 text-sm text-slate-600">Project ID: {projectId === '' ? 'unknown' : projectId}</p>
      {canStart && projectId !== '' ? (
        <button
          type="button"
          data-testid="project-start-processing"
          className="dp-btn dp-btn-primary dp-btn-md dp-focus-ring"
          onClick={() => {
            setPreflightOpen(true);
          }}
        >
          {t('processing:title')}
        </button>
      ) : null}
      {preflightOpen && projectId !== '' ? (
        <PreflightDialog
          projectId={projectId}
          onClose={() => {
            setPreflightOpen(false);
          }}
          onStarted={() => {
            setPreflightOpen(false);
            push('success', t('processing:toasts.started'));
            setSearchParams({ started: '1' });
          }}
        />
      ) : null}
    </section>
  );
}
