import { useCallback, useEffect, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import type { UseQueryResult } from '@tanstack/react-query';
import { apiClient } from '../../api/client/index.js';
import { normalizeError } from '../../api/errors/index.js';
import type { AppError } from '../../api/errors/index.js';
import { useIsAuthenticated } from '../auth/useSession.js';
import { NOTIFICATION_PREFERENCE_KEY, NOTIFICATION_TYPES } from './types.js';
import type { NotificationTypeName } from './types.js';

/**
 * Per-type delivery toggles bound to the `notificationPreferences` identity
 * key (Task 034, Task 006 `PUT /me/preferences`).
 *
 * - The stored value is a JSON-encoded `Record<type, boolean>` under the
 *   single `notificationPreferences` key (the generated
 *   `PreferencesResponse` map is `Record<string, string>`). Unknown or
 *   missing entries default to enabled (`true`).
 * - Toggles disable future delivery only; they never delete history (the
 *   list query is untouched by this hook).
 * - A rejected save (`PREFERENCE_KEY_UNKNOWN` or any 400/413) surfaces an
 *   inline field error and leaves toggles unchanged (rollback to the last
 *   server snapshot).
 */

export type NotificationPrefs = Record<NotificationTypeName, boolean>;

function defaultPrefs(): NotificationPrefs {
  return {
    ProcessingCompleted: true,
    ProcessingFailed: true,
    ManualReviewRequired: true,
    ReviewResolved: true,
    ExportCompleted: true,
    ExportFailed: true,
    UploadRejected: true,
    QuotaWarning: true,
    ProviderPolicyWarning: true,
  };
}

/** Parses the stored `notificationPreferences` string value. Never throws. Pure. */
export function parseNotificationPrefs(raw: unknown): NotificationPrefs {
  const defaults = defaultPrefs();
  if (typeof raw !== 'string' || raw === '') {
    return defaults;
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw) as unknown;
  } catch {
    return defaults;
  }
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    return defaults;
  }
  const record = parsed as Record<string, unknown>;
  const next: NotificationPrefs = { ...defaults };
  for (const type of NOTIFICATION_TYPES) {
    const value = record[type];
    if (typeof value === 'boolean') {
      next[type] = value;
    } else if (typeof value === 'string') {
      const lowered = value.toLowerCase();
      if (lowered === 'true' || lowered === '1' || lowered === 'on' || lowered === 'enabled') {
        next[type] = true;
      } else if (lowered === 'false' || lowered === '0' || lowered === 'off' || lowered === 'disabled') {
        next[type] = false;
      }
    } else if (typeof value === 'number') {
      next[type] = value !== 0;
    }
  }
  return next;
}

/** Serializes prefs for the `notificationPreferences` string value. Pure. */
export function serializeNotificationPrefs(prefs: NotificationPrefs): string {
  const ordered: Record<string, boolean> = {};
  for (const type of NOTIFICATION_TYPES) {
    ordered[type] = prefs[type] === true;
  }
  return JSON.stringify(ordered);
}

async function fetchPrefsMap(): Promise<Record<string, string>> {
  try {
    const response = await apiClient.getPreferences();
    return (response ?? {}) as Record<string, string>;
  } catch (error) {
    throw normalizeError(error, { method: 'GET' });
  }
}

export function prefsQueryKey(): readonly unknown[] {
  return ['me', 'preferences', NOTIFICATION_PREFERENCE_KEY];
}

export function useNotificationPrefsQuery(): UseQueryResult<NotificationPrefs, AppError> {
  const enabled = useIsAuthenticated();
  return useQuery<NotificationPrefs, AppError>({
    queryKey: prefsQueryKey(),
    queryFn: async (): Promise<NotificationPrefs> => {
      const map = await fetchPrefsMap();
      return parseNotificationPrefs(map[NOTIFICATION_PREFERENCE_KEY]);
    },
    enabled,
    staleTime: 60_000,
    refetchInterval: false,
    refetchOnWindowFocus: false,
    retry: false,
  });
}

export interface UseNotificationPrefsResult {
  readonly prefs: NotificationPrefs;
  readonly isPending: boolean;
  readonly fieldError: string | undefined;
  readonly savePending: boolean;
  readonly setTypeEnabled: (type: NotificationTypeName, enabled: boolean) => void;
}

/**
 * Center prefs hook: exposes per-type toggles with immediate `PUT`
 * persistence. Failures roll back to the last server snapshot and surface
 * `fieldError` inline.
 */
export function useNotificationPrefs(): UseNotificationPrefsResult {
  const queryClient = useQueryClient();
  const prefsQuery = useNotificationPrefsQuery();
  const [local, setLocal] = useState<NotificationPrefs | undefined>(undefined);
  const [fieldError, setFieldError] = useState<string | undefined>(undefined);
  const [savePending, setSavePending] = useState(false);

  const server = prefsQuery.data ?? defaultPrefs();
  const prefs = local ?? server;

  useEffect(() => {
    setLocal(undefined);
    setFieldError(undefined);
  }, [prefsQuery.data]);

  const setTypeEnabled = useCallback(
    (type: NotificationTypeName, enabled: boolean): void => {
      const base = local ?? server;
      const next: NotificationPrefs = { ...base, [type]: enabled };
      const previous = base;
      setLocal(next);
      setFieldError(undefined);
      setSavePending(true);
      void (async (): Promise<void> => {
        try {
          const body = { [NOTIFICATION_PREFERENCE_KEY]: serializeNotificationPrefs(next) };
          await apiClient.updatePreferences(
            { path: {} },
            body as unknown as Parameters<typeof apiClient.updatePreferences>[1],
          );
          queryClient.setQueryData(prefsQueryKey(), next);
          setLocal(undefined);
        } catch (error) {
          const normalized = normalizeError(error, { method: 'PUT' });
          setLocal(previous);
          if (normalized.code === 'PREFERENCE_KEY_UNKNOWN') {
            setFieldError('Unknown preference key. Refresh the page and try again.');
          } else if (normalized.code === 'PREFERENCE_VALUE_TOO_LARGE') {
            setFieldError('This preference value is too large. Shorten it and try again.');
          } else {
            setFieldError(normalized.message !== '' ? normalized.message : 'Preferences could not be saved. No data was changed.');
          }
        } finally {
          setSavePending(false);
        }
      })();
    },
    [local, queryClient, server],
  );

  return {
    prefs,
    isPending: prefsQuery.isPending && local === undefined,
    fieldError: prefsQuery.isError
      ? (prefsQuery.error?.message ?? 'Preferences could not be loaded.')
      : fieldError,
    savePending,
    setTypeEnabled,
  };
}
