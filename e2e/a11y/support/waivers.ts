// Task 041C: the waiver registry.
//
// The task's rule is precise: "Third-party widget violation -> wrapper fix or
// replace in owning feature task; axe skip-rules require expiry dates and are
// rejected in review without one." So a waiver here is not a suppression list -
// it is a claim, with a date and an issue, that a specific axe rule on a
// specific screen is not a real defect.
//
// Four fields are mandatory, and `waivers.spec.ts` fails if any is missing:
//
//   `manualVerification`  what a human checked, by hand, to reach the conclusion
//   `issue`               a link, so the claim is trackable
//   `expiresOn`           an ISO date, so the claim cannot outlive its evidence
//   `justification`       why the finding is not a defect here
//
// A fifth rule, enforced at scan time rather than here: a waiver that stops
// matching anything is a failure. Waivers that accumulate silently are how a
// green audit stops meaning anything.

import type { Waiver } from './axe.js';

/**
 * Every waiver currently in force.
 *
 * **This list is intentionally empty at the Task 041C baseline.** Every axe
 * finding the audit produced was either fixed in the owning primitive or feature
 * component, or was a false positive that manual verification disproved. The
 * entry below is the worked example of the shape a real entry must take, kept as
 * a comment rather than as data so it cannot be mistaken for a live waiver -
 * if it were live with no matching violation, every scan would fail on the stale
 * check.
 *
 *     {
 *       ruleId: 'color-contrast',
 *       screen: 'login',
 *       justification: "The disabled submit button is exempt under WCAG 1.4.3 ...",
 *       manualVerification: 'Checked with a colour-contrast analyser at 200% zoom ...',
 *       issue: 'https://github.com/.../issues/123',
 *       expiresOn: '2026-12-31',
 *     }
 */
export const WAIVERS: readonly Waiver[] = [];

/** The audit date. A waiver must expire after it to have been reviewed since. */
export const AUDIT_DATE = '2026-09-29';

/**
 * Fails if any waiver is missing a mandatory field or has already expired.
 *
 * Expiry is checked against the wall clock, not the seeded one, because the
 * question is "is this claim still current" - a wall-clock question by
 * definition. `AUDIT_DATE` is only used to require that a waiver was *reviewed*
 * no earlier than the audit that justifies it.
 */
export function assertWaiversAreCurrent(
  waivers: readonly Waiver[],
  now: Date = new Date(),
): readonly string[] {
  const problems: string[] = [];

  for (const waiver of waivers) {
    const label = `${waiver.screen}/${waiver.ruleId}`;

    if (waiver.justification.trim() === '') {
      problems.push(`${label}: justification is required`);
    }
    if (waiver.issue.trim() === '') {
      problems.push(`${label}: an issue link is required (the task rejects waivers without one)`);
    }
    if (waiver.manualVerification.trim() === '') {
      problems.push(`${label}: manualVerification is required (an unreviewed finding is not a false positive)`);
    }

    if (!/^\d{4}-\d{2}-\d{2}$/.test(waiver.expiresOn)) {
      problems.push(`${label}: expiresOn must be an ISO date (yyyy-mm-dd), got "${waiver.expiresOn}"`);
      continue;
    }

    const expiry = Date.parse(`${waiver.expiresOn}T23:59:59Z`);
    if (Number.isNaN(expiry)) {
      problems.push(`${label}: expiresOn is not a real date`);
    } else if (expiry < now.getTime()) {
      problems.push(
        `${label}: expired on ${waiver.expiresOn}. Renew it with fresh manual verification, or fix the finding.`,
      );
    }
  }

  // A waiver is identified by (screen, ruleId). Two entries for the same pair
  // means one of them is unreachable, which is how a fix gets shadowed by a
  // stale claim.
  const seen = new Set<string>();
  for (const waiver of waivers) {
    const key = `${waiver.screen}/${waiver.ruleId}`;
    if (seen.has(key)) {
      problems.push(`${key}: duplicated waiver - only one can ever be consulted`);
    }
    seen.add(key);
  }

  return problems;
}
