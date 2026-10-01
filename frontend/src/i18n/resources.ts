import arCommon from './locales/ar/common.json';
import arNav from './locales/ar/nav.json';
import ruCommon from './locales/ru/common.json';
import enActivity from './locales/en/activity.json';
import enAuth from './locales/en/auth.json';
import enCommon from './locales/en/common.json';
import enDashboard from './locales/en/dashboard.json';
import enErrors from './locales/en/errors.json';
import enExports from './locales/en/exports.json';
import enNav from './locales/en/nav.json';
import enNotifications from './locales/en/notifications.json';
import enProcessing from './locales/en/processing.json';
import enProjects from './locales/en/projects.json';
import enQuality from './locales/en/quality.json';
import enReview from './locales/en/review.json';
import enSettings from './locales/en/settings.json';
import enTimeline from './locales/en/timeline.json';
import enTranscript from './locales/en/transcript.json';
import enTranslation from './locales/en/translation.json';
import enUploads from './locales/en/uploads.json';
import enVoices from './locales/en/voices.json';
import enWorkspace from './locales/en/workspace.json';

/**
 * The translation resource registry (Task 045; structure from Task 018).
 *
 * WHY THIS IS A SEPARATE MODULE
 * -----------------------------
 * `i18n.ts` used to inline the whole `resources` object, which meant the
 * *bundles themselves* were only reachable from the module that boots i18next.
 * Three things need them without booting anything:
 *
 *   1. `locales/pseudo.ts` derives the pseudo bundle from `en` (R4), and must do
 *      so from the same source i18next loads or the pseudo run proves nothing.
 *   2. The bundle-completeness test walks every namespace to assert that no key
 *      is missing a plural sibling.
 *   3. The extraction gate (`scripts/check-no-hardcoded-copy.mjs`) enumerates the
 *      shipped namespaces to report which ones exist.
 *
 * NO REMOTE BUNDLES
 * -----------------
 * Resources are static `import`s of JSON compiled into the bundle. There is no
 * `backendConnector`, no `loadPath`, and no fetch of a translation file: a
 * remote bundle is an attacker-controlled `en.json` that silently rewrites every
 * button in the product, and there is no deployment where that trade is worth it
 * for this app. Adding a locale means committing a directory here.
 */

export type TranslationBundle = Readonly<Record<string, unknown>>;

export type NamespaceResources = Readonly<Record<string, TranslationBundle>>;

/** Every namespace in the product, in `i18n.ts`'s load order. */
export const NAMESPACE_NAMES = [
  'common',
  'nav',
  'auth',
  'dashboard',
  'projects',
  'processing',
  'uploads',
  'workspace',
  'transcript',
  'translation',
  'voices',
  'timeline',
  'review',
  'quality',
  'exports',
  'notifications',
  'activity',
  'settings',
  'errors',
] as const;

export type NamespaceName = (typeof NAMESPACE_NAMES)[number];

/**
 * The English baseline: complete, and the `fallbackLng` target for every other
 * locale. A key that exists nowhere else must exist here.
 */
export const EN_RESOURCES: NamespaceResources = Object.freeze({
  common: enCommon,
  nav: enNav,
  auth: enAuth,
  dashboard: enDashboard,
  projects: enProjects,
  processing: enProcessing,
  uploads: enUploads,
  workspace: enWorkspace,
  transcript: enTranscript,
  translation: enTranslation,
  voices: enVoices,
  timeline: enTimeline,
  review: enReview,
  quality: enQuality,
  exports: enExports,
  notifications: enNotifications,
  activity: enActivity,
  settings: enSettings,
  errors: enErrors,
});

/** Arabic: RTL + six-way plurals. Partial by design — `en` fills the rest. */
export const AR_RESOURCES: NamespaceResources = Object.freeze({
  common: arCommon,
  nav: arNav,
});

/** Russian: LTR with one/few/many. Partial by design. */
export const RU_RESOURCES: NamespaceResources = Object.freeze({
  common: ruCommon,
});

export type LocaleResources = Readonly<Record<string, NamespaceResources>>;

/** Every locale's namespaces, keyed by locale tag. */
export const RESOURCES: LocaleResources = Object.freeze({
  en: EN_RESOURCES,
  ar: AR_RESOURCES,
  ru: RU_RESOURCES,
});

/**
 * Every leaf key in a bundle set, as `namespace:key` pairs, sorted.
 *
 * The workhorse for the two completeness assertions in this task (plural
 * siblings and pseudo coverage). Sorted so a diff is readable and so the count
 * is order-independent.
 */
export function listResourceKeys(resources: NamespaceResources): readonly string[] {
  const keys: string[] = [];
  for (const namespace of Object.keys(resources).sort()) {
    const bundle = resources[namespace] as TranslationBundle;
    for (const key of flattenKeys(bundle)) {
      keys.push(`${namespace}:${key}`);
    }
  }
  return keys.sort();
}

function flattenKeys(bundle: TranslationBundle, prefix = ''): string[] {
  const out: string[] = [];
  for (const key of Object.keys(bundle).sort()) {
    const value = bundle[key];
    const path = prefix === '' ? key : `${prefix}.${key}`;
    if (value !== null && typeof value === 'object' && !Array.isArray(value)) {
      out.push(...flattenKeys(value as TranslationBundle, path));
    } else {
      out.push(path);
    }
  }
  return out;
}