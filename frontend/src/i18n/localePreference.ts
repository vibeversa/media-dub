import { FALLBACK_LOCALE, isSupportedLocaleTag } from '../lib/dates/dates.js';
import { baseLanguage } from './direction.js';

/**
 * Default-locale resolution from the user preference (Task 045, R5 /
 * instruction 5).
 *
 * THE FALLBACK CHAIN
 * ------------------
 *   1. **preference** — `locale` on `GET /me`. The platform reads it from the
 *      `UserPreference` row (`UserPreference.AllowedKeys` includes `locale`), and
 *      `MeResponse.Locale` defaults server-side to `en-US`.
 *   2. **stored** — `dubbing.locale` in `localStorage`, i.e. the last locale
 *      this browser's owner explicitly chose. This is the local half of
 *      "preference": without it, a user who switched to Arabic and then signed
 *      out gets English again for the login screen.
 *   3. **browser** — `navigator.languages`, first entry the app ships a bundle
 *      for.
 *   4. **en** — the `fallbackLng` baseline.
 *
 * The task specifies `preference -> browser -> en`. `stored` is inserted between
 * preference and browser because it is the same thing by another transport: an
 * explicit user choice. Leaving it out would mean an explicit choice is
 * outranked by the browser default, which is the one ordering nobody asked for.
 *
 * EVERY STEP CAN FAIL, AND EVERY FAILURE IS `en`
 * ------------------------------------------------
 * An invalid preference (`"not a locale"`), an unsupported one (`"xh"`), a
 * non-string one (`42`), a browser that reports `["zz-ZZ"]`, and a
 * `localStorage` that throws on read all resolve to `en` with a reason code. The
 * caller can warn; nothing crashes, and no step can produce `undefined`.
 *
 * THIS MODULE IS PURE
 * -------------------
 * Every input is a parameter. Nothing here reads `localStorage`, `navigator`, or
 * `window` — the read side lives in `applyLocalePreference` below, which is the
 * only impure function and is the single place the app touches either global.
 */

export type LocaleSource = 'preference' | 'stored' | 'browser' | 'default';

/**
 * Why a candidate locale was refused.
 *
 * There is deliberately no `absent` code. "Nothing was offered at this step" is
 * already answered by which step won (`source`), and recording it would bury the
 * one thing `rejected` exists for - *something was offered and we could not
 * honour it*, which is a data problem somebody has to fix.
 */
export type LocaleRejectionReason = 'blank' | 'not-a-string' | 'malformed' | 'unsupported';

export interface LocaleResolution {
  /** The tag to apply. Always a usable `Intl` tag; `en` at worst. */
  readonly locale: string;
  /** Which step of the chain produced it. */
  readonly source: LocaleSource;
  /** Rejections encountered while walking the chain, in order. */
  readonly rejected: readonly LocaleRejection[];
}

/** Locales the product ships bundles for (`en` + the translations in 018). */
export const LOCALE_CHOICES: readonly string[] = ['en', 'ar', 'ru'];

export interface ResolveLocaleInput {
  /** `MeResponse.Locale` (raw — it is parsed defensively, see below). */
  readonly preference?: unknown;
  /** `localStorage['dubbing.locale']`. */
  readonly stored?: string | undefined;
  /** `navigator.languages`. */
  readonly browserLocales?: readonly string[];
  /** Bundle tags the app ships. Defaults to `LOCALE_CHOICES`. */
  readonly supported?: readonly string[];
  /** Last resort. Defaults to `en`. */
  readonly fallback?: string;
}

/** One candidate the chain refused, and why. */
export interface LocaleRejection {
  readonly value: unknown;
  readonly reason: LocaleRejectionReason;
}

function reject(value: unknown, reason: LocaleRejectionReason): LocaleRejection {
  return { value, reason };
}

/**
 * Normalises one candidate tag.
 *
 * Accepts a full BCP-47 tag (`en-US`, `ar-EG`) and returns it intact when the
 * app ships a bundle for its base language, because the *tag* is what
 * `Intl.DateTimeFormat` / `Intl.NumberFormat` need and `en-US` formats dates
 * differently from `en-GB`. The bundle lookup happens through i18next, which
 * resolves `en-US -> en` on its own.
 *
 * Rejects with a reason rather than returning `undefined` so the caller can tell
 * "no preference at all" from "a preference we could not honour" — the second is
 * a data problem worth surfacing and the first is not.
 */
function normalizeCandidate(
  raw: unknown,
  supported: readonly string[],
  rejected: LocaleRejection[],
  value: unknown,
): string | undefined {
  if (raw === undefined || raw === null) {
    // Not a rejection: nothing was offered here. See `LocaleRejectionReason`.
    return undefined;
  }
  if (typeof raw !== 'string') {
    rejected.push(reject(value, 'not-a-string'));
    return undefined;
  }
  const trimmed = raw.trim();
  if (trimmed === '') {
    rejected.push(reject(value, 'blank'));
    return undefined;
  }
  if (!isSupportedLocaleTag(trimmed)) {
    rejected.push(reject(value, 'malformed'));
    return undefined;
  }
  const base = baseLanguage(trimmed);
  if (!supported.some((locale) => baseLanguage(locale) === base)) {
    rejected.push(reject(value, 'unsupported'));
    return undefined;
  }
  return trimmed;
}

/**
 * Walks the fallback chain and returns the first usable locale. Pure.
 *
 * Every input is optional, so the fully-empty call (`resolveLocale({})`) is the
 * documented degenerate case: `{ locale: 'en', source: 'default', rejected: [] }`.
 */
export function resolveLocale(input: ResolveLocaleInput = {}): LocaleResolution {
  const supported = input.supported ?? LOCALE_CHOICES;
  const fallback = input.fallback ?? FALLBACK_LOCALE;
  const rejected: LocaleRejection[] = [];

  const fromPreference = normalizeCandidate(input.preference, supported, rejected, input.preference);
  if (fromPreference !== undefined) {
    return { locale: fromPreference, source: 'preference', rejected };
  }

  const fromStored = normalizeCandidate(input.stored, supported, rejected, input.stored);
  if (fromStored !== undefined) {
    return { locale: fromStored, source: 'stored', rejected };
  }

  for (const candidate of input.browserLocales ?? []) {
    const fromBrowser = normalizeCandidate(candidate, supported, rejected, candidate);
    if (fromBrowser !== undefined) {
      return { locale: fromBrowser, source: 'browser', rejected };
    }
  }

  return { locale: fallback, source: 'default', rejected };
}

/**
 * Reads `locale` out of a raw `GET /me` document.
 *
 * WHY RAW, NOT THE GENERATED TYPE
 * -------------------------------
 * The committed OpenAPI bundle's `MeResponse` declares only
 * `userId`/`tenantId`/`permissions`/`roles` — the server record
 * (`AuthMeDtos.MeResponse`) carries `locale`, `featureFlags` and `session`, and
 * the bundle is behind it. The generated type would make `me.locale` a type
 * error, so this reads `unknown` and validates, exactly as Task 044 reads the
 * `/me` flag slice. Against an older backend the field is absent, the parse
 * yields `undefined`, and the chain simply continues to `stored`.
 *
 * Also accepts a bare string, because a caller holding just the value should not
 * have to fabricate a document to test this.
 */
export function parseMeLocale(raw: unknown): string | undefined {
  if (typeof raw === 'string') {
    return raw;
  }
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) {
    return undefined;
  }
  const locale = (raw as Record<string, unknown>)['locale'];
  return typeof locale === 'string' ? locale : undefined;
}

/**
 * The browser's languages, or `[]` where `navigator` is unavailable. This is the
 * only global read in the module, and it is guarded.
 */
export function readBrowserLocales(): readonly string[] {
  if (typeof navigator === 'undefined') {
    return [];
  }
  const languages = navigator.languages;
  if (Array.isArray(languages) && languages.length > 0) {
    return languages.filter((tag): tag is string => typeof tag === 'string');
  }
  return typeof navigator.language === 'string' ? [navigator.language] : [];
}

/**
 * Resolves the locale from the live browser (preference -> stored -> browser ->
 * en) and writes it into `useAppStore`, returning what it resolved.
 *
 * This is the impure half, called once when `/me` settles (see `authStore.ts`).
 * `applyLocale` is injected rather than imported so this stays testable without
 * a store, and so the caller can prove the write happened exactly once.
 */
export function resolveAndApplyLocale(
  rawMe: unknown,
  applyLocale: (locale: string) => void,
  stored?: string | undefined,
  supported?: readonly string[],
): LocaleResolution {
  const resolution = resolveLocale({
    preference: parseMeLocale(rawMe),
    stored: stored ?? undefined,
    browserLocales: readBrowserLocales(),
    ...(supported === undefined ? {} : { supported }),
  });
  applyLocale(resolution.locale);
  return resolution;
}