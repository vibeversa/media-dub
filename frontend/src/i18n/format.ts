/**
 * Task 018's formatting surface, now backed by Task 045's single implementation.
 *
 * Nothing here formats anything: `formatDate`, `formatNumber`,
 * `getPluralCategory` and `resolveTimeZone` are re-exported from
 * `src/lib/dates` + `src/lib/formatting`, and `isRtlLocale`/`applyDirection`
 * from `src/i18n/direction.ts`. The module survives because every Task 018
 * caller (the shell, the settings form, the locale switcher) imports these
 * names from `../i18n/format.js`, and rewriting those imports to spread three
 * new paths across a dozen files would be churn with no behaviour change.
 *
 * The re-export is also the thing that keeps the duplication from coming back:
 * there is exactly one `Intl.DateTimeFormat` call site per concern in this app,
 * and it is the one with the tests.
 */

export {
  FALLBACK_TIME_ZONE,
  formatDate,
  formatDateOnly,
  formatTimeOnly,
  formatTimestamp,
  isValidTimeZone,
  resolveTimeZone,
} from '../lib/dates/dates.js';
export type { FormatDateOptions } from '../lib/dates/dates.js';

export { formatNumber, getPluralCategory } from '../lib/formatting/formatting.js';
export type { FormatNumberOptions } from '../lib/formatting/formatting.js';

export { applyDirection, baseLanguage, isRtlLocale, resolveDirection } from './direction.js';
export type { Direction } from './direction.js';