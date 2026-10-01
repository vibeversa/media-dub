/**
 * Missing-translation collector (Task 045, instruction 4 / Edge cases).
 *
 * WHY THIS EXISTS ALONGSIDE TELEMETRY
 * -----------------------------------
 * `missingKeyHandler` already reports to telemetry (`trackMissingTranslation`,
 * Task 018). That is the right channel for production: allowlisted scalars, a
 * kill switch, a bounded buffer. But telemetry is exactly the wrong channel for
 * the pseudo-locale run, because R4 needs the pseudo test to **fail** on a new
 * missing key and telemetry is gated on a build flag plus a user opt-out — a
 * suite that only asserts on telemetry asserts nothing whenever the flag is off.
 *
 * So there are two observers on one event:
 *   - telemetry, for production (allowlisted, opt-out-able, bounded), and
 *   - this collector, for tests: in-process, never gated, `reset()`-able.
 *
 * It is deliberately *bounded*. A long-lived tab with a bad bundle could
 * otherwise record the same key thousands of times; the ring keeps the first
 * `MAX` reports, which is all a test needs and all a diagnostic needs.
 *
 * IT NEVER AFFECTS WHAT RENDERS
 * -----------------------------
 * Recording is a side effect of a miss; the render path is untouched. A missing
 * key resolves through `fallbackLng` (`en`) and, failing that, renders the key
 * name — never a blank string, never a thrown error, never a blank screen.
 */

export interface MissingTranslationReport {
  /** The locale i18next was resolving for. Never empty. */
  readonly locale: string;
  /** Namespace i18next asked in. Never empty. */
  readonly namespace: string;
  /** The key it could not resolve. */
  readonly key: string;
}

/**
 * How many reports to keep. Comfortably above "one run of one screen" and
 * comfortably below "a tab open for a week".
 */
export const MAX_MISSING_TRANSLATION_REPORTS = 500;

let reports: MissingTranslationReport[] = [];

/**
 * Records one miss. Called from i18next's `missingKeyHandler`; exported because a
 * test that wants to exercise the render fallback can seed it directly.
 *
 * Every field is normalised to a non-empty string: a report with an empty locale
 * or namespace is useless for triage and would make a "no missing keys" assertion
 * ambiguous.
 */
export function recordMissingTranslation(report: MissingTranslationReport): void {
  if (reports.length >= MAX_MISSING_TRANSLATION_REPORTS) {
    return;
  }
  reports.push({
    locale: report.locale === '' ? 'en' : report.locale,
    namespace: report.namespace === '' ? 'common' : report.namespace,
    key: report.key,
  });
}

/** Every report since the last `resetMissingTranslations()`, oldest first. */
export function getMissingTranslations(): readonly MissingTranslationReport[] {
  return reports;
}

/** How many reports are buffered. The number an assertion usually wants. */
export function countMissingTranslations(): number {
  return reports.length;
}

/**
 * Drops every buffered report.
 *
 * Required rather than optional: vitest reuses one module registry per test
 * *file*, so a suite that renders several screens would otherwise accumulate the
 * first screen's misses and fail the second. `resetMissingTranslations()` in
 * `beforeEach` is the contract.
 */
export function resetMissingTranslations(): void {
  reports = [];
}

/**
 * `namespace:key` pairs for every buffered report, deduplicated and sorted.
 * The shape a failure message wants — one line per distinct missing key.
 */
export function missingTranslationKeys(): readonly string[] {
  return [...new Set(reports.map((report) => `${report.namespace}:${report.key}`))].sort();
}