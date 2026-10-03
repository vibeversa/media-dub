import { useState } from 'react';
import type { ReactNode } from 'react';
import { Badge } from '../../components/Badge/Badge.js';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { StatusBadge } from '../../components/StatusBadge/StatusBadge.js';
import { useFeatureFlag } from '../../hooks/useFeatureFlag.js';
import { canAccessAdmin } from './adminGuard.js';
import { LocalGpuNotAvailableState, LocalGpuPrivacyNote, LocalGpuUnavailableState, LocalGpuUnknownState } from './LocalGpuStates.js';
import { isAdminForbiddenError, isAdminUnknownRouteError } from './types.js';
import { useLocalGpuHealth } from './useLocalGpuQueries.js';
import { useAppStore } from '../../stores/index.js';
import { useTranslation } from 'react-i18next';

/**
 * Operator-only local-GPU health (Task 044, §19.3, R4).
 *
 * RESTRICTED TWICE, ON PURPOSE
 * ----------------------------
 * Once on the client: it lives inside the Task 036 Admin section, behind
 * `useAdminGuard`, AND it re-checks `canAccessAdmin` itself rather than
 * trusting its position in the tree. Placement is a rendering decision and can
 * be changed by a later refactor; a permission check cannot. The task's
 * security section asks for the elevated Admin role specifically, so the
 * ordinary-user case renders a permission state — not the panel with its data
 * stripped, which would confirm that a device exists.
 *
 * Once on the server: `GET /admin/local-gpu` is admin-gated, so a caller who
 * reaches this panel without the grant gets a 403 and NOTHING renders. What is
 * on this panel — accelerator model, model revision, device count, per-device
 * latency — is infrastructure inventory. It is not secret, and it is equally
 * not appropriate for an ordinary project member to read.
 *
 * UNKNOWN IS NOT HEALTHY
 * ----------------------
 * An unreachable endpoint renders `UnknownState`, never "Healthy". An operator
 * panel whose failure mode reads as good news is worse than no panel, and that
 * is the exact hazard Task 043's runbooks keep warning about with dashboards
 * that stopped reporting.
 */

/** Parsed status → shared badge tone. Text carries the meaning too (041C). */
const STATUS_BADGE: Readonly<Record<string, 'success' | 'warning' | 'error' | 'neutral'>> = Object.freeze({
  Healthy: 'success',
  Degraded: 'warning',
  Down: 'error',
  Unknown: 'neutral',
});

export interface LocalGpuPanelProps {
  /**
   * Overrides the flag read. `AdminPage` decides whether the section exists
   * with the shared hook and mounts this panel inside it; the panel re-reads the
   * same flag itself, so a future refactor that moved the section out from under
   * that check would not expose device detail. Omitted by the panel's own specs,
   * which mount it directly.
   */
  readonly enabled?: boolean;
}

export function LocalGpuPanel({ enabled }: LocalGpuPanelProps = {}): ReactNode {
    const { t } = useTranslation();
const permissions = useAppStore((s) => s.permissions);
  const sessionStatus = useAppStore((s) => s.sessionStatus);
  const localInference = useFeatureFlag('localInference');
  const [dismissed, setDismissed] = useState(false);

  const flagOn = enabled ?? localInference;
  // `sessionStatus` matters as well as permissions: a permission list that
  // survived a logout must not keep an operator panel open.
  const allowed = sessionStatus === 'authenticated' && canAccessAdmin(permissions);

  if (flagOn !== true) {
    // R1: zero chrome. Not a disabled section, not a "coming soon" note.
    return null;
  }

  if (!allowed) {
    return (
      <section data-testid="admin-local-gpu" aria-label={t('admin:localGpuPanel.local-gpu-health')}>
        <h3>{t('admin:localGpuPanel.local-gpu-health2')}</h3>
        <div data-testid="admin-local-gpu-forbidden">
          <EmptyState
            title={t('admin:localGpuPanel.local-gpu-health-unavailable')}
            description={t('admin:localGpuPanel.you-do-not-have-permission-to')}
          />
        </div>
      </section>
    );
  }

  return (
    <section data-testid="admin-local-gpu" aria-label={t('admin:localGpuPanel.local-gpu-health3')}>
      <h3>{t('admin:localGpuPanel.local-gpu-health4')}</h3>
      <LocalGpuBody dismissed={dismissed} onDismiss={() => setDismissed(true)} onUndismiss={() => setDismissed(false)} />
    </section>
  );
}

interface LocalGpuBodyProps {
  readonly dismissed: boolean;
  readonly onDismiss: () => void;
  readonly onUndismiss: () => void;
}

function LocalGpuBody({ dismissed, onDismiss, onUndismiss }: LocalGpuBodyProps): ReactNode {
    const { t } = useTranslation();
const query = useLocalGpuHealth(true);

  if (query.isPending && query.data === undefined) {
    return (
      <div data-testid="admin-local-gpu-loading">
        <Skeleton lines={3} />
      </div>
    );
  }

  if (query.data === undefined) {
    const error = query.error;
    if (isAdminForbiddenError(error)) {
      // No detail of any kind: a 403 must not confirm whether a device exists.
      return (
        <div data-testid="admin-local-gpu-forbidden">
          <EmptyState
            title={t('admin:localGpuPanel.local-gpu-health-unavailable2')}
            description={t('admin:localGpuPanel.you-do-not-have-permission-to2')}
          />
        </div>
      );
    }
    if (isAdminUnknownRouteError(error)) {
      // `AdminController`'s catch-all answers 404 `ADMIN_ROUTE_UNKNOWN`: the
      // deployment has not provisioned local inference. Not an error, and
      // emphatically not "no device".
      return <LocalGpuNotAvailableState />;
    }
    if (dismissed) {
      return null;
    }
    return (
      <LocalGpuUnavailableState
        message={error?.message ?? t('admin:localGpuPanel.device-health-could-not-be-read')}
        correlationId={error?.correlationId}
        onRetry={() => {
          onUndismiss();
          void query.refetch();
        }}
        retryPending={query.isFetching}
        onDismiss={onDismiss}
      />
    );
  }

  const view = query.data;
  if (view.empty) {
    return <LocalGpuUnknownState detail="The endpoint responded without a recognisable device summary." />;
  }

  return (
    <>
      <div data-testid="admin-local-gpu-summary">
        <p data-testid="admin-local-gpu-status">
          <StatusBadge status={view.status} />{' '}
          <span data-testid="admin-local-gpu-status-badge">
            <Badge tone={STATUS_BADGE[view.status] ?? 'neutral'}>{view.status}</Badge>
          </span>
        </p>
        <dl>
          <dt>{t('admin:localGpuPanel.provider')}</dt>
          <dd data-testid="admin-local-gpu-provider">{view.provider === '' ? '—' : view.provider}</dd>
          <dt>{t('admin:localGpuPanel.model')}</dt>
          <dd data-testid="admin-local-gpu-model">{view.model === '' ? '—' : view.model}</dd>
          <dt>{t('admin:localGpuPanel.model-version')}</dt>
          <dd data-testid="admin-local-gpu-model-version">{view.modelVersion === '' ? '—' : view.modelVersion}</dd>
          <dt>{t('admin:localGpuPanel.device')}</dt>
          <dd data-testid="admin-local-gpu-device">{view.device === '' ? '—' : view.device}</dd>
          <dt>{t('admin:localGpuPanel.devices')}</dt>
          <dd data-testid="admin-local-gpu-device-count">{view.deviceCount === undefined ? '—' : String(view.deviceCount)}</dd>
          <dt>{t('admin:localGpuPanel.latency-p95')}</dt>
          <dd data-testid="admin-local-gpu-latency">{view.latencyMsP95 === undefined ? '—' : `${String(view.latencyMsP95)} ms`}</dd>
          <dt>{t('admin:localGpuPanel.last-success')}</dt>
          <dd data-testid="admin-local-gpu-last-success">{view.lastSuccessAt === '' ? '—' : view.lastSuccessAt}</dd>
        </dl>
      </div>
      <LocalGpuPrivacyNote />
    </>
  );
}