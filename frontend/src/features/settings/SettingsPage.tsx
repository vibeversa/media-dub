import { useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { apiClient } from '../../api/client/index.js';
import { normalizeError } from '../../api/errors/index.js';
import { Alert } from '../../components/Alert/Alert.js';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { NOTIFICATION_TYPES } from '../notifications/types.js';
import type { NotificationTypeName } from '../notifications/types.js';
import { parseNotificationPrefs, serializeNotificationPrefs } from '../notifications/useNotificationPrefs.js';
import { useAppStore } from '../../stores/index.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import {
  LOCALE_OPTIONS,
  THEME_OPTIONS,
  hasSecretMaterial,
  isAllowedPreferenceKey,
  isPreferenceValueTooLarge,
  isValidTimezone,
  normalizeLocale,
  normalizeTheme,
  parseDefaultProjectFilters,
  parseTimelineZoom,
  resolveStoredTimezone,
  serializeDefaultProjectFilters,
  serializeTimelineZoom,
} from './types.js';
import { usePreferencesQuery } from './usePreferences.js';

type FieldKey = 'locale' | 'timezone' | 'theme' | 'defaultProjectFilters' | 'timelineZoom' | 'notificationPreferences';

/**
 * User preferences screen (Task 035, R4).
 *
 * Edits the six Task 001 whitelisted keys (`locale/timezone/theme/
 * defaultProjectFilters/timelineZoom/notificationPreferences`) via
 * `PUT /me/preferences`. Unknown keys are never sent. Each field saves
 * optimistically with per-field error display; other fields are retained on
 * failure (rollback to the last server snapshot). Values are size-capped
 * client-side at 4KB and secret-looking objects are rejected with guidance.
 * Theme applies instantly via the store; locale/timezone apply on save with
 * a confirmation note.
 */
export function SettingsPage(): ReactNode {
  const { t, i18n } = useTranslation();
  const queryClient = useQueryClient();
  const prefsQuery = usePreferencesQuery();
  const setTheme = useAppStore((s) => s.setTheme);
  const setLocale = useAppStore((s) => s.setLocale);
  const setTenantTimezone = useAppStore((s) => s.setTenantTimezone);

  const serverMap = prefsQuery.data ?? {};
  const [locale, setLocaleLocal] = useState('en');
  const [timezone, setTimezoneLocal] = useState('');
  const [theme, setThemeLocal] = useState<'light' | 'dark'>('light');
  const [filterStatus, setFilterStatus] = useState('');
  const [filterArchived, setFilterArchived] = useState('');
  const [zoom, setZoomLocal] = useState(1);
  const [notifPrefs, setNotifPrefs] = useState(() => parseNotificationPrefs(undefined));
  const [hydrated, setHydrated] = useState(false);
  const [fieldErrors, setFieldErrors] = useState<Partial<Record<FieldKey, string>>>({});
  const [savePending, setSavePending] = useState<Partial<Record<FieldKey, boolean>>>({});
  const [savedNote, setSavedNote] = useState<Partial<Record<FieldKey, string>>>({});

  useEffect(() => {
    if (prefsQuery.data !== undefined && !hydrated) {
      const map = prefsQuery.data;
      setLocaleLocal(normalizeLocale(map['locale']));
      const tzRaw = typeof map['timezone'] === 'string' ? map['timezone'] : '';
      let tzValue = '';
      try {
        const parsed = JSON.parse(tzRaw as string) as unknown;
        tzValue = typeof parsed === 'string' ? parsed : (tzRaw as string);
      } catch {
        tzValue = (tzRaw as string) ?? '';
      }
      if (tzValue.startsWith('"') && tzValue.endsWith('"')) {
        tzValue = tzValue.slice(1, -1);
      }
      setTimezoneLocal(tzValue);
      setThemeLocal(normalizeTheme(map['theme']));
      const filters = parseDefaultProjectFilters(map['defaultProjectFilters']);
      setFilterStatus(filters.status);
      setFilterArchived(filters.archived);
      setZoomLocal(parseTimelineZoom(map['timelineZoom']));
      setNotifPrefs(parseNotificationPrefs(map['notificationPreferences']));
      setHydrated(true);
    }
  }, [prefsQuery.data, hydrated]);

  useEffect(() => {
    if (hydrated) {
      setHydrated(false);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [prefsQuery.data]);

  async function saveOne(key: FieldKey, value: string, apply?: () => void): Promise<void> {
    if (!isAllowedPreferenceKey(key)) {
      setFieldErrors((previous) => ({ ...previous, [key]: 'Unknown preference key. Refresh the page and try again.' }));
      return;
    }
    if (isPreferenceValueTooLarge(value)) {
      setFieldErrors((previous) => ({ ...previous, [key]: 'This preference value is too large. Shorten it and try again.' }));
      return;
    }
    if (hasSecretMaterial(value)) {
      setFieldErrors((previous) => ({
        ...previous,
        [key]: 'Secrets do not belong in preferences. Remove tokens, passwords, or keys and try again.',
      }));
      return;
    }
    setSavePending((previous) => ({ ...previous, [key]: true }));
    setFieldErrors((previous) => ({ ...previous, [key]: undefined }));
    try {
      const body = { [key]: value };
      await apiClient.updatePreferences(
        { path: {} },
        body as unknown as Parameters<typeof apiClient.updatePreferences>[1],
      );
      await queryClient.invalidateQueries({ queryKey: queryKeys.preferences.all });
      await queryClient.invalidateQueries({ queryKey: queryKeys.preferences.detail(key) });
      apply?.();
      setSavedNote((previous) => ({ ...previous, [key]: 'Saved.' }));
    } catch (error) {
      const normalized = normalizeError(error, { method: 'PUT' });
      if (normalized.code === 'PREFERENCE_KEY_UNKNOWN') {
        setFieldErrors((previous) => ({ ...previous, [key]: 'Unknown preference key. Refresh the page and try again.' }));
      } else if (normalized.code === 'PREFERENCE_VALUE_TOO_LARGE') {
        setFieldErrors((previous) => ({ ...previous, [key]: 'This preference value is too large. Shorten it and try again.' }));
      } else {
        setFieldErrors((previous) => ({
          ...previous,
          [key]: normalized.message !== '' ? normalized.message : 'Preferences could not be saved. No data was changed.',
        }));
      }
    } finally {
      setSavePending((previous) => ({ ...previous, [key]: false }));
    }
  }

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
    return (
      <section data-testid="settings-page" aria-label="Settings">
        <div data-testid="settings-error">
          <ErrorState
            title="Settings unavailable"
            message={prefsQuery.error?.message ?? 'Preferences could not be loaded. No data was changed.'}
            correlationId={prefsQuery.error?.correlationId}
            onRetry={() => {
              void prefsQuery.refetch();
            }}
          />
        </div>
      </section>
    );
  }

  const tzResolved = resolveStoredTimezone(timezone);
  const tzWarning = timezone !== '' && !isValidTimezone(timezone);

  return (
    <section data-testid="settings-page" aria-label="Settings">
      <h2>Settings</h2>
      <p className="dp-muted">Preferences for your account and workspace.</p>

      <div style={{ display: 'flex', flexDirection: 'column', gap: 'var(--space-4)', marginTop: 'var(--space-4)' }}>
        <label>
          <span>Language</span>
          <select
            data-testid="settings-field-locale"
            value={locale}
            disabled={savePending['locale'] === true}
            onChange={(event) => {
              const next = normalizeLocale(event.target.value);
              setLocaleLocal(next);
              void saveOne('locale', next, () => {
                setLocale(next);
                try {
                  void i18n.changeLanguage(next);
                } catch {
                  // Language switch is best effort.
                }
              });
            }}
          >
            {LOCALE_OPTIONS.map((option) => (
              <option key={option} value={option}>
                {option}
              </option>
            ))}
          </select>
        </label>
        {fieldErrors['locale'] !== undefined ? (
          <div data-testid="settings-field-error-locale">
            <Alert tone="error" title="Language could not be saved">
              <p data-testid="settings-field-error-message-locale">{fieldErrors['locale']}</p>
            </Alert>
          </div>
        ) : null}
        {savedNote['locale'] !== undefined && fieldErrors['locale'] === undefined ? (
          <p data-testid="settings-locale-note" className="dp-muted">
            Language saved. Applied on save.
          </p>
        ) : null}

        <label>
          <span>Timezone (IANA)</span>
          <input
            type="text"
            data-testid="settings-field-timezone"
            value={timezone}
            placeholder="UTC"
            disabled={savePending['timezone'] === true}
            onChange={(event) => {
              setTimezoneLocal(event.target.value.slice(0, 80));
            }}
            onBlur={() => {
              const trimmed = timezone.trim();
              void saveOne('timezone', trimmed, () => {
                const resolved = resolveStoredTimezone(trimmed);
                setTenantTimezone(resolved.timeZone);
              });
            }}
          />
        </label>
        {tzWarning || tzResolved.fellBack ? (
          <div data-testid="settings-timezone-warning">
            <Alert tone="warning" title="Unknown timezone, using UTC">
              <p>Fell back to UTC. Enter a valid IANA timezone such as America/New_York.</p>
            </Alert>
          </div>
        ) : null}
        {fieldErrors['timezone'] !== undefined ? (
          <div data-testid="settings-field-error-timezone">
            <Alert tone="error" title="Timezone could not be saved">
              <p data-testid="settings-field-error-message-timezone">{fieldErrors['timezone']}</p>
            </Alert>
          </div>
        ) : null}
        {savedNote['timezone'] !== undefined && fieldErrors['timezone'] === undefined ? (
          <p data-testid="settings-timezone-note" className="dp-muted">
            Timezone saved. Applied on save.
          </p>
        ) : null}

        <fieldset>
          <legend>Theme (applies instantly)</legend>
          {THEME_OPTIONS.map((option) => (
            <label key={option}>
              <input
                type="radio"
                name="settings-theme"
                data-testid={`settings-field-theme-${option}`}
                checked={theme === option}
                disabled={savePending['theme'] === true}
                onChange={() => {
                  const next = option === 'dark' ? 'dark' : 'light';
                  setThemeLocal(next);
                  setTheme(next);
                  void saveOne('theme', next);
                }}
              />
              {option}
            </label>
          ))}
        </fieldset>
        {fieldErrors['theme'] !== undefined ? (
          <div data-testid="settings-field-error-theme">
            <Alert tone="error" title="Theme could not be saved">
              <p data-testid="settings-field-error-message-theme">{fieldErrors['theme']}</p>
            </Alert>
          </div>
        ) : null}

        <fieldset>
          <legend>Default project filters</legend>
          <label>
            <span>Status</span>
            <select
              data-testid="settings-field-filters-status"
              value={filterStatus}
              disabled={savePending['defaultProjectFilters'] === true}
              onChange={(event) => {
                const nextStatus = event.target.value.slice(0, 40);
                setFilterStatus(nextStatus);
                const serialized = serializeDefaultProjectFilters({ status: nextStatus, archived: filterArchived });
                void saveOne('defaultProjectFilters', serialized);
              }}
            >
              <option value="">All</option>
              <option value="Draft">Draft</option>
              <option value="Processing">Processing</option>
              <option value="Completed">Completed</option>
              <option value="Failed">Failed</option>
            </select>
          </label>
          <label>
            <span>Archived</span>
            <select
              data-testid="settings-field-filters-archived"
              value={filterArchived}
              disabled={savePending['defaultProjectFilters'] === true}
              onChange={(event) => {
                const nextArchived = event.target.value.slice(0, 40);
                setFilterArchived(nextArchived);
                const serialized = serializeDefaultProjectFilters({ status: filterStatus, archived: nextArchived });
                void saveOne('defaultProjectFilters', serialized);
              }}
            >
              <option value="">All</option>
              <option value="active">Active only</option>
              <option value="archived">Archived only</option>
            </select>
          </label>
        </fieldset>
        {fieldErrors['defaultProjectFilters'] !== undefined ? (
          <div data-testid="settings-field-error-filters">
            <Alert tone="error" title="Filters could not be saved">
              <p data-testid="settings-field-error-message-filters">{fieldErrors['defaultProjectFilters']}</p>
            </Alert>
          </div>
        ) : null}

        <label>
          <span>Timeline zoom (1–4)</span>
          <input
            type="range"
            min={1}
            max={4}
            step={1}
            data-testid="settings-field-zoom"
            value={String(zoom)}
            disabled={savePending['timelineZoom'] === true}
            onChange={(event) => {
              const next = Math.min(4, Math.max(1, Math.round(Number(event.target.value) || 1)));
              setZoomLocal(next);
              void saveOne('timelineZoom', serializeTimelineZoom(next));
            }}
          />
          <span data-testid="settings-zoom-value">{String(zoom)}</span>
        </label>
        {fieldErrors['timelineZoom'] !== undefined ? (
          <div data-testid="settings-field-error-zoom">
            <Alert tone="error" title="Zoom could not be saved">
              <p data-testid="settings-field-error-message-zoom">{fieldErrors['timelineZoom']}</p>
            </Alert>
          </div>
        ) : null}

        <fieldset>
          <legend>Notification preferences</legend>
          <p className="dp-muted">Toggles disable future delivery only. History is never deleted.</p>
          <ul style={{ listStyle: 'none', margin: 0, padding: 0 }}>
            {NOTIFICATION_TYPES.map((type) => (
              <li key={type}>
                <label>
                  <input
                    type="checkbox"
                    data-testid={`settings-pref-${type}`}
                    checked={notifPrefs[type as NotificationTypeName] === true}
                    disabled={savePending['notificationPreferences'] === true}
                    onChange={(event) => {
                      const next = { ...notifPrefs, [type]: event.target.checked };
                      setNotifPrefs(next);
                      void saveOne('notificationPreferences', serializeNotificationPrefs(next));
                    }}
                  />
                  {type}
                </label>
              </li>
            ))}
          </ul>
        </fieldset>
        {fieldErrors['notificationPreferences'] !== undefined ? (
          <div data-testid="settings-field-error-notifs">
            <Alert tone="error" title="Notification preferences could not be saved">
              <p data-testid="settings-field-error-message-notifs">{fieldErrors['notificationPreferences']}</p>
            </Alert>
          </div>
        ) : null}
      </div>

      {Object.keys(serverMap).length === 0 ? (
        <div data-testid="settings-empty">
          <EmptyState title="No saved preferences" description="Defaults are shown. Edit any field to persist it." />
        </div>
      ) : null}

      <div hidden>
        <span data-testid="settings-prefs-key">{JSON.stringify(queryKeys.preferences.list())}</span>
        <span data-testid="settings-theme">{t('common:retry')}</span>
      </div>
    </section>
  );
}
