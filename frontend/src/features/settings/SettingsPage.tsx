import type { ReactNode } from 'react';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { PreferencesForm } from './PreferencesForm.js';
import { usePreferencesQuery } from './usePreferences.js';

/**
 * User preferences screen (Task 035B).
 *
 * Loads the six Task 006 whitelisted keys (`locale/timezone/theme/
 * defaultProjectFilters/timelineZoom/notificationPreferences`) via
 * `GET /me/preferences` and delegates editing to `PreferencesForm`
 * (draft + dirty guard + optimistic save with rollback). Unknown keys are
 * never sent. Values are size-capped at 4KB and secret-looking objects are
 * rejected. Theme/locale/timezone apply without reload through the 045
 * helpers on save. Preference changes surface in activity only where the
 * backend projects them (no direct activity writes here).
 */
export function SettingsPage(): ReactNode {
  const prefsQuery = usePreferencesQuery();

  if (prefsQuery.isPending && prefsQuery.data === undefined) {
    return (
      <section data-testid="settings-page" aria-label="Settings">
        <div data-testid="settings-loading">
          <Skeleton lines={6} />
        </div>
      </section>
    );
  }

  if (prefsQuery.isError && prefsQuery.data === undefined) {
    const status = prefsQuery.error?.status ?? 0;
    const code = prefsQuery.error?.code ?? '';
    const isForbidden = status === 403 || code === 'FORBIDDEN' || code === 'USER_DISABLED';
    return (
      <section data-testid="settings-page" aria-label="Settings">
        <div data-testid="settings-error">
          <ErrorState
            title={isForbidden ? 'Preferences unavailable' : 'Settings unavailable'}
            message={
              isForbidden
                ? 'You do not have permission to view these preferences. Preferences are scoped to your account; contact your tenant admin.'
                : (prefsQuery.error?.message ?? 'Preferences could not be loaded. No data was changed.')
            }
            correlationId={prefsQuery.error?.correlationId}
            onRetry={() => {
              void prefsQuery.refetch();
            }}
          />
        </div>
      </section>
    );
  }

  const serverMap = prefsQuery.data ?? {};

  return (
    <section data-testid="settings-page" aria-label="Settings">
      <h2>Settings</h2>
      <p className="dp-muted">Preferences for your account and workspace.</p>
      <PreferencesForm
        serverMap={serverMap}
        refetch={async () => {
          await prefsQuery.refetch();
        }}
        isFetching={prefsQuery.isFetching}
      />
      {Object.keys(serverMap).length === 0 ? (
        <div data-testid="settings-empty">
          <EmptyState title="No saved preferences" description="Defaults are shown. Edit any field to persist it." />
        </div>
      ) : null}
    </section>
  );
}
