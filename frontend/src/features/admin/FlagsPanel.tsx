import { useState } from 'react';
import type { ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Alert } from '../../components/Alert/Alert.js';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { useToast } from '../../components/Toast/useToast.js';
import { DestructiveAction } from './DestructiveAction.js';
import { applyFeatureFlags, invalidateAdminQueries, setFeatureFlagEnabled, useAdminFlags } from './useAdminQueries.js';
import { isAdminForbiddenError, isAdminFreezeError } from './types.js';

/**
 * Feature-flag panel (Task 036).
 *
 * Reads `GET /admin/feature-flags` on `queryKeys.admin.section('flags')`.
 * Safe toggles apply immediately and reversibly (optimistic flip, revert on
 * failure); a rollout freeze (backend 423, or a frozen flag) opens a dialog
 * explaining the freeze and reverts the toggle. Destructive flag-apply
 * (rollout commit) is gated behind the shared destructive flow with confirm
 * + reason + permission + audit receipt.
 */
export function FlagsPanel(): ReactNode {
  const queryClient = useQueryClient();
  const { push } = useToast();
  const flagsQuery = useAdminFlags();
  const [pendingKeys, setPendingKeys] = useState<ReadonlySet<string>>(() => new Set());
  const [optimistic, setOptimistic] = useState<Readonly<Record<string, boolean>>>(() => ({}));
  const [freezeDialog, setFreezeDialog] = useState<string | undefined>(undefined);

  if (flagsQuery.isPending && flagsQuery.data === undefined) {
    return (
      <section data-testid="admin-flags" aria-label="Feature flags">
        <div data-testid="admin-flags-loading">
          <Skeleton lines={3} />
        </div>
      </section>
    );
  }

  if (flagsQuery.isError && flagsQuery.data === undefined) {
    if (isAdminForbiddenError(flagsQuery.error)) {
      return (
        <section data-testid="admin-flags" aria-label="Feature flags">
          <div data-testid="admin-flags-forbidden">
            <EmptyState
              title="Flags unavailable"
              description="You do not have permission to view feature flags. Contact your tenant admin for access."
            />
          </div>
        </section>
      );
    }
    return (
      <section data-testid="admin-flags" aria-label="Feature flags">
        <div data-testid="admin-flags-error">
          <ErrorState
            title="Flags unavailable"
            message={flagsQuery.error?.message ?? 'Feature flags could not be loaded. No data was changed.'}
            correlationId={flagsQuery.error?.correlationId}
            onRetry={() => {
              void flagsQuery.refetch();
            }}
          />
        </div>
      </section>
    );
  }

  const flags = flagsQuery.data?.items ?? [];
  const notProvisioned = flagsQuery.data?.notProvisioned === true;
  if (flags.length === 0) {
    return (
      <section data-testid="admin-flags" aria-label="Feature flags">
        <div data-testid="admin-flags-empty">
          <EmptyState
            title="No feature flags"
            description={notProvisioned ? 'Flag reads are not provisioned on this backend yet.' : 'No feature flags are defined.'}
          />
        </div>
      </section>
    );
  }

  function effectiveEnabled(key: string, server: boolean): boolean {
    return optimistic[key] ?? server;
  }

  async function toggle(key: string, server: boolean, frozen: boolean): Promise<void> {
    if (pendingKeys.has(key)) {
      return;
    }
    if (frozen) {
      setFreezeDialog(key);
      return;
    }
    const next = !effectiveEnabled(key, server);
    setPendingKeys((previous) => new Set(previous).add(key));
    setOptimistic((previous) => ({ ...previous, [key]: next }));
    try {
      await setFeatureFlagEnabled(key, next);
      await invalidateAdminQueries(queryClient);
      push('success', `Flag ${key} ${next ? 'enabled' : 'disabled'}.`);
    } catch (error) {
      // Revert the optimistic flip on any failure; freezes get an explainer.
      setOptimistic((previous) => {
        const copy = { ...previous };
        delete copy[key];
        return copy;
      });
      if (isAdminFreezeError(error)) {
        setFreezeDialog(key);
      } else {
        push('error', error instanceof Error && error.message !== '' ? error.message : `Flag ${key} could not be updated.`);
      }
    } finally {
      setPendingKeys((previous) => {
        const copy = new Set(previous);
        copy.delete(key);
        return copy;
      });
    }
  }

  return (
    <section data-testid="admin-flags" aria-label="Feature flags">
      <h3>Feature flags</h3>
      <ul data-testid="admin-flags-list">
        {flags.map((flag) => {
          const enabled = effectiveEnabled(flag.key, flag.enabled);
          return (
            <li key={flag.key} data-testid={`admin-flag-row-${flag.key}`}>
              <span data-testid={`admin-flag-name-${flag.key}`}>{flag.key}</span>{' '}
              {flag.description !== '' ? <span data-testid={`admin-flag-desc-${flag.key}`} className="dp-muted">{flag.description}</span> : null}
              {flag.frozen ? (
                <span data-testid={`admin-flag-frozen-${flag.key}`} className="dp-muted">
                  Frozen
                </span>
              ) : null}
              <button
                type="button"
                role="switch"
                aria-checked={enabled}
                data-testid={`admin-flag-toggle-${flag.key}`}
                className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
                disabled={pendingKeys.has(flag.key)}
                onClick={() => {
                  void toggle(flag.key, flag.enabled, flag.frozen);
                }}
              >
                {enabled ? 'On' : 'Off'}
              </button>
            </li>
          );
        })}
      </ul>
      {freezeDialog !== undefined ? (
        <div role="dialog" aria-modal="true" aria-label="Rollout freeze" data-testid="admin-flag-freeze-dialog">
          <Alert tone="warning" title="Rollout freeze">
            <p data-testid="admin-flag-freeze-text">
              Flag {freezeDialog} cannot change during the rollout freeze. The toggle was reverted; try again after the freeze lifts.
            </p>
            <button
              type="button"
              data-testid="admin-flag-freeze-close"
              className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
              onClick={() => {
                setFreezeDialog(undefined);
              }}
            >
              Understood
            </button>
          </Alert>
        </div>
      ) : null}
      <div data-testid="admin-flags-apply">
        <DestructiveAction
          action="flags.apply"
          label="Apply flag rollout"
          confirmToken="APPLY-FLAGS"
          testId="admin-flags-apply"
          description="Commits the current flag set as the rollout. This is a destructive, audited rollout step."
          onConfirm={(reason) => applyFeatureFlags(reason)}
        />
      </div>
    </section>
  );
}
