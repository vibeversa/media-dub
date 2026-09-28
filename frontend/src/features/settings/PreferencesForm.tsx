import { useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { apiClient } from '../../api/client/index.js';
import { normalizeError } from '../../api/errors/index.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { Alert } from '../../components/Alert/Alert.js';
import { useToast } from '../../components/Toast/useToast.js';
import i18n from '../../i18n/i18n.js';
import { applyDirection } from '../../i18n/format.js';
import { useAppStore } from '../../stores/index.js';
import { NOTIFICATION_TYPES } from '../notifications/types.js';
import type { NotificationTypeName } from '../notifications/types.js';
import { parseNotificationPrefs, serializeNotificationPrefs } from '../notifications/useNotificationPrefs.js';
import {
  LOCALE_OPTIONS,
  NOTIFICATION_CHANNELS,
  THEME_OPTIONS,
  TIMEZONE_OPTIONS,
  buildPreferencePayload,
  collectDirtyKeys,
  draftFromServerMap,
  fieldErrorsFromDetails,
  hasSecretMaterial,
  isAllowedPreferenceKey,
  isDraftDirty,
  isPreferenceConflictError,
  isPreferenceForbiddenError,
  isPreferenceOfflineError,
  isPreferenceValueTooLarge,
  isValidTimezone,
  normalizeLocale,
  normalizeThemePreference,
  normalizeTimezoneOption,
  resolveEffectiveTheme,
  resolveStoredTimezone,
} from './types.js';
import type { PreferenceDraft, PreferenceKey, PreferenceMap, ThemePreference } from './types.js';

export interface PreferencesFormProps {
  readonly serverMap: PreferenceMap;
  readonly refetch: () => Promise<unknown>;
  readonly isFetching?: boolean;
}

type FieldKey = PreferenceKey;

/**
 * Preferences editors (Task 035B).
 *
 * Draft-based form over the six Task 006 whitelisted keys. Edits update the
 * local draft only; `Save` persists dirty keys via `PUT /me/preferences`
 * with optimistic cache + store application and rollback on failure.
 * Only dirty keys are sent (per-key last-write-wins, never silent
 * cross-key overwrite). Unknown keys are never sent. Values are size-capped
 * at 4KB and secret-looking objects are rejected client-side.
 * Locale/timezone/theme apply immediately through the 045 helpers on save
 * (no reload): locale via store + i18next + `document.dir/lang`, timezone
 * via the tenant slice, theme via the resolved light/dark store value.
 */
export function PreferencesForm({ serverMap, refetch, isFetching = false }: PreferencesFormProps): ReactNode {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const { push } = useToast();
  const setTheme = useAppStore((s) => s.setTheme);
  const setLocale = useAppStore((s) => s.setLocale);
  const setTenantTimezone = useAppStore((s) => s.setTenantTimezone);

  const [baseline, setBaseline] = useState<PreferenceMap>(serverMap);
  const [draft, setDraft] = useState<PreferenceDraft>(() => draftFromServerMap(serverMap));
  const [notifPrefs, setNotifPrefs] = useState(() => parseNotificationPrefs(serverMap['notificationPreferences']));
  const [fieldErrors, setFieldErrors] = useState<Partial<Record<FieldKey, string>>>({});
  const [savePending, setSavePending] = useState(false);
  const [savedNote, setSavedNote] = useState<Partial<Record<FieldKey, string>>>({});
  const [conflict, setConflict] = useState<string | null>(null);
  const [offlineQueued, setOfflineQueued] = useState(false);
  const [forbidden, setForbidden] = useState<string | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [pendingLeave, setPendingLeave] = useState<(() => void) | undefined>(undefined);
  const [pendingLabel, setPendingLabel] = useState<string | undefined>(undefined);
  const [online, setOnline] = useState(() =>
    typeof navigator !== 'undefined' && typeof navigator.onLine === 'boolean' ? navigator.onLine : true,
  );

  useEffect(() => {
    setBaseline(serverMap);
  }, [serverMap]);

  useEffect(() => {
    const onOnline = (): void => {
      setOnline(true);
    };
    const onOffline = (): void => {
      setOnline(false);
    };
    window.addEventListener('online', onOnline);
    window.addEventListener('offline', onOffline);
    return () => {
      window.removeEventListener('online', onOnline);
      window.removeEventListener('offline', onOffline);
    };
  }, []);

  useEffect(() => {
    if (!isDraftDirty(draft, baseline)) {
      return;
    }
    const handler = (event: BeforeUnloadEvent): void => {
      event.preventDefault();
    };
    window.addEventListener('beforeunload', handler);
    return () => {
      window.removeEventListener('beforeunload', handler);
    };
  }, [draft, baseline]);

  const dirtyKeys = useMemo(() => collectDirtyKeys(draft, baseline), [draft, baseline]);
  const isDirty = dirtyKeys.length > 0;

  const serverTzRaw = typeof baseline['timezone'] === 'string' ? baseline['timezone'] : '';
  const serverTzResolved = resolveStoredTimezone(
    (() => {
      try {
        const parsed = JSON.parse(serverTzRaw) as unknown;
        if (typeof parsed === 'string') {
          return parsed;
        }
      } catch {
        // Plain zone name.
      }
      const stripped = serverTzRaw.startsWith('"') && serverTzRaw.endsWith('"') ? serverTzRaw.slice(1, -1) : serverTzRaw;
      return stripped;
    })(),
  );
  const draftTzValid = draft.timezone === '' ? true : isValidTimezone(draft.timezone);
  const showTzWarning = !draftTzValid || serverTzResolved.fellBack;

  function applyPreferencesThroughHelpers(values: { locale: string; timezone: string; theme: ThemePreference }): void {
    const normalizedLocale = normalizeLocale(values.locale);
    setLocale(normalizedLocale);
    applyDirection(normalizedLocale);
    try {
      void i18n.changeLanguage(normalizedLocale);
    } catch {
      // Language switch is best effort.
    }
    const resolvedTz = resolveStoredTimezone(values.timezone);
    setTenantTimezone(resolvedTz.timeZone);
    setTheme(resolveEffectiveTheme(normalizeThemePreference(values.theme)));
  }

  function snapshotStore(): { locale: string; theme: 'light' | 'dark'; timezone: string | undefined } {
    const state = useAppStore.getState();
    return { locale: state.locale, theme: state.theme, timezone: state.tenantTimezone };
  }

  function restoreStore(snapshot: { locale: string; theme: 'light' | 'dark'; timezone: string | undefined }): void {
    setLocale(snapshot.locale);
    applyDirection(snapshot.locale);
    try {
      void i18n.changeLanguage(snapshot.locale);
    } catch {
      // Best effort.
    }
    setTenantTimezone(snapshot.timezone);
    setTheme(snapshot.theme);
  }

  async function persistDraft(options?: { fromGuard?: boolean }): Promise<boolean> {
    if (savePending) {
      return false;
    }
    const payload = buildPreferencePayload(draft, baseline);
    const keys = Object.keys(payload) as FieldKey[];
    if (keys.length === 0) {
      return true;
    }
    for (const key of keys) {
      if (!isAllowedPreferenceKey(key)) {
        setFieldErrors((previous) => ({ ...previous, [key]: 'Unknown preference key. Refresh the page and try again.' }));
        return false;
      }
    }
    const nextErrors: Partial<Record<FieldKey, string>> = {};
    let hasClientError = false;
    for (const key of keys) {
      const value = payload[key] ?? '';
      if (isPreferenceValueTooLarge(value)) {
        nextErrors[key] = 'This preference value is too large. Shorten it and try again.';
        hasClientError = true;
      } else if (hasSecretMaterial(value)) {
        nextErrors[key] =
          'Secrets do not belong in preferences. Remove tokens, passwords, or keys and try again.';
        hasClientError = true;
      }
    }
    if (draft.timezone.trim() !== '' && !isValidTimezone(draft.timezone)) {
      nextErrors['timezone'] =
        'Unknown timezone. Enter a valid IANA timezone such as America/New_York, or use UTC.';
      hasClientError = true;
    }
    if (hasClientError) {
      setFieldErrors((previous) => ({ ...previous, ...nextErrors }));
      return false;
    }

    const previousCache = queryClient.getQueryData<PreferenceMap>(queryKeys.preferences.list());
    const previousStore = snapshotStore();
    const optimisticMap: PreferenceMap = { ...baseline, ...payload };
    queryClient.setQueryData(queryKeys.preferences.list(), optimisticMap);
    applyPreferencesThroughHelpers({ locale: draft.locale, timezone: draft.timezone, theme: draft.theme });

    setSavePending(true);
    setFieldErrors({});
    setSaveError(null);
    setConflict(null);
    setForbidden(null);
    setOfflineQueued(false);
    try {
      await apiClient.updatePreferences(
        { path: {} },
        payload as unknown as Parameters<typeof apiClient.updatePreferences>[1],
      );
      await queryClient.invalidateQueries({ queryKey: queryKeys.preferences.all });
      for (const key of keys) {
        await queryClient.invalidateQueries({ queryKey: queryKeys.preferences.detail(key) });
      }
      setBaseline((previous) => ({ ...previous, ...payload }));
      const notes: Partial<Record<FieldKey, string>> = {};
      for (const key of keys) {
        notes[key] = 'Saved.';
      }
      setSavedNote((previous) => ({ ...previous, ...notes }));
      setOfflineQueued(false);
      push('success', 'Preferences saved.');
      if (options?.fromGuard === true) {
        const next = pendingLeave;
        setPendingLeave(undefined);
        setPendingLabel(undefined);
        if (next !== undefined) {
          next();
        }
      }
      return true;
    } catch (error) {
      const normalized = normalizeError(error, { method: 'PUT' });
      queryClient.setQueryData(queryKeys.preferences.list(), previousCache ?? baseline);
      restoreStore(previousStore);
      const shape = {
        code: normalized.code,
        status: normalized.status,
        message: normalized.message,
        details: normalized.details,
      };
      if (isPreferenceConflictError(shape)) {
        setConflict(
          'Preferences changed elsewhere. The latest values were reloaded; your draft was kept. Review, then retry or use the server values.',
        );
        try {
          await queryClient.invalidateQueries({ queryKey: queryKeys.preferences.all });
          await refetch();
        } catch {
          // Refetch is best effort; the draft is already preserved.
        }
        return false;
      }
      if (isPreferenceOfflineError(shape) || online === false) {
        setOfflineQueued(true);
        setSaveError('You appear to be offline. Your draft was kept and queued — reconnect, then retry.');
        return false;
      }
      if (isPreferenceForbiddenError(shape)) {
        const message =
          'You do not have permission to edit these preferences. Preferences are scoped to your account; contact your tenant admin.';
        setForbidden(message);
        push('error', message);
        return false;
      }
      const detailFields = fieldErrorsFromDetails(normalized.details);
      if (Object.keys(detailFields).length > 0) {
        setFieldErrors(detailFields);
        return false;
      }
      if (normalized.code === 'PREFERENCE_KEY_UNKNOWN') {
        const target = keys[0] ?? 'locale';
        setFieldErrors({ [target]: 'Unknown preference key. Refresh the page and try again.' });
        return false;
      }
      if (normalized.code === 'PREFERENCE_VALUE_TOO_LARGE') {
        const target = keys[0] ?? 'locale';
        setFieldErrors({ [target]: 'This preference value is too large. Shorten it and try again.' });
        return false;
      }
      if (normalized.status === 400 || normalized.status === 413 || normalized.status === 422) {
        const target = keys[0] ?? 'locale';
        setFieldErrors({
          [target]: normalized.message !== '' ? normalized.message : 'Preferences could not be saved. No data was changed.',
        });
        return false;
      }
      setSaveError(normalized.message !== '' ? normalized.message : 'Preferences could not be saved. No data was changed.');
      return false;
    } finally {
      setSavePending(false);
    }
  }

  function handleDiscard(): void {
    const clean = draftFromServerMap(baseline);
    setDraft(clean);
    setNotifPrefs(parseNotificationPrefs(baseline['notificationPreferences']));
    setFieldErrors({});
    setSavedNote({});
    setConflict(null);
    setOfflineQueued(false);
    setForbidden(null);
    setSaveError(null);
    restoreStore({
      locale: normalizeLocale(baseline['locale']),
      theme: resolveEffectiveTheme(normalizeThemePreference(baseline['theme'])),
      timezone: resolveStoredTimezone(baseline['timezone']).timeZone,
    });
  }

  function requestLeave(next: () => void, label?: string): void {
    if (!isDirty) {
      next();
      return;
    }
    setPendingLeave(() => next);
    setPendingLabel(label);
  }

  function handleBack(): void {
    requestLeave(
      () => {
        navigate('/dashboard');
      },
      'dashboard',
    );
  }

  async function handleGuardSave(): Promise<void> {
    const ok = await persistDraft({ fromGuard: true });
    if (!ok) {
      setPendingLeave(undefined);
      setPendingLabel(undefined);
    }
  }

  function handleGuardDiscard(): void {
    handleDiscard();
    const next = pendingLeave;
    setPendingLeave(undefined);
    setPendingLabel(undefined);
    if (next !== undefined) {
      next();
    }
  }

  function handleGuardCancel(): void {
    setPendingLeave(undefined);
    setPendingLabel(undefined);
  }

  function handleConflictUseServer(): void {
    handleDiscard();
    setConflict(null);
    void refetch();
  }

  return (
    <div data-testid="preferences-form">
      {isDirty ? (
        <div data-testid="settings-dirty-bar">
          <Alert tone="warning" title="Unsaved changes">
            <p data-testid="settings-dirty-text">
              {dirtyKeys.length === 1
                ? `1 preference has unsaved changes (${dirtyKeys.join(', ')}).`
                : `${String(dirtyKeys.length)} preferences have unsaved changes (${dirtyKeys.join(', ')}).`}
            </p>
            <div style={{ display: 'flex', gap: '0.5rem' }}>
              <button
                type="button"
                data-testid="settings-save"
                className="dp-btn dp-btn-primary dp-btn-md dp-focus-ring"
                disabled={savePending}
                onClick={() => {
                  void persistDraft();
                }}
              >
                Save changes
              </button>
              <button
                type="button"
                data-testid="settings-discard"
                className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
                disabled={savePending}
                onClick={handleDiscard}
              >
                Discard
              </button>
            </div>
          </Alert>
        </div>
      ) : null}

      {online === false || offlineQueued ? (
        <div data-testid="settings-offline">
          <Alert tone="warning" title="Offline — draft queued">
            <p data-testid="settings-queued-text">
              You appear to be offline. Your draft was kept and queued — reconnect, then retry. Nothing was discarded.
            </p>
            <button
              type="button"
              data-testid="settings-queued-retry"
              className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
              disabled={savePending}
              onClick={() => {
                void persistDraft();
              }}
            >
              Retry save
            </button>
          </Alert>
        </div>
      ) : null}

      {conflict !== null ? (
        <div data-testid="settings-conflict">
          <Alert tone="warning" title="Preferences changed elsewhere">
            <p data-testid="settings-conflict-text">{conflict}</p>
            <div style={{ display: 'flex', gap: '0.5rem' }}>
              <button
                type="button"
                data-testid="settings-conflict-retry"
                className="dp-btn dp-btn-primary dp-btn-md dp-focus-ring"
                disabled={savePending}
                onClick={() => {
                  void persistDraft();
                }}
              >
                Retry with my values
              </button>
              <button
                type="button"
                data-testid="settings-conflict-refresh"
                className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
                onClick={() => {
                  void refetch();
                }}
              >
                Refresh
              </button>
              <button
                type="button"
                data-testid="settings-conflict-use-server"
                className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
                onClick={handleConflictUseServer}
              >
                Use server values
              </button>
            </div>
          </Alert>
        </div>
      ) : null}

      {forbidden !== null ? (
        <div data-testid="settings-forbidden">
          <Alert tone="error" title="Not permitted">
            <p data-testid="settings-forbidden-text">{forbidden}</p>
          </Alert>
        </div>
      ) : null}

      {saveError !== null && conflict === null && !offlineQueued ? (
        <div data-testid="settings-save-error">
          <Alert tone="error" title="Preferences could not be saved">
            <p data-testid="settings-save-error-text">{saveError}</p>
            <button
              type="button"
              data-testid="settings-save-retry"
              className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
              disabled={savePending}
              onClick={() => {
                void persistDraft();
              }}
            >
              Retry
            </button>
          </Alert>
        </div>
      ) : null}

      {isFetching && isDirty ? (
        <p data-testid="settings-refreshing" className="dp-muted">
          Refreshing latest values in the background; your draft is preserved.
        </p>
      ) : null}

      <div style={{ display: 'flex', flexDirection: 'column', gap: 'var(--space-4)', marginTop: 'var(--space-4)' }}>
        <label>
          <span>Language</span>
          <select
            data-testid="settings-field-locale"
            value={draft.locale}
            disabled={savePending}
            onChange={(event) => {
              const next = normalizeLocale(event.target.value);
              setDraft((previous) => ({ ...previous, locale: next }));
              setSavedNote((previous) => ({ ...previous, locale: undefined }));
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
            Language saved. Applied without reload.
          </p>
        ) : null}

        <label>
          <span>Timezone (IANA)</span>
          <select
            data-testid="settings-field-timezone"
            value={TIMEZONE_OPTIONS.includes(draft.timezone) ? draft.timezone : 'UTC'}
            disabled={savePending}
            onChange={(event) => {
              const next = normalizeTimezoneOption(event.target.value);
              setDraft((previous) => ({ ...previous, timezone: next }));
              setSavedNote((previous) => ({ ...previous, timezone: undefined }));
            }}
          >
            {TIMEZONE_OPTIONS.map((option) => (
              <option key={option} value={option}>
                {option}
              </option>
            ))}
          </select>
        </label>
        {showTzWarning ? (
          <div data-testid="settings-timezone-warning">
            <Alert tone="warning" title="Unknown timezone, using UTC">
              <p>Fell back to UTC. Select a valid IANA timezone such as America/New_York.</p>
              <button
                type="button"
                data-testid="settings-timezone-use-utc"
                className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
                disabled={savePending}
                onClick={() => {
                  setDraft((previous) => ({ ...previous, timezone: 'UTC' }));
                }}
              >
                Use UTC
              </button>
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
            Timezone saved. Applied without reload.
          </p>
        ) : null}

        <fieldset>
          <legend>Theme (applies on save, no reload)</legend>
          {THEME_OPTIONS.map((option) => (
            <label key={option}>
              <input
                type="radio"
                name="settings-theme"
                data-testid={`settings-field-theme-${option}`}
                checked={draft.theme === option}
                disabled={savePending}
                onChange={() => {
                  const next = normalizeThemePreference(option);
                  setDraft((previous) => ({ ...previous, theme: next }));
                  setSavedNote((previous) => ({ ...previous, theme: undefined }));
                }}
              />
              {option}
            </label>
          ))}
          <p className="dp-muted" data-testid="settings-theme-system-note">
            System follows your OS setting and resolves to light or dark without a reload.
          </p>
        </fieldset>
        {fieldErrors['theme'] !== undefined ? (
          <div data-testid="settings-field-error-theme">
            <Alert tone="error" title="Theme could not be saved">
              <p data-testid="settings-field-error-message-theme">{fieldErrors['theme']}</p>
            </Alert>
          </div>
        ) : null}
        {savedNote['theme'] !== undefined && fieldErrors['theme'] === undefined ? (
          <p data-testid="settings-theme-note" className="dp-muted">
            Theme saved. Applied without reload.
          </p>
        ) : null}

        <fieldset>
          <legend>Default project filters</legend>
          <label>
            <span>Status</span>
            <select
              data-testid="settings-field-filters-status"
              value={draft.filterStatus}
              disabled={savePending}
              onChange={(event) => {
                const nextStatus = event.target.value.slice(0, 40);
                setDraft((previous) => ({ ...previous, filterStatus: nextStatus }));
                setSavedNote((previous) => ({ ...previous, defaultProjectFilters: undefined }));
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
              value={draft.filterArchived}
              disabled={savePending}
              onChange={(event) => {
                const nextArchived = event.target.value.slice(0, 40);
                setDraft((previous) => ({ ...previous, filterArchived: nextArchived }));
                setSavedNote((previous) => ({ ...previous, defaultProjectFilters: undefined }));
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
        {savedNote['defaultProjectFilters'] !== undefined && fieldErrors['defaultProjectFilters'] === undefined ? (
          <p data-testid="settings-filters-note" className="dp-muted">
            Default filters saved.
          </p>
        ) : null}

        <label>
          <span>Timeline zoom (1–4)</span>
          <input
            type="range"
            min={1}
            max={4}
            step={1}
            data-testid="settings-field-zoom"
            value={String(draft.zoom)}
            disabled={savePending}
            onChange={(event) => {
              const next = Math.min(4, Math.max(1, Math.round(Number(event.target.value) || 1)));
              setDraft((previous) => ({ ...previous, zoom: next }));
              setSavedNote((previous) => ({ ...previous, timelineZoom: undefined }));
            }}
          />
          <span data-testid="settings-zoom-value">{String(draft.zoom)}</span>
        </label>
        {fieldErrors['timelineZoom'] !== undefined ? (
          <div data-testid="settings-field-error-zoom">
            <Alert tone="error" title="Zoom could not be saved">
              <p data-testid="settings-field-error-message-zoom">{fieldErrors['timelineZoom']}</p>
            </Alert>
          </div>
        ) : null}
        {savedNote['timelineZoom'] !== undefined && fieldErrors['timelineZoom'] === undefined ? (
          <p data-testid="settings-zoom-note" className="dp-muted">
            Timeline zoom saved.
          </p>
        ) : null}

        <fieldset>
          <legend>Delivery channels</legend>
          <p className="dp-muted" data-testid="settings-channels-note">
            In-app delivery is available now. Email and webhook are future-only and disabled here; the toggles below
            control in-app delivery. Toggles disable future delivery only. History is never deleted.
          </p>
          <ul style={{ listStyle: 'none', margin: 0, padding: 0 }}>
            {NOTIFICATION_CHANNELS.map((channel) => {
              const isFuture = channel !== 'in-app';
              return (
                <li key={channel}>
                  <label>
                    <input
                      type="checkbox"
                      data-testid={`settings-channel-${channel}`}
                      checked={channel === 'in-app'}
                      disabled={isFuture || savePending}
                      onChange={() => {
                        // Channels are display-only: in-app is always on, future channels disabled.
                      }}
                    />
                    {channel}
                    {isFuture ? ' (coming soon)' : ''}
                  </label>
                </li>
              );
            })}
          </ul>
        </fieldset>

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
                    disabled={savePending}
                    onChange={(event) => {
                      const next = { ...notifPrefs, [type]: event.target.checked };
                      setNotifPrefs(next);
                      setDraft((previous) => ({ ...previous, notificationPreferences: serializeNotificationPrefs(next) }));
                      setSavedNote((previous) => ({ ...previous, notificationPreferences: undefined }));
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
        {savedNote['notificationPreferences'] !== undefined &&
        fieldErrors['notificationPreferences'] === undefined ? (
          <p data-testid="settings-notifs-note" className="dp-muted">
            Notification preferences saved.
          </p>
        ) : null}
      </div>

      <div style={{ display: 'flex', gap: '0.5rem', marginTop: 'var(--space-4)' }}>
        <button
          type="button"
          data-testid="settings-back-link"
          className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
          onClick={handleBack}
        >
          Back to dashboard
        </button>
      </div>

      {pendingLeave !== undefined ? (
        <div data-testid="settings-dirty-dialog" role="dialog" aria-label="Unsaved preferences">
          <Alert tone="warning" title="Unsaved preferences">
            <p data-testid="settings-dirty-dialog-text">
              {pendingLabel !== undefined
                ? `You have unsaved preferences (navigating to ${pendingLabel}). Save them, discard them, or stay.`
                : 'You have unsaved preferences. Save them, discard them, or stay.'}
            </p>
            <div style={{ display: 'flex', gap: '0.5rem' }}>
              <button
                type="button"
                data-testid="settings-dirty-save"
                className="dp-btn dp-btn-primary dp-btn-md dp-focus-ring"
                disabled={savePending}
                onClick={() => {
                  void handleGuardSave();
                }}
              >
                Save changes
              </button>
              <button
                type="button"
                data-testid="settings-dirty-discard"
                className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
                onClick={handleGuardDiscard}
              >
                Discard changes
              </button>
              <button
                type="button"
                data-testid="settings-dirty-cancel"
                className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
                onClick={handleGuardCancel}
              >
                Stay
              </button>
            </div>
          </Alert>
        </div>
      ) : null}

      <div hidden>
        <span data-testid="settings-prefs-key">{JSON.stringify(queryKeys.preferences.list())}</span>
        <span data-testid="settings-theme">{t('common:retry')}</span>
      </div>
    </div>
  );
}
