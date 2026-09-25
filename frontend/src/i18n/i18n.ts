import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import arCommon from './locales/ar/common.json';
import arNav from './locales/ar/nav.json';
import enAuth from './locales/en/auth.json';
import enCommon from './locales/en/common.json';
import enDashboard from './locales/en/dashboard.json';
import enErrors from './locales/en/errors.json';
import enNav from './locales/en/nav.json';
import enProjects from './locales/en/projects.json';
import enProcessing from './locales/en/processing.json';
import enUploads from './locales/en/uploads.json';
import enWorkspace from './locales/en/workspace.json';
import enTranscript from './locales/en/transcript.json';
import ruCommon from './locales/ru/common.json';
import { trackMissingTranslation } from '../telemetry/telemetry.js';

export const FALLBACK_LOCALE = 'en';
export const SUPPORTED_LOCALES = ['en', 'ar', 'ru'] as const;
export type SupportedLocale = (typeof SUPPORTED_LOCALES)[number];

/**
 * i18next init (Task 018): `en` baseline with namespaced JSON bundles
 * (common, nav, auth, dashboard, projects, processing, uploads, workspace, transcript, errors), `ar`/`ru` partial
 * bundles for RTL + plural coverage, `en` fallback for every missing key
 * (never blank strings). Plural categories follow ICU via Intl.PluralRules
 * (`_zero/_one/_two/_few/_many/_other` suffixes). Synchronous init
 * (`initImmediate: false`) with inline resources so unit tests and the shell
 * render translated strings on first paint.
 */
void i18n.use(initReactI18next).init({
  resources: {
    en: {
      common: enCommon,
      nav: enNav,
      auth: enAuth,
      dashboard: enDashboard,
      projects: enProjects,
      processing: enProcessing,
      uploads: enUploads,
      workspace: enWorkspace,
      transcript: enTranscript,
      errors: enErrors,
    },
    ar: {
      common: arCommon,
      nav: arNav,
    },
    ru: {
      common: ruCommon,
    },
  },
  lng: 'en',
  fallbackLng: FALLBACK_LOCALE,
  supportedLngs: [...SUPPORTED_LOCALES],
  defaultNS: 'common',
  // Feature code always uses namespaced keys (`nav:dashboard`); a bare key
  // resolves against `common` instead of rendering the key itself blank.
  ns: ['common', 'nav', 'auth', 'dashboard', 'projects', 'processing', 'uploads', 'workspace', 'transcript', 'errors'],
  interpolation: {
    // React already escapes; double-escaping would corrupt ICU placeholders.
    escapeValue: false,
  },
  returnEmptyString: false,
  initAsync: false,
  saveMissing: false,
  missingKeyHandler: (lngs, _ns, key) => {
    const lng = Array.isArray(lngs) ? (lngs[0] ?? FALLBACK_LOCALE) : FALLBACK_LOCALE;
    trackMissingTranslation({ locale: lng, key });
  },
});

export default i18n;
