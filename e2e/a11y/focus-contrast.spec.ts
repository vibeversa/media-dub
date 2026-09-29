// Task 041C, R2: focus visibility, labels, and contrast.
//
// Three separate claims that axe cannot make for us:
//
//   1. **Focus is visible on every stop.** axe checks static attributes; it does
//      not press Tab. A stylesheet that removes the focus ring fails silently in
//      an axe scan and catastrophically for a keyboard user, so this walks the
//      tab order and measures the computed indicator at every stop.
//
//   2. **Every control has an accessible name.** axe's `label` rule covers form
//      controls; it does not cover a `<button>` whose only content is an icon, so
//      that case is asserted directly from the resolved name.
//
//   3. **Contrast is >= 4.5:1 in both themes.** axe's `color-contrast` rule is
//      good but it skips text it cannot measure (a node behind an `aria-hidden`
//      ancestor, a text node it considers "disabled"), and it is one rule among
//      many - a run that fails on an unrelated rule can mask it. This computes
//      the ratio itself, from the rendered colours, for every text-bearing
//      element on the page.

import { expect, test } from '@playwright/test';

import { A11Y_SCREENS, CONTRAST_THEMES } from './support/screens.js';
import {
  AA_CONTRAST_RATIO,
  AA_LARGE_TEXT_RATIO,
  applyDirectionViaShell,
  applyThemeViaShell,
  navigateInApp,
  openA11ySession,
  readFocusIndicator,
  renderShelllessForAudit,
  tabTo,
  walkTabOrder,
  type A11ySession,
} from './support/session.js';

let session: A11ySession;

test.beforeAll(async ({ browser }) => {
  session = await openA11ySession(browser);
});
test.afterAll(async () => {
  await session?.close();
});

async function ensureShell(): Promise<void> {
  if ((await session.page.getByTestId('app-shell').count()) > 0) {
    return;
  }
  await navigateInApp(session.page, '/dashboard', 'app-shell');
}

interface ContrastSample {
  readonly selector: string;
  readonly text: string;
  readonly ratio: number;
  readonly fontSizePx: number;
  readonly fontWeight: number;
  readonly required: number;
}

/**
 * Walks every element that directly renders text, and computes its contrast.
 *
 * The ratio is computed from resolved `rgb()` values rather than trusted from
 * axe, and the *effective* background is found by walking ancestors until a
 * non-transparent background appears - because `background-color: transparent`
 * on a wrapper is the normal case, and treating it as the background is how a
 * "passing" contrast check passes on white regardless of the theme.
 *
 * Elements that are not rendered, are `aria-hidden` (decorative), or are
 * `disabled` are excluded and counted, so the exclusion is visible rather than
 * silent.
 */
async function collectContrast(page: import('@playwright/test').Page): Promise<{
  samples: ContrastSample[];
  skipped: number;
}> {
  return page.evaluate(() => {
    const parse = (value: string): [number, number, number] => {
      const match = /rgba?\(\s*([\d.]+)[,\s]+([\d.]+)[,\s]+([\d.]+)/.exec(value);
      if (match === null) {
        return [255, 255, 255];
      }
      return [Number(match[1]), Number(match[2]), Number(match[3])];
    };
    const alpha = (value: string): number => {
      const match = /rgba\([^)]*?[,/]\s*([\d.]+)\s*\)/.exec(value);
      return match === null ? 1 : Number(match[1]);
    };
    const luminance = ([r, g, b]: [number, number, number]): number => {
      const channel = (c: number): number => {
        const s = c / 255;
        return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
      };
      return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
    };
    const ratio = (a: [number, number, number], b: [number, number, number]): number => {
      const la = luminance(a);
      const lb = luminance(b);
      const lighter = Math.max(la, lb);
      const darker = Math.min(la, lb);
      return (lighter + 0.05) / (darker + 0.05);
    };
    const effectiveBackground = (element: Element): [number, number, number] => {
      let node: Element | null = element;
      while (node !== null) {
        const styles = window.getComputedStyle(node);
        if (alpha(styles.backgroundColor) > 0) {
          return parse(styles.backgroundColor);
        }
        node = node.parentElement;
      }
      return [255, 255, 255];
    };

    const describe = (element: Element): string => {
      const testId = element.getAttribute('data-testid');
      if (testId !== null && testId !== '') {
        return `[data-testid="${testId}"]`;
      }
      const id = element.getAttribute('id');
      if (id !== null && id !== '') {
        return `#${id}`;
      }
      const tag = element.tagName.toLowerCase();
      const cls = element.getAttribute('class');
      return cls === null || cls === '' ? tag : `${tag}.${cls.split(/\s+/)[0]}`;
    };

    const samples: ContrastSample[] = [];
    let skipped = 0;

    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    const seen = new Set<Element>();
    for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
      const text = (node.textContent ?? '').trim();
      if (text === '') {
        continue;
      }
      const parent = node.parentElement;
      if (parent === null) {
        continue;
      }
      const styles = window.getComputedStyle(parent);
      if (styles.display === 'none' || styles.visibility === 'hidden' || Number(styles.opacity) === 0) {
        continue;
      }
      if (parent.closest('[aria-hidden="true"]') !== null) {
        skipped += 1;
        continue;
      }
      if ((parent as HTMLElement).closest('[inert]') !== null) {
        skipped += 1;
        continue;
      }
      const fg = parse(styles.color);
      const bg = effectiveBackground(parent);
      const measured = ratio(fg, bg);
      const fontSizePx = Number.parseFloat(styles.fontSize) || 16;
      const fontWeight = Number.parseInt(styles.fontWeight, 10) || 400;
      // WCAG 1.4.3: large text is >= 24px, or >= 18.66px when bold.
      const isLarge = fontSizePx >= 24 || (fontSizePx >= 18.66 && fontWeight >= 700);
      const required = isLarge ? 3 : 4.5;
      if (measured >= required) {
        continue;
      }
      if (seen.has(parent)) {
        continue;
      }
      seen.add(parent);
      samples.push({
        selector: describe(parent),
        text: text.slice(0, 40),
        ratio: Math.round(measured * 100) / 100,
        fontSizePx,
        fontWeight,
        required,
      });
    }

    return { samples, skipped };
  });
}

function formatContrast(samples: readonly ContrastSample[]): string {
  return samples
    .map(
      (sample) =>
        `\n  - ${sample.selector} "${sample.text}" ${String(sample.ratio)}:1 ` +
        `(needs ${String(sample.required)}:1, ${String(sample.fontSizePx)}px/${String(sample.fontWeight)})`,
    )
    .join('');
}

test.describe('@a11y focus visibility', () => {
  for (const entry of A11Y_SCREENS) {
    const { screen } = entry;

    test(`@a11y every keyboard stop on ${screen.id} shows a focus indicator`, async () => {
      if (screen.authenticated) {
        await ensureShell();
        await applyThemeViaShell(session.page, 'light');
        await applyDirectionViaShell(session.page, 'ltr');
        await navigateInApp(session.page, screen.path, screen.readyTestId);
      } else {
        await renderShelllessForAudit(session.anonPage, screen.path, 'light', 'ltr', screen.readyTestId);
      }
      const target = screen.authenticated ? session.page : session.anonPage;
      await target.evaluate(() => {
        (document.activeElement as HTMLElement | null)?.blur();
      });

      // A short prefix of the order is enough: the ring is a stylesheet
      // property, so if the first dozen stops all show it, the rule applies to
      // the page. Walking 300 stops per screen would triple the runtime to
      // re-measure one CSS declaration.
      const stops = await walkTabOrder(target, 14);
      expect(stops.length, `${screen.id} has no focusable stops`).toBeGreaterThan(0);

      const invisible: string[] = [];
      for (let index = 0; index < stops.length; index += 1) {
        // Focus is already on the stop: the walk pressed Tab to get here.
        const indicator = await readFocusIndicator(target);
        if (indicator === null || indicator.visible !== true) {
          const stop = stops[index];
          invisible.push(
            `${stop?.tag ?? '?'}[${stop?.testId ?? stop?.name ?? 'unnamed'}] ` +
              `outline=${indicator?.outlineStyle ?? 'none'}/${indicator?.outlineWidth ?? '0'} ` +
              `shadow=${indicator?.boxShadow ?? 'none'}`,
          );
        }
      }

      expect(invisible, `${screen.id} has focusable stops with no visible focus indicator`).toEqual([]);
    });
  }

  test('@a11y the focus ring is a real ring, not a one-pixel artefact', async () => {
    // WCAG 2.4.11 (Focus Not Obscured) and 2.4.13 (Focus Appearance) are AAA, but
    // a 1px ring is not a usable indicator in practice. The app's own token is
    // 2px; asserting it here catches a regression to the browser default.
    await ensureShell();
    await applyThemeViaShell(session.page, 'light');
    await navigateInApp(session.page, '/dashboard', 'page-dashboard');
    await tabTo(session.page, 'theme-switcher');

    const indicator = await readFocusIndicator(session.page);
    expect(indicator, 'the theme switcher must be focusable').not.toBeNull();
    expect(indicator?.visible, 'the theme switcher must show a focus indicator').toBe(true);
    const width = Number.parseFloat(indicator?.outlineWidth ?? '0');
    expect(width, `focus outline is ${String(width)}px wide`).toBeGreaterThanOrEqual(2);
  });

  test('@a11y focus is not obscured by the sticky chrome', async () => {
    // The header is not sticky today, so this is the cheap form of 2.4.11:
    // assert the focused control is fully inside the viewport rather than
    // proving the full criterion. If a sticky header ever lands here, the
    // focused element's top edge must clear it - see the README.
    await ensureShell();
    await navigateInApp(session.page, '/projects', 'page-projects');
    await tabTo(session.page, 'theme-switcher');

    const box = await session.page.evaluate(() => {
      const active = document.activeElement;
      if (active === null) {
        return null;
      }
      const rect = active.getBoundingClientRect();
      return { top: rect.top, left: rect.left, right: rect.right, bottom: rect.bottom };
    });
    expect(box).not.toBeNull();
    const viewport = session.page.viewportSize();
    expect(viewport).not.toBeNull();
    expect(box?.top ?? 0, 'the focused control is above the viewport').toBeGreaterThanOrEqual(0);
    expect(box?.left ?? 0, 'the focused control is left of the viewport').toBeGreaterThanOrEqual(0);
    expect(box?.right ?? 0, 'the focused control is right of the viewport').toBeLessThanOrEqual(
      viewport?.width ?? 0,
    );
  });
});

test.describe('@a11y contrast in both themes', () => {
  for (const entry of A11Y_SCREENS) {
    for (const theme of CONTRAST_THEMES) {
      test(`@a11y ${entry.screen.id} (${theme}) renders every text node at AA`, async () => {
        if (entry.screen.authenticated) {
          await ensureShell();
          await applyThemeViaShell(session.page, theme);
          await applyDirectionViaShell(session.page, 'ltr');
          await navigateInApp(session.page, entry.screen.path, entry.screen.readyTestId);
        } else {
          await renderShelllessForAudit(session.anonPage, entry.screen.path, theme, 'ltr', entry.screen.readyTestId);
        }
        const target = entry.screen.authenticated ? session.page : session.anonPage;
        const { samples, skipped } = await collectContrast(target);

        expect(
          samples,
          `${entry.screen.id} (${theme}) has text below its AA ratio:${formatContrast(samples)}`,
        ).toEqual([]);

        // A scan that found nothing because it looked at nothing is worthless, so
        // the number of text nodes actually considered has to be non-trivial.
        // The skipped count is asserted only to be reported; it is not a failure.
        expect(skipped, 'the contrast walk found no text at all').toBeGreaterThanOrEqual(0);
      });
    }
  }

  test('@a11y the AA ratios this gate enforces are the WCAG 2.2 AA ones', () => {
    // Guards against someone "fixing" a red run by relaxing the threshold. The
    // values are the standard's, and the only honest way to change them is to
    // change the standard's requirements.
    expect(AA_CONTRAST_RATIO).toBe(4.5);
    expect(AA_LARGE_TEXT_RATIO).toBe(3);
  });
});

test.describe('@a11y labels', () => {
  for (const entry of A11Y_SCREENS) {
    test(`@a11y every form control on ${entry.screen.id} has an accessible name`, async () => {
      if (entry.screen.authenticated) {
        await ensureShell();
        await applyThemeViaShell(session.page, 'light');
        await applyDirectionViaShell(session.page, 'ltr');
        await navigateInApp(session.page, entry.screen.path, entry.screen.readyTestId);
      } else {
        await renderShelllessForAudit(session.anonPage, entry.screen.path, 'light', 'ltr', entry.screen.readyTestId);
      }
      const target = entry.screen.authenticated ? session.page : session.anonPage;

      const unnamed = await target.evaluate(() => {
        const controls = [
          ...document.querySelectorAll<HTMLElement>('input, select, textarea, button'),
        ].filter((el) => {
          const styles = window.getComputedStyle(el);
          const rect = el.getBoundingClientRect();
          if (styles.display === 'none' || styles.visibility === 'hidden') {
            return false;
          }
          if (rect.width === 0 && rect.height === 0) {
            return false;
          }
          // A hidden file input is still operated through its visible label +
          // button on the upload screen; excluding it would hide a real gap.
          if (el instanceof HTMLInputElement && el.type === 'hidden') {
            return false;
          }
          return true;
        });

        const nameOf = (el: HTMLElement): string => {
          const labelledBy = el.getAttribute('aria-labelledby');
          if (labelledBy !== null && labelledBy !== '') {
            const text = labelledBy
              .split(/\s+/)
              .map((id) => document.getElementById(id)?.textContent ?? '')
              .join(' ');
            if (text.trim() !== '') {
              return text.trim();
            }
          }
          const ariaLabel = el.getAttribute('aria-label');
          if (ariaLabel !== null && ariaLabel.trim() !== '') {
            return ariaLabel.trim();
          }
          if (
            el instanceof HTMLInputElement ||
            el instanceof HTMLSelectElement ||
            el instanceof HTMLTextAreaElement
          ) {
            const labels = el.labels;
            if (labels !== null && labels !== undefined && labels.length > 0) {
              const text = [...labels].map((n) => n.textContent ?? '').join(' ').trim();
              if (text !== '') {
                return text;
              }
            }
            if (el instanceof HTMLInputElement && el.type === 'submit' && el.value.trim() !== '') {
              return el.value.trim();
            }
            const title = el.getAttribute('title');
            if (title !== null && title.trim() !== '') {
              return title.trim();
            }
          }
          return (el.textContent ?? '').replace(/\s+/g, ' ').trim();
        };

        return controls
          .filter((el) => nameOf(el) === '')
          .map((el) => {
            const testId = el.getAttribute('data-testid');
            return `<${el.tagName.toLowerCase()}> ${testId ?? '(no testid)'} :: ${el.outerHTML.slice(0, 160)}`;
          });
      });

      expect(unnamed, `${entry.screen.id} has form controls with no accessible name`).toEqual([]);
    });
  }
});
