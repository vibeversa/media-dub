import { useEffect, useRef, useState } from 'react';
import type { ChangeEvent, ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { useAppMutation } from '../../api/hooks.js';
import { apiClient, newIdempotencyKey } from '../../api/client/index.js';
import type { ProcessingRun, ProcessingStartRequest } from '../../api/client/index.js';
import { normalizeError } from '../../api/errors/index.js';
import type { AppError } from '../../api/errors/index.js';
import { Alert } from '../../components/Alert/Alert.js';
import { Input } from '../../components/Input/Input.js';
import { Modal } from '../../components/Modal/Modal.js';
import { formatDate, formatNumber } from '../../i18n/format.js';
import { useAuthStore } from '../auth/authStore.js';
import { CostEstimateCard } from './CostEstimateCard.js';
import { confirmBlocks, loadPreflight } from './usePreflight.js';
import type { ConfirmBlock, PreflightSnapshot } from './usePreflight.js';

export interface PreflightDialogProps {
  readonly projectId: string;
  readonly onClose: () => void;
  /** Called with the new run id after a 202 (or idempotent 200 replay). */
  readonly onStarted: (runId: string) => void;
}

type LoadPhase = 'loading' | 'ready' | 'loadError';

interface ConflictInfo {
  readonly runId: string | undefined;
  readonly status: string | undefined;
}

/**
 * Processing-start preflight dialog (Task 024).
 *
 * Hosts mount this conditionally per open (fresh idempotency key per mount,
 * stable across in-dialog retries so error-retries replay idempotently).
 * There is no direct-start code path: the only `startProcessing` call in the
 * frontend lives in this dialog's confirm handler (asserted by the R1 source
 * scan in `__tests__/noDirectStart.test.ts`), and it fires only after the
 * confirm button enables (estimate loaded + consent acknowledged + quota
 * override when exhausted).
 *
 * Conflict handling (R5): a pre-start active run, or a 409
 * `RUN_ALREADY_ACTIVE` from the start call, switches the dialog to the
 * conflict state — start stays visible but disabled with the reason plus a
 * workspace link (never silently enabled, never hidden). A 429
 * `QUOTA_EXCEEDED` renders the over-budget panel; other failures render an
 * inline error with the correlation reference. The server stays authoritative
 * for media readiness and archival: those render as warnings, never as
 * client-side blocks.
 */
export function PreflightDialog({ projectId, onClose, onStarted }: PreflightDialogProps): ReactNode {
  const { t } = useTranslation();
  const userId = useAuthStore((s) => s.userId);
  const [phase, setPhase] = useState<LoadPhase>('loading');
  const [snapshot, setSnapshot] = useState<PreflightSnapshot | null>(null);
  const [loadError, setLoadError] = useState<AppError | null>(null);
  const [reloadToken, setReloadToken] = useState(0);
  const [consentAcknowledged, setConsentAcknowledged] = useState(false);
  const [consentMeta, setConsentMeta] = useState<{ user: string; at: string } | null>(null);
  const [overrideReason, setOverrideReason] = useState('');
  const [conflictAfterStart, setConflictAfterStart] = useState<ConflictInfo | null>(null);
  const [startError, setStartError] = useState<AppError | null>(null);
  const [quotaBlocked, setQuotaBlocked] = useState<AppError | null>(null);
  const [idempotencyKey] = useState<string>(() => newIdempotencyKey());
  const submittedRef = useRef(false);

  useEffect(() => {
    let cancelled = false;
    async function load(): Promise<void> {
      setPhase('loading');
      setLoadError(null);
      try {
        const loaded = await loadPreflight(projectId);
        if (!cancelled) {
          setSnapshot(loaded);
          setPhase('ready');
        }
      } catch (error) {
        if (!cancelled) {
          setLoadError(normalizeError(error, { method: 'GET' }));
          setPhase('loadError');
        }
      }
    }
    void load();
    return () => {
      cancelled = true;
    };
  }, [projectId, reloadToken]);

  const startMutation = useAppMutation<ProcessingRun, ProcessingStartRequest | undefined>({
    mutationFn: (body, ctx) =>
      apiClient.startProcessing({ path: { projectId } }, body, { idempotencyKey: ctx.idempotencyKey }),
    idempotencyKey,
  });

  const blocks: ConfirmBlock[] =
    snapshot === null
      ? []
      : confirmBlocks(snapshot, {
          consentAcknowledged,
          overrideReason,
          starting: startMutation.isPending,
          inConflict: conflictAfterStart !== null || snapshot.activeRun !== null,
        });
  const confirmDisabled = phase !== 'ready' || snapshot === null || blocks.length > 0;
  const amount = snapshot === null ? '' : formatNumber(snapshot.estimate.amountUsd, { style: 'currency', currency: snapshot.currency });

  function handleConsentChange(event: ChangeEvent<HTMLInputElement>): void {
    const checked = event.target.checked;
    setConsentAcknowledged(checked);
    setConsentMeta(checked ? { user: userId ?? 'unknown', at: new Date().toISOString() } : null);
  }

  function handleConfirm(): void {
    if (snapshot === null || submittedRef.current || startMutation.isPending) {
      return;
    }
    if (
      confirmBlocks(snapshot, {
        consentAcknowledged,
        overrideReason,
        starting: false,
        inConflict: conflictAfterStart !== null || snapshot.activeRun !== null,
      }).length > 0
    ) {
      return;
    }
    submittedRef.current = true;
    setStartError(null);
    setQuotaBlocked(null);
    const body: ProcessingStartRequest | undefined =
      snapshot.configHash === undefined ? undefined : { configHash: snapshot.configHash };
    startMutation.mutate(body, {
      onSuccess: (run) => {
        onStarted(run.runId);
      },
      onError: (error) => {
        submittedRef.current = false;
        if (error.code === 'RUN_ALREADY_ACTIVE') {
          setConflictAfterStart({ runId: undefined, status: undefined });
        } else if (error.code === 'QUOTA_EXCEEDED') {
          setQuotaBlocked(error);
        } else {
          setStartError(error);
        }
      },
    });
  }

  let body: ReactNode;
  if (phase === 'loading') {
    body = (
      <p role="status" data-testid="preflight-loading">
        {t('processing:loading')}
      </p>
    );
  } else if (phase === 'loadError') {
    body = (
      <div data-testid="preflight-load-error">
        <Alert
          tone="error"
          title={t('processing:loadError.title')}
          details={loadError?.correlationId !== undefined && loadError.correlationId !== '' ? `${loadError.message}\n${t('processing:startError.reportRef', { ref: loadError.correlationId })}` : loadError?.message}
        >
          <p>{t('processing:loadError.message')}</p>
        </Alert>
        <button
          type="button"
          data-testid="preflight-retry"
          className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
          onClick={() => {
            setReloadToken((token) => token + 1);
          }}
        >
          {t('processing:loadError.retry')}
        </button>
      </div>
    );
  } else if (snapshot !== null) {
    const conflictRun = conflictAfterStart ?? snapshot.activeRun;
    body = (
      <div>
        {snapshot.isArchived ? (
          <div data-testid="preflight-archived-warning">
            <Alert tone="warning" title={t('processing:status.archivedTitle')}>
              <p>{t('processing:status.archived')}</p>
            </Alert>
          </div>
        ) : null}
        {!snapshot.isArchived && snapshot.status !== 'MediaReady' ? (
          <div data-testid="preflight-status-warning">
            <Alert tone="warning" title={t('processing:status.warningTitle')}>
              <p>{t('processing:status.warning', { status: snapshot.status })}</p>
            </Alert>
          </div>
        ) : null}
        <CostEstimateCard
          estimate={snapshot.estimate}
          currency={snapshot.currency}
          monthToDate={snapshot.monthToDate}
          quota={snapshot.quota}
          configHash={snapshot.configHash}
        />
        <p className="dp-muted">{t('processing:estimate.timeNote')}</p>
        <section data-testid="preflight-config" aria-label={t('processing:config.title')}>
          <h3>{t('processing:config.title')}</h3>
          <dl>
            <div>
              <dt>{t('processing:config.status')}</dt>
              <dd data-testid="preflight-config-status">{snapshot.status}</dd>
            </div>
            <div>
              <dt>{t('processing:config.languages')}</dt>
              <dd>{`${snapshot.sourceLanguage ?? '—'} → ${snapshot.targetLanguage ?? '—'}`}</dd>
            </div>
            <div>
              <dt>{t('processing:config.speakers')}</dt>
              <dd data-testid="preflight-speaker-count">{String(snapshot.speakerTotal)}</dd>
            </div>
          </dl>
          {snapshot.speakersTruncated ? (
            <p className="dp-muted" data-testid="preflight-speakers-truncated">
              {t('processing:config.speakersTruncated', { shown: String(snapshot.clonedVoices.length), total: String(snapshot.speakerTotal) })}
            </p>
          ) : null}
        </section>
        {snapshot.clonedVoices.length > 0 ? (
          <section data-testid="preflight-consent" aria-label={t('processing:consent.title')}>
            <h3>{t('processing:consent.title')}</h3>
            <p>{t('processing:consent.description')}</p>
            <ul data-testid="preflight-consent-voices">
              {snapshot.clonedVoices.map((voice) => (
                <li key={`${voice.speakerId}:${voice.voiceId}`} data-testid={`preflight-consent-voice-${voice.speakerId}`}>
                  {voice.displayName} — {voice.voiceId}
                </li>
              ))}
            </ul>
            <label htmlFor="preflight-consent-check">{t('processing:consent.check')}</label>
            <input id="preflight-consent-check" data-testid="preflight-consent-check" type="checkbox" checked={consentAcknowledged} onChange={handleConsentChange} />
            {consentMeta !== null ? (
              <p className="dp-muted" data-testid="preflight-consent-ack">
                {t('processing:consent.acknowledged', { user: consentMeta.user, at: formatDate(consentMeta.at) })}
              </p>
            ) : null}
          </section>
        ) : null}
        {snapshot.quota.remaining <= 0 ? (
          <div data-testid="preflight-quota-exhausted">
            <Alert tone="warning" title={t('processing:quota.exhaustedTitle')}>
              <p>{t('processing:quota.exhausted')}</p>
            </Alert>
            <Input
              label={t('processing:quota.overrideLabel')}
              hint={t('processing:quota.overrideHint')}
              data-testid="preflight-override-input"
              value={overrideReason}
              maxLength={500}
              autoComplete="off"
              onChange={(event) => {
                setOverrideReason(event.target.value);
              }}
            />
          </div>
        ) : null}
        {conflictRun !== null ? (
          <div data-testid="preflight-conflict">
            <Alert tone="error" title={t('processing:conflict.title')}>
              <p>{t('processing:conflict.message', { status: conflictRun.status ?? 'active' })}</p>
              <Link to={`/projects/${projectId}`} data-testid="preflight-conflict-workspace">
                {t('processing:actions.openWorkspace')}
              </Link>
            </Alert>
          </div>
        ) : null}
        {quotaBlocked !== null ? (
          <div data-testid="preflight-quota-blocked">
            <Alert
              tone="error"
              title={t('processing:quota.blockedTitle')}
              details={quotaBlocked.correlationId !== '' ? t('processing:startError.reportRef', { ref: quotaBlocked.correlationId }) : undefined}
            >
              <p>{t('processing:quota.blocked')}</p>
            </Alert>
          </div>
        ) : null}
        {startError !== null ? (
          <div data-testid="preflight-start-error">
            <Alert
              tone="error"
              title={t('processing:startError.title')}
              details={startError.correlationId !== '' ? `${startError.message}\n${t('processing:startError.reportRef', { ref: startError.correlationId })}` : startError.message}
            />
          </div>
        ) : null}
        <div data-testid="preflight-controls">
          <button
            type="button"
            data-testid="preflight-confirm"
            className="dp-btn dp-btn-primary dp-btn-md dp-focus-ring"
            disabled={confirmDisabled}
            onClick={handleConfirm}
          >
            {t('processing:actions.start', { amount })}
          </button>
          <ul data-testid="preflight-blocks">
            {blocks.map((block) => (
              <li key={block} data-testid={`preflight-block-${block}`}>
                {t(`processing:blocks.${block}`)}
              </li>
            ))}
          </ul>
          <button
            type="button"
            data-testid="preflight-cancel"
            className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
            onClick={onClose}
          >
            {t('processing:actions.cancel')}
          </button>
        </div>
      </div>
    );
  }

  return (
    <Modal open title={t('processing:title')} onClose={onClose}>
      <div data-testid="preflight-dialog">
        <p className="dp-muted">{t('processing:subtitle')}</p>
        {/* Scroll container: quota/consent/conflict panels can push the
            confirm below the fold on short viewports; the shared Modal
            primitive stays untouched (Task 018). */}
        <div style={{ maxBlockSize: '70vh', overflowY: 'auto' }}>{body}</div>
      </div>
    </Modal>
  );
}
