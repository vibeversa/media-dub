/**
 * Wizard language options (Task 022).
 *
 * The backend accepts any 2–3 ASCII-letter code (`ProjectService.ValidateLanguage`)
 * with source and target differing; the bundle documents no fixed list. The
 * wizard offers a curated select of common codes with plain-language labels so
 * users never type raw codes, plus validation helpers shared by the store and
 * the step components. Pure: no React, no storage, fully unit-testable.
 */

export interface LanguageOption {
  readonly value: string;
  readonly label: string;
}

/** Curated ISO 639-1 codes with human labels for the two selects. */
export const LANGUAGE_OPTIONS: readonly LanguageOption[] = [
  { value: 'en', label: 'English (en)' },
  { value: 'es', label: 'Spanish (es)' },
  { value: 'fr', label: 'French (fr)' },
  { value: 'de', label: 'German (de)' },
  { value: 'it', label: 'Italian (it)' },
  { value: 'pt', label: 'Portuguese (pt)' },
  { value: 'nl', label: 'Dutch (nl)' },
  { value: 'ru', label: 'Russian (ru)' },
  { value: 'ar', label: 'Arabic (ar)' },
  { value: 'tr', label: 'Turkish (tr)' },
  { value: 'hi', label: 'Hindi (hi)' },
  { value: 'ja', label: 'Japanese (ja)' },
  { value: 'ko', label: 'Korean (ko)' },
  { value: 'zh', label: 'Chinese (zh)' },
  { value: 'pl', label: 'Polish (pl)' },
  { value: 'uk', label: 'Ukrainian (uk)' },
];

export const DEFAULT_SOURCE_LANGUAGE = 'en';

export const DEFAULT_TARGET_LANGUAGE = 'es';

/** True for the backend shape: 2–3 ASCII letters. */
export function isLanguageCode(value: string): boolean {
  return /^[A-Za-z]{2,3}$/.test(value.trim());
}

/** True when the pair is submittable: both codes, case-insensitively different. */
export function isValidLanguagePair(source: string, target: string): boolean {
  if (!isLanguageCode(source) || !isLanguageCode(target)) {
    return false;
  }
  return source.trim().toLowerCase() !== target.trim().toLowerCase();
}
