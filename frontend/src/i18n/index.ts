// Task 045 i18n barrel.
//
// `useLocale` is re-exported for the same reason every other hook in this repo is
// (and `src/app/__tests__/barrels.test.ts` pins it): the barrel is the module a
// feature imports, and a hook that lives behind a deep path is a hook that
// eventually gets deep-imported from three places instead.

export { default as i18n, FALLBACK_LOCALE, SUPPORTED_LOCALES, missingKeyLocale } from './i18n.js';
export type { SupportedLocale } from './i18n.js';
export * from './format.js';
export * from './direction.js';
export * from './resources.js';
export * from './missingKeys.js';
export * from './localePreference.js';
export * from './useLocale.js';
export {
  PSEUDO_BRACKETS,
  PSEUDO_EXPANSION_RATIO,
  PSEUDO_LOCALE,
  buildPseudoResources,
  pseudoLocalize,
} from './locales/pseudo.js';