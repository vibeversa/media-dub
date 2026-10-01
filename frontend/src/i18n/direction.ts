/**
 * Text direction, RTL detection and the `?dir=` manual override (Task 045).
 *
 * WHY THIS IS ITS OWN FILE
 * ------------------------
 * `react-refresh/only-export-components` makes a `.tsx` file component-only, so
 * the direction *rules* cannot live next to the provider that applies them. They
 * are also the part that has to be provable without rendering anything, which is
 * what R3 asks for.
 *
 * WHAT `?dir=` IS, AND WHAT IT IS NOT
 * -----------------------------------
 * `?dir=rtl` is a **presentation-only** debugging aid: it mirrors the shell so a
 * reviewer can see what an Arabic or Hebrew layout looks like without changing a
 * stored preference. It reads one query parameter and writes exactly one DOM
 * attribute (`<html dir>`).
 *
 * It deliberately does NOT:
 *   - change the active locale (so Arabic copy is *not* what you see; the point
 *     is to inspect chrome layout, and mixing the two hides chrome bugs behind
 *     copy that reflows differently),
 *   - touch `useAppStore`, i18next, auth, tenant scoping or any API request,
 *   - survive a navigation that rewrites the query string.
 *
 * An unrecognised value (`?dir=sideways`, `?dir=`, `?dir=RTL%20`) is ignored
 * rather than guessed at — a typo in a debug switch must not silently mirror a
 * production screen.
 */

export type Direction = 'ltr' | 'rtl';

/** Query parameter carrying the manual direction override. */
export const DIRECTION_PARAM = 'dir';

/**
 * Base languages laid out right-to-left.
 *
 * A base-language list, not `Intl.Locale.prototype.textInfo`: `textInfo` is
 * stage-3 and unavailable in the jsdom/Node ICU build this app ships with, so
 * relying on it would make direction detection depend on a browser version.
 * Adding a language is a one-line, reviewable change here.
 */
const RTL_BASE_LANGUAGES = new Set(['ar', 'ckb', 'dv', 'fa', 'he', 'ps', 'sd', 'ug', 'ur', 'yi']);

/**
 * The base (language-only) part of a tag, lowercased. `'ar-EG'` → `'ar'`,
 * `'EN-us'` → `'en'`, `''` and `'-x'` → `''`.
 */
export function baseLanguage(locale: string): string {
  const dash = locale.indexOf('-');
  return (dash >= 0 ? locale.slice(0, dash) : locale).toLowerCase();
}

/** True when the locale lays out right-to-left. Never throws. */
export function isRtlLocale(locale: string | undefined): boolean {
  if (locale === undefined || locale === '') {
    return false;
  }
  return RTL_BASE_LANGUAGES.has(baseLanguage(locale));
}

/**
 * Reads the `?dir=` override out of a query string.
 *
 * Takes the string rather than reading `window.location` so it is pure and
 * testable, and so the caller can pass `location.search` once instead of this
 * module reaching into globals. Anything that is not exactly `ltr` or `rtl`
 * (case-insensitively) yields `undefined`, which means "no override".
 */
export function resolveDirectionOverride(search: string | undefined): Direction | undefined {
  if (search === undefined || search === '') {
    return undefined;
  }
  let params: URLSearchParams;
  try {
    params = new URLSearchParams(search);
  } catch {
    return undefined;
  }
  const raw = params.get(DIRECTION_PARAM);
  if (raw === null) {
    return undefined;
  }
  const normalized = raw.trim().toLowerCase();
  if (normalized === 'rtl') {
    return 'rtl';
  }
  if (normalized === 'ltr') {
    return 'ltr';
  }
  return undefined;
}

/** The `?dir=` override for the current location, or `undefined`. */
export function currentDirectionOverride(): Direction | undefined {
  if (typeof window === 'undefined') {
    return undefined;
  }
  try {
    return resolveDirectionOverride(window.location.search);
  } catch {
    return undefined;
  }
}

/**
 * Effective direction: the override when present, otherwise the locale's own
 * direction. `ltr` is the default for an unknown locale, never `undefined`.
 */
export function resolveDirection(locale: string | undefined, override?: Direction): Direction {
  if (override !== undefined) {
    return override;
  }
  return isRtlLocale(locale) ? 'rtl' : 'ltr';
}

/**
 * Syncs `<html dir>`/`<html lang>` with the active locale and the override.
 *
 * `lang` is set from the **full tag** (`ar-EG`, `en-US`) while `dir` is derived
 * from the base language, because the two answer different questions: which
 * locale this document is in, and which way it reads. A screen reader needs the
 * first; the layout needs the second.
 *
 * Returns the direction it applied, so a caller (and a test) can assert the
 * decision without re-deriving it. Safe under SSR/no-DOM (`typeof document`).
 */
export function applyDirection(locale: string, override?: Direction): Direction {
  const direction = resolveDirection(locale, override);
  if (typeof document === 'undefined') {
    return direction;
  }
  const root = document.documentElement;
  root.setAttribute('dir', direction);
  root.setAttribute('lang', locale === '' ? 'en' : locale);
  return direction;
}