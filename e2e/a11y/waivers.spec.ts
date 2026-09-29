// Task 041C: the waiver registry's own rules.
//
// The task is explicit: "axe skip-rules require expiry dates and are rejected in
// review without one" and "Axe false positive -> manual verification recorded".
//
// A suppression list is the easiest way to make an accessibility audit look
// green while meaning nothing, and it fails in a specific way: entries
// accumulate, nobody re-reads them, and the rule underneath was fixed a year ago
// while the waiver still says it is not. So the registry is policed by its own
// tests, and the registry is currently EMPTY - which is the honest state after
// this task's fixes, and the state that makes the next waiver a deliberate act
// rather than a habit.

import { expect, test } from '@playwright/test';

import { A11Y_SCREENS, AUDITED_SCREEN_IDS, CONTRAST_THEMES } from './support/screens.js';
import { AUDIT_DATE, WAIVERS, assertWaiversAreCurrent } from './support/waivers.js';

test.describe('@a11y waiver registry', () => {
  test('@a11y every waiver carries a justification, manual verification, an issue link and an expiry', () => {
    const problems = assertWaiversAreCurrent(WAIVERS);
    expect(
      problems,
      'a waiver is missing a mandatory field, is expired, or is duplicated. The task rejects ' +
        'a skip-rule without an expiry date, and an unreviewed finding is not a false positive.',
    ).toEqual([]);
  });

  test('@a11y no waiver may name a screen the audit does not cover', () => {
    // A waiver for a screen that is not scanned hides nothing today, and starts
    // hiding things the day the screen list shrinks.
    const unknown = WAIVERS.filter(
      (waiver) => !AUDITED_SCREEN_IDS.includes(waiver.screen),
    ).map((waiver) => `${waiver.screen}/${waiver.ruleId}`);
    expect(unknown, 'a waiver names a screen the audit never scans').toEqual([]);
  });

  test('@a11y the registry is empty, and that is a reviewable state', () => {
    // Deliberately asserting the current value rather than "at most N". The
    // point is that the list is looked at on every run: the day it is not
    // empty, this test fails and the diff is the review event.
    expect(
      WAIVERS.map((waiver) => `${waiver.screen}/${waiver.ruleId} (expires ${waiver.expiresOn})`),
      'The waiver registry is no longer empty. Every entry needs an issue link, a manual ' +
        'verification note, and a date by which it will be re-checked; record them in ' +
        'e2e/a11y/README.md and bump this expectation deliberately.',
    ).toEqual([]);
  });

  test('@a11y the expiry check actually rejects an expired waiver', () => {
    // The check is worthless if it has never been seen to fail. A synthetic
    // expired entry must be reported, with the date in the message.
    const problems = assertWaiversAreCurrent(
      [
        {
          ruleId: 'color-contrast',
          screen: 'dashboard',
          justification: 'synthetic',
          manualVerification: 'synthetic',
          issue: 'https://example.invalid/issue/1',
          expiresOn: '2000-01-01',
        },
      ],
      new Date(),
    );
    expect(problems.join('\n')).toContain('expired on 2000-01-01');
  });

  test('@a11y the expiry check rejects a waiver with no issue link', () => {
    const problems = assertWaiversAreCurrent([
      {
        ruleId: 'color-contrast',
        screen: 'dashboard',
        justification: 'synthetic',
        manualVerification: 'synthetic',
        issue: '',
        expiresOn: '2099-12-31',
      },
    ]);
    expect(problems.join('\n')).toContain('issue link is required');
  });

  test('@a11y the expiry check rejects a non-ISO expiry', () => {
    const problems = assertWaiversAreCurrent([
      {
        ruleId: 'color-contrast',
        screen: 'dashboard',
        justification: 'synthetic',
        manualVerification: 'synthetic',
        issue: 'https://example.invalid/issue/1',
        expiresOn: 'next spring',
      },
    ]);
    expect(problems.join('\n')).toContain('ISO date');
  });

  test('@a11y the expiry check rejects a waiver with no manual verification', () => {
    const problems = assertWaiversAreCurrent([
      {
        ruleId: 'color-contrast',
        screen: 'dashboard',
        justification: 'synthetic',
        manualVerification: '   ',
        issue: 'https://example.invalid/issue/1',
        expiresOn: '2099-12-31',
      },
    ]);
    expect(problems.join('\n')).toContain('manualVerification is required');
  });
});

test.describe('@a11y audit coverage', () => {
  test('@a11y the matrix is 12 screens x 2 themes', () => {
    // The same guard as the axe spec's, restated where the waivers live: a
    // screen added to the matrix must be scanned, and both themes must be in.
    expect(A11Y_SCREENS.length).toBe(12);
    expect(CONTRAST_THEMES).toEqual(['light', 'dark']);
    expect(AUDITED_SCREEN_IDS).toContain('login');
    expect(AUDITED_SCREEN_IDS).toContain('exports');
  });

  test('@a11y the audit date is recorded and is a real date', () => {
    expect(Number.isNaN(Date.parse(AUDIT_DATE)), 'AUDIT_DATE must be a real date').toBe(false);
  });

  test('@a11y every audited screen records which criteria families it covers', () => {
    for (const entry of A11Y_SCREENS) {
      expect(entry.audit, `${entry.screen.id} must be axe-scanned`).toContain('axe');
      expect(entry.audit, `${entry.screen.id} must be keyboard-checked`).toContain('keyboard');
      expect(entry.audit, `${entry.screen.id} must be focus-checked`).toContain('focus');
      expect(
        entry.rigState.length,
        `${entry.screen.id} must record what the rig actually renders, so a finding on an ` +
          'empty-state screen is not read as a finding on a populated one',
      ).toBeGreaterThan(10);
    }
  });
});
