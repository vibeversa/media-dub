import type { ReactNode } from 'react';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { PreferencesForm } from './PreferencesForm.js';
import { usePreferencesQuery } from './usePreferences.js';
import { useTranslation } from 'react-i18next';

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
    const { t } = useTranslation();
const prefsQuery = usePreferencesQuery();

  if (prefsQuery.isPending && prefsQuery.data === undefined) {
    return (
      <section data-testid="settings-page" aria-label={t('settings:settingsPage.settings')}>
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
      <section data-testid="settings-page" aria-label={t('settings:settingsPage.settings2')}>
        <div data-testid="settings-error">
          <ErrorState
            title={isForbidden ? 'Preferences unavailable' : 'Settings unavailable'}
            message={
              isForbidden
                ? 'You do not have permission to view these preferences. Preferences are scoped to your account; contact your tenant admin.'
                : (prefsQuery.error?.message ?? t('settings:settingsPage.preferences-could-not-be-loaded-no'))
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
    <section data-testid="settings-page" aria-label={t('settings:settingsPage.settings3')}>
      <h2>{t('settings:settingsPage.settings4')}</h2>
      <p className="dp-muted">{t('settings:settingsPage.preferences-for-your-account-and-workspace')}</p>
      <PreferencesForm
        serverMap={serverMap}
        refetch={async () => {
          await prefsQuery.refetch();
        }}
        isFetching={prefsQuery.isFetching}
      />
      {Object.keys(serverMap).length === 0 ? (
        <div data-testid="settings-empty">
          <EmptyState title={t('settings:settingsPage.no-saved-preferences')} description={t('settings:settingsPage.defaults-are-shown-edit-any-field')} />
        </div>
      ) : null}
    </section>
  );
}
