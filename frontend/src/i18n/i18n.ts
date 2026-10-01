import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import { PSEUDO_LOCALE, buildPseudoResources } from './locales/pseudo.js';
import { recordMissingTranslation } from './missingKeys.js';
import { AR_RESOURCES, EN_RESOURCES, NAMESPACE_NAMES, RESOURCES, RU_RESOURCES } from './resources.js';
import { trackMissingTranslation } from '../telemetry/telemetry.js';

export const FALLBACK_LOCALE = 'en';
export const SUPPORTED_LOCALES = ['en', 'ar', 'ru'] as const;
export type SupportedLocale = (typeof SUPPORTED_LOCALES)[number];

/**
 * Locales i18next is willing to resolve, which is the shipped locales **plus**
 * the pseudo test locale.
 *
 * `SUPPORTED_LOCALES` deliberately stays the three a person can select: the
 * locale switcher (`AppShell`), the preference form and any `locale in
 * SUPPORTED_LOCALES` check must never offer `pseudo`. Adding it to the same
 * list would put a fake locale in front of users the moment anyone reused the
 * constant for a `<Select>`.
 *
 * `pseudo` is registered here so `i18n.t(key, { lng: 'pseudo' })` resolves — that
 * is the whole mechanism the pseudo-locale test drives.
 */
const RESOLVABLE_LOCALES: readonly string[] = [...SUPPORTED_LOCALES, PSEUDO_LOCALE];

/**
 * The pseudo bundle, derived from `EN_RESOURCES` at module load.
 *
 * Never committed, never fetched, never stale — see the header of
 * `locales/pseudo.ts` for why that is the only safe shape for it.
 */
const PSEUDO_RESOURCES = buildPseudoResources(EN_RESOURCES);

/**
 * Locale recorded for a missing-key report. i18next always hands the handler
 * an array, but the resolver falls back to `FALLBACK_LOCALE` for a non-array
 * or empty list so telemetry never records an empty locale. Pure.
 */
export function missingKeyLocale(lngs: readonly string[] | string | undefined): string {
  if (!Array.isArray(lngs)) {
    return FALLBACK_LOCALE;
  }
  return lngs[0] ?? FALLBACK_LOCALE;
}

/**
 * i18next init (Task 018; bundles and the pseudo locale from Task 045): `en`
 * baseline with namespaced JSON bundles (common, nav, auth, dashboard, projects,
 * processing, uploads, workspace, transcript, translation, voices, timeline,
 * review, quality, exports, notifications, activity, settings, errors), `ar`/`ru`
 * partial bundles for RTL + plural coverage, a derived `pseudo` bundle for the
 * locale-readiness test, and `en` as `fallbackLng` for every missing key (never a
 * blank string). Plural categories follow ICU via Intl.PluralRules
 * (`_zero/_one/_two/_few/_many/_other` suffixes). Synchronous init
 * (`initAsync: false`) with inline resources so unit tests and the shell render
 * translated strings on first paint.
 *
 * RESOURCES ARE COMPILED IN, NEVER FETCHED
 * ----------------------------------------
 * There is no `backendConnector` and no `loadPath`. A translation bundle fetched
 * at runtime is a JSON file chosen by someone else's server that rewrites every
 * button in the product — including "Delete project" and any error text a user
 * copies into a support ticket. Shipping a locale means committing a directory
 * under `src/i18n/locales/`; that is the whole deployment story.
 */
void i18n.use(initReactI18next).init({
  resources: {
    en: EN_RESOURCES,
    ar: AR_RESOURCES,
    ru: RU_RESOURCES,
    [PSEUDO_LOCALE]: PSEUDO_RESOURCES,
  },
  lng: 'en',
  fallbackLng: FALLBACK_LOCALE,
  supportedLngs: RESOLVABLE_LOCALES,
  defaultNS: 'common',
  // Feature code always uses namespaced keys (`nav:dashboard`); a bare key
  // resolves against `common` instead of rendering the key itself blank.
  ns: [...NAMESPACE_NAMES],
  interpolation: {
    // React already escapes; double-escaping would corrupt ICU placeholders.
    escapeValue: false,
  },
  returnEmptyString: false,
  initAsync: false,
  // i18next only invokes `missingKeyHandler` when `saveMissing` is on, and it
  // is the handler (not a backend connector) that runs, so missing keys are
  // reported to telemetry without any network write.
  saveMissing: true,
  // TWO OBSERVERS, ONE EVENT (Task 045). Telemetry is the production channel
  // and is gated on a build flag plus the user's opt-out; the in-process
  // collector is the channel a test can assert on unconditionally, which is what
  // R4 ("no missing-key warnings") needs. Both are fed here so they cannot
  // disagree about what happened.
  missingKeyHandler: (lngs, ns, key) => {
    recordMissingTranslation({ locale: missingKeyLocale(lngs), namespace: ns, key });
    trackMissingTranslation({ locale: missingKeyLocale(lngs), key });
  },
});

export default i18n;

/** Re-exported so `resources.ts` and `i18n.ts` cannot disagree on the map. */
export { RESOURCES };