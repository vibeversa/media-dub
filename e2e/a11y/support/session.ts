// Task 041C: the accessibility session.
//
// The a11y audit runs against the same rig, the same seeded data and the same
// in-app navigation as 041B's visual matrix, so it reuses that session helper
// rather than growing a second boot path. A second sign-in would burn the login
// rate limit (5/min per IP) that 041B already has to work around, and a second
// notion of "ready" is a second thing to keep correct.
//
// What this file adds on top of 041B's session:
//
//   * **Two motion contexts.** R3 is about `prefers-reduced-motion`, so the
//     audit needs a page that *has* motion and a page that has none, and it
//     needs to compare them. `reducedMotion: 'reduce'` is already how 041B
//     renders; a11y needs both, so `openA11ySession` builds the reduced-motion
//     context explicitly and `openMotionContext` builds a second one with motion
//     enabled.
//
//   * **Announcement capture.** R5 needs to count what a screen reader would be
//     told, not just what is in the DOM. `captureAnnouncements` installs a
//     MutationObserver over every live region and records the text each
//     mutation makes available, so "announced once" is a measurement.
//
//   * **Live-region reads.** `readLiveRegions` is the static counterpart: what
//     live regions exist, what politeness they declare, and whether they are
//     inside an `aria-busy` subtree (which suppresses announcement entirely).

import type { Browser, BrowserContext, Page } from '@playwright/test';

import type { DirectionName, ThemeName } from '../../visual/support/matrix.js';
import {
  applyDirectionViaShell,
  applyThemeViaShell,
  navigateInApp,
  openVisualSession,
  readAxes,
  renderShelllessScreen,
  waitForContentStable,
  type VisualAxes,
  type VisualSession,
} from '../../visual/support/session.js';

export {
  applyDirectionViaShell,
  applyThemeViaShell,
  navigateInApp,
  readAxes,
  waitForContentStable,
};
export type { VisualAxes, VisualSession };

// `DirectionName`/`ThemeName` are re-exported from the matrix rather than from
// the visual session, because the session module *uses* those types without
// declaring them and so does not export them. Going through the matrix keeps one
// definition of each name.

/** WCAG 2.2's AA minimum for body text (SC 1.4.3). */
export const AA_CONTRAST_RATIO = 4.5;

/** AA for large text (SC 1.4.3 exception): >= 18pt, or >= 14pt bold. */
export const AA_LARGE_TEXT_RATIO = 3;

export interface A11ySession {
  readonly visual: VisualSession;
  /** Motion disabled (`prefers-reduced-motion: reduce`). The default for audits. */
  readonly page: Page;
  /** Never authenticated, for the login screen. */
  readonly anonPage: Page;
  /**
   * A second context with `reducedMotion: 'no-preference'`, sharing nothing with
   * `page`. Used only to prove that motion *is* present without the preference
   * and absent with it - comparing two pages from different contexts avoids the
   * trap of toggling a media feature on one page and misreading leftover state.
   */
  readonly motionPage: () => Promise<Page>;
  readonly close: () => Promise<void>;
}

/**
 * Opens the audit session.
 *
 * The reduced-motion context is the one 041B already uses, so the audit sees
 * exactly the pixels 041B pinned - a contrast finding here and a visual diff
 * there describe the same page.
 */
export async function openA11ySession(browser: Browser): Promise<A11ySession> {
  const visual = await openVisualSession(browser);
  const motionContextRef: { current: BrowserContext | null } = { current: null };

  // One viewport for the whole audit, set explicitly rather than inherited.
  // Contrast, focus-ring geometry and reachable-set size all depend on layout,
  // so an audit whose width came from a shared default would quietly change
  // meaning the next time that default moved. 1440 is the visual matrix's
  // desktop width, so the two gates describe the same page.
  const AUDIT_VIEWPORT = { width: 1440, height: 900 } as const;
  await visual.page.setViewportSize(AUDIT_VIEWPORT);
  await visual.anonPage.setViewportSize(AUDIT_VIEWPORT);

  return {
    visual,
    page: visual.page,
    anonPage: visual.anonPage,
    motionPage: async (): Promise<Page> => {
      if (motionContextRef.current === null) {
        motionContextRef.current = await browser.newContext({
          reducedMotion: 'no-preference',
          deviceScaleFactor: 1,
          locale: 'en-US',
          timezoneId: 'UTC',
        });
      }
      const page = await motionContextRef.current.newPage();
      // Same pinned instant as the visual matrix, so a date rendered on the
      // motion-enabled page is comparable with the one on the reduced page.
      await page.clock.setFixedTime(new Date('2026-01-15T12:00:00.000Z'));
      await page.setViewportSize({ width: 1440, height: 900 });
      return page;
    },
    close: async () => {
      await motionContextRef.current?.close();
      await visual.close();
    },
  };
}

/**
 * Signs a page in through the real login form, by keyboard only.
 *
 * R1 requires flows to be completable without a mouse, so the audit's own
 * sign-in is driven by Tab/Enter rather than by `fill()`. `fill()` sets a value
 * without ever focusing the field, which would mean a login path that only
 * appears to work from the keyboard.
 */
export async function signInWithKeyboard(
  page: Page,
  identity: { readonly tenantId: string; readonly externalSubject: string },
): Promise<void> {
  await page.goto('/login', { waitUntil: 'domcontentloaded' });
  await page.getByTestId('page-login').waitFor({ state: 'visible', timeout: 30_000 });

  await focusAndType(page, 'auth-tenant-id', identity.tenantId);
  await focusAndType(page, 'auth-external-subject', identity.externalSubject);

  // The submit control must be reachable by keyboard from the previous field.
  // Tab moves focus; Enter activates. If the control were unreachable, this
  // would time out on the shell never appearing - which is the assertion.
  await page.keyboard.press('Tab');
  const onSubmit = await page.evaluate(() => document.activeElement?.getAttribute('data-testid'));
  if (onSubmit !== 'auth-submit') {
    throw new Error(
      `After the subject field, Tab landed on '${onSubmit ?? 'nothing'}' rather than the submit ` +
        "control. The sign-in form is not keyboard-complete (WCAG 2.1.1).",
    );
  }
  await page.keyboard.press('Enter');
  await page.getByTestId('app-shell').waitFor({ state: 'visible', timeout: 30_000 });
}

/** Tabs forward until `testId` has focus, then types into it. */
async function focusAndType(page: Page, testId: string, value: string): Promise<void> {
  const target = page.getByTestId(testId);
  await target.waitFor({ state: 'visible', timeout: 30_000 });

  for (let attempt = 0; attempt < 40; attempt += 1) {
    const focused = await page.evaluate(
      (id) => document.activeElement?.getAttribute('data-testid') === id,
      testId,
    );
    if (focused) {
      await page.keyboard.type(value, { delay: 0 });
      return;
    }
    await page.keyboard.press('Tab');
  }
  throw new Error(`Could not reach '${testId}' with the keyboard in 40 tab stops.`);
}

/** Tabs forward until `testId` has focus. Does not activate it. */
export async function tabTo(page: Page, testId: string, maxStops = 60): Promise<void> {
  const target = page.getByTestId(testId);
  await target.waitFor({ state: 'visible', timeout: 30_000 });

  for (let attempt = 0; attempt < maxStops; attempt += 1) {
    const focused = await page.evaluate(
      (id) => document.activeElement?.getAttribute('data-testid') === id,
      testId,
    );
    if (focused) {
      return;
    }
    await page.keyboard.press('Tab');
  }
  throw new Error(`Could not reach '${testId}' with the keyboard in ${maxStops} tab stops.`);
}

/**
 * Presses Tab at most `maxStops` times, collecting every element focus lands on.
 *
 * The reachable set, in order. Stops when focus returns to the first stop,
 * because a page with three controls would otherwise produce `maxStops`
 * repetitions of those three - which is what a page with *no* controls would
 * also produce. Wrap-around is the signal that the walk is complete, and without
 * it a small screen looks like a large one.
 *
 * The name is computed the way a screen reader would: `aria-labelledby`, then
 * `aria-label`, then an associated `<label>`, then the element's own text. Using
 * `textContent` alone would report every `<input>` as unnamed, which is both
 * wrong and useless.
 */
export async function walkTabOrder(page: Page, maxStops = 300): Promise<TabStop[]> {
  const stops: TabStop[] = [];
  let firstKey: string | null = null;

  for (let index = 0; index < maxStops; index += 1) {
    await page.keyboard.press('Tab');
    const stop = await page.evaluate(() => {
      const active = document.activeElement;
      if (active === null || active === document.body) {
        return null;
      }
      const styles = window.getComputedStyle(active);
      const rect = active.getBoundingClientRect();

      // Accessible name, in the order a screen reader resolves it.
      const labelledBy = active.getAttribute('aria-labelledby');
      let name = '';
      if (labelledBy !== null && labelledBy !== '') {
        name = labelledBy
          .split(/\s+/)
          .map((id) => document.getElementById(id)?.textContent ?? '')
          .join(' ');
      }
      if (name.trim() === '') {
        const ariaLabel = active.getAttribute('aria-label');
        if (ariaLabel !== null && ariaLabel !== '') {
          name = ariaLabel;
        }
      }
      if (name.trim() === '') {
        const labelled = (active as HTMLInputElement).labels;
        if (labelled !== null && labelled !== undefined && labelled.length > 0) {
          name = [...labelled].map((node) => node.textContent ?? '').join(' ');
        }
      }
      if (name.trim() === '' && active instanceof HTMLInputElement && active.type === 'submit') {
        name = active.value;
      }
      if (name.trim() === '') {
        name = active.textContent ?? '';
      }
      name = name.replace(/\s+/g, ' ').trim().slice(0, 80);

      const title = active.getAttribute('title');

      return {
        key: `${active.tagName.toLowerCase()}|${active.getAttribute('data-testid') ?? ''}|${name}`,
        tag: active.tagName.toLowerCase(),
        testId: active.getAttribute('data-testid'),
        type: active.getAttribute('type'),
        name,
        title,
        tabIndex: active.getAttribute('tabindex'),
        disabled: (active as HTMLInputElement).disabled === true,
        hidden:
          styles.display === 'none' ||
          styles.visibility === 'hidden' ||
          (rect.width === 0 && rect.height === 0),
        outlineStyle: styles.outlineStyle,
        outlineWidth: styles.outlineWidth,
        outlineColor: styles.outlineColor,
        outlineOffset: styles.outlineOffset,
        boxShadow: styles.boxShadow,
        borderColor: styles.borderColor,
        backgroundColor: styles.backgroundColor,
        href: active.getAttribute('href'),
        inLiveRegion: active.closest('[aria-live], [role="status"], [role="alert"]') !== null,
        html: active.outerHTML.slice(0, 400),
        ancestors: ((): string[] => {
          const chain: string[] = [];
          let node: HTMLElement | null = active.parentElement;
          for (let depth = 0; depth < 3 && node !== null; depth += 1) {
            const testId = node.getAttribute('data-testid');
            chain.push(
              `${node.tagName.toLowerCase()}${testId === null ? '' : `[${testId}]`}`,
            );
            node = node.parentElement;
          }
          return chain;
        })(),
      };
    });

    if (stop === null) {
      continue;
    }
    if (firstKey !== null && stop.key === firstKey) {
      // Wrapped. The set is complete.
      return stops;
    }
    if (firstKey === null) {
      firstKey = stop.key;
    }
    stops.push(stop);
  }

  return stops;
}

export interface TabStop {
  /** Identity used only for wrap-around detection. */
  readonly key: string;
  readonly tag: string;
  readonly testId: string | null;
  readonly type: string | null;
  /** Accessible name, as a screen reader would resolve it. */
  readonly name: string;
  readonly title: string | null;
  readonly tabIndex: string | null;
  readonly disabled: boolean;
  readonly hidden: boolean;
  readonly outlineStyle: string;
  readonly outlineWidth: string;
  readonly outlineColor: string;
  readonly outlineOffset: string;
  readonly boxShadow: string;
  readonly borderColor: string;
  readonly backgroundColor: string;
  readonly href: string | null;
  readonly inLiveRegion: boolean;
  /** Outer HTML, so a failing assertion can name the control. */
  readonly html: string;
  /** Up to three ancestor elements, for the same reason. */
  readonly ancestors: readonly string[];
}

/**
 * Renders the shell-less login screen for the audit.
 *
 * The login screen has no shell, so the theme and locale switchers do not exist
 * on it - the axes are set the way the app itself restores them, through
 * `localStorage` and a load, which is free because there is no session to lose.
 * Wrapped from 041B's helper so the a11y suite has one name for the operation
 * and cannot drift from it.
 */
export async function renderShelllessForAudit(
  page: Page,
  path: string,
  theme: ThemeName,
  direction: DirectionName,
  readyTestId: string,
): Promise<VisualAxes> {
  return renderShelllessScreen(page, path, theme, direction, readyTestId);
}

export interface AnnouncementRecord {
  /** ISO timestamp of the mutation. */
  readonly at: string;
  /** `aria-live` politeness of the region, or the implicit role's. */
  readonly politeness: string;
  /** A stable-ish identifier for the region, for grouping. */
  readonly region: string;
  /** The text the mutation made available to assistive technology. */
  readonly text: string;
}

export interface AnnouncementCapture {
  /** Reads and clears the recorded announcements. */
  readonly drain: () => Promise<AnnouncementRecord[]>;
  /** Reads without clearing. */
  readonly peek: () => Promise<AnnouncementRecord[]>;
  readonly stop: () => Promise<void>;
}

/**
 * Installs an observer that records every text change inside a live region.
 *
 * What a screen reader announces is not a DOM concept the page can be asked
 * about, so it is measured: every mutation that adds or changes text inside an
 * element with `aria-live`, `role="status"` or `role="alert"` is recorded with
 * the resulting text. That is exactly the set of things that become
 * announcements, so "completion announced once" becomes a count.
 *
 * `role="status"` implies `aria-live="polite"` and `role="alert"` implies
 * `aria-live="assertive"` when the attribute is absent, so the implicit value is
 * applied here rather than letting a region be classified as "silent" purely
 * because it relied on its role.
 */
export async function captureAnnouncements(page: Page): Promise<AnnouncementCapture> {
  await page.evaluate(() => {
    interface Captured {
      at: string;
      politeness: string;
      region: string;
      text: string;
    }
    const store: Captured[] = [];
    (window as unknown as { __announcements: Captured[] }).__announcements = store;

    const politenessOf = (element: Element): string => {
      const live = element.getAttribute('aria-live');
      if (live !== null && live !== '') {
        return live;
      }
      const role = element.getAttribute('role');
      if (role === 'alert' || role === 'assertive') {
        return 'assertive';
      }
      return 'polite';
    };

    const regionKey = (element: Element): string => {
      const testId = element.getAttribute('data-testid');
      if (testId !== null && testId !== '') {
        return testId;
      }
      const id = element.getAttribute('id');
      if (id !== null && id !== '') {
        return `#${id}`;
      }
      const label = element.getAttribute('aria-label');
      if (label !== null && label !== '') {
        return `[aria-label="${label}"]`;
      }
      return element.tagName.toLowerCase();
    };

    const record = (node: Node): void => {
      // A live region is announced for text inside it, including text inside
      // descendants, so the closest live ancestor is what matters.
      const target = node.nodeType === 1 ? (node as Element) : node.parentElement;
      if (target === null) {
        return;
      }
      const live = target.closest('[aria-live], [role="status"], [role="alert"]');
      if (live === null) {
        return;
      }
      // An `aria-busy` ancestor means the region is not announcing yet; recording
      // those would count messages a user never heard.
      if (live.closest('[aria-busy="true"]') !== null) {
        return;
      }
      store.push({
        at: new Date().toISOString(),
        politeness: politenessOf(live),
        region: regionKey(live),
        text: (live.textContent ?? '').replace(/\s+/g, ' ').trim(),
      });
    };

    const observer = new MutationObserver((records) => {
      for (const entry of records) {
        if (entry.type === 'characterData') {
          record(entry.target);
          continue;
        }
        for (const added of entry.addedNodes) {
          record(added);
        }
        for (const removed of entry.removedNodes) {
          // A removal makes the region quieter, not louder. Recording it would
          // inflate every dismissal into a phantom announcement.
          if (removed.nodeType === 1 && (removed.textContent ?? '').trim() !== '') {
            continue;
          }
        }
      }
    });

    observer.observe(document.body, {
      subtree: true,
      childList: true,
      characterData: true,
    });
    (window as unknown as { __announcementObserver: MutationObserver }).__announcementObserver = observer;
  });

  return {
    drain: async (): Promise<AnnouncementRecord[]> => {
      const all = await peekAll(page);
      await page.evaluate(() => {
        (window as unknown as { __announcements: unknown[] }).__announcements = [];
      });
      return all;
    },
    peek: async (): Promise<AnnouncementRecord[]> => peekAll(page),
    stop: async (): Promise<void> => {
      await page.evaluate(() => {
        const observer = (window as unknown as { __announcementObserver?: MutationObserver })
          .__announcementObserver;
        observer?.disconnect();
        delete (window as unknown as { __announcementObserver?: MutationObserver }).__announcementObserver;
      });
    },
  };
}

async function peekAll(page: Page): Promise<AnnouncementRecord[]> {
  return page.evaluate(
    () => (window as unknown as { __announcements?: AnnouncementRecord[] }).__announcements ?? [],
  );
}

export interface LiveRegionInfo {
  readonly region: string;
  readonly politeness: string;
  readonly atomic: string | null;
  readonly relevant: string | null;
  readonly text: string;
  readonly suppressedByBusy: boolean;
}

/** Enumerates every live region currently in the document. */
export async function readLiveRegions(page: Page): Promise<LiveRegionInfo[]> {
  return page.evaluate(() => {
    const nodes = [...document.querySelectorAll('[aria-live], [role="status"], [role="alert"]')];
    return nodes.map((element) => {
      const role = element.getAttribute('role');
      const declared = element.getAttribute('aria-live');
      const politeness =
        declared !== null && declared !== ''
          ? declared
          : role === 'alert'
            ? 'assertive'
            : role === 'status'
              ? 'polite'
              : 'off';
      const testId = element.getAttribute('data-testid');
      const id = element.getAttribute('id');
      const label = element.getAttribute('aria-label');
      // The same key order as `captureAnnouncements`' `regionKey`. Two helpers
      // that name the same element differently make a cross-referencing
      // assertion impossible - the toast region is a bare `<div>` with only an
      // `aria-label`, and "div" is not an answer.
      const region =
        testId !== null && testId !== ''
          ? testId
          : id !== null && id !== ''
            ? `#${id}`
            : label !== null && label !== ''
              ? `[aria-label="${label}"]`
              : element.tagName.toLowerCase();
      return {
        region,
        politeness,
        atomic: element.getAttribute('aria-atomic'),
        relevant: element.getAttribute('aria-relevant'),
        text: (element.textContent ?? '').replace(/\s+/g, ' ').trim().slice(0, 200),
        suppressedByBusy: element.closest('[aria-busy="true"]') !== null,
      };
    });
  });
}

/** Focus ring properties as the stylesheet computes them, for the focused element. */
export interface FocusIndicator {
  readonly tag: string;
  readonly testId: string | null;
  readonly name: string;
  readonly outlineStyle: string;
  readonly outlineWidth: string;
  readonly outlineColor: string;
  readonly outlineOffset: string;
  readonly boxShadow: string;
  readonly borderColor: string;
  readonly backgroundColor: string;
  /** True when the indicator is a real, visible change rather than `none`. */
  readonly visible: boolean;
}

/** Measures the focus indicator on whatever currently has focus. */
export async function readFocusIndicator(page: Page): Promise<FocusIndicator | null> {
  return page.evaluate(() => {
    const active = document.activeElement;
    if (active === null || active === document.body) {
      return null;
    }
    const styles = window.getComputedStyle(active);
    const px = (value: string): number => Number.parseFloat(value) || 0;
    const outlineWidth = px(styles.outlineWidth);
    const outlineVisible =
      styles.outlineStyle !== 'none' && outlineWidth > 0 && styles.outlineColor !== 'transparent';
    const shadowVisible = styles.boxShadow !== 'none' && styles.boxShadow !== '';
    const label = active.getAttribute('aria-label');
    const name =
      label !== null && label !== ''
        ? label
        : (active.textContent ?? '').replace(/\s+/g, ' ').trim().slice(0, 60);

    return {
      tag: active.tagName.toLowerCase(),
      testId: active.getAttribute('data-testid'),
      name,
      outlineStyle: styles.outlineStyle,
      outlineWidth: styles.outlineWidth,
      outlineColor: styles.outlineColor,
      outlineOffset: styles.outlineOffset,
      boxShadow: styles.boxShadow,
      borderColor: styles.borderColor,
      backgroundColor: styles.backgroundColor,
      visible: outlineVisible || shadowVisible,
    };
  });
}

/** The accessible name Playwright computes, for a locator, or `''` when none. */
export async function accessibleNameOf(page: Page, testId: string): Promise<string> {
  const locator = page.getByTestId(testId).first();
  return (await locator.evaluate((element) => {
    const labelled = element.getAttribute('aria-labelledby');
    if (labelled !== null && labelled !== '') {
      const text = labelled
        .split(/\s+/)
        .map((id) => document.getElementById(id)?.textContent ?? '')
        .join(' ')
        .replace(/\s+/g, ' ')
        .trim();
      if (text !== '') {
        return text;
      }
    }
    const label = element.getAttribute('aria-label');
    if (label !== null && label !== '') {
      return label.trim();
    }
    if (
      element instanceof HTMLInputElement ||
      element instanceof HTMLSelectElement ||
      element instanceof HTMLTextAreaElement
    ) {
      // `labels` is nullable on the union, so it is normalised once rather than
      // narrowed three times. A form control with no `<label>` has no accessible
      // name from this mechanism, and the caller asserts it has one from some
      // mechanism.
      const labels = element.labels;
      if (labels !== null && labels !== undefined && labels.length > 0) {
        return Array.from(labels)
          .map((node) => (node.textContent ?? '').replace(/\s+/g, ' ').trim())
          .join(' ')
          .trim();
      }
      const placeholder = element.getAttribute('placeholder');
      if (placeholder !== null && placeholder !== '') {
        return placeholder.trim();
      }
    }
    if (element instanceof HTMLInputElement && element.type === 'submit') {
      return element.value.trim();
    }
    return (element.textContent ?? '').replace(/\s+/g, ' ').trim();
  })) ?? '';
}

export { PROJECT_ID } from '../../visual/support/matrix.js';
export type { DirectionName, ThemeName } from '../../visual/support/matrix.js';
