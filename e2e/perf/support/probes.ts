// Task 041D: in-page measurement probes.
//
// Every timing number this suite reports is taken inside the page, against
// `performance.now()`. That is not a style choice:
//
//   * `performance.now()` in the page is relative to the navigation start, so a
//     cold-load measurement already includes document, chunk, parse, render and
//     paint. A Node-side `Date.now()` around `page.goto` adds the driver's own
//     scheduling, the websocket hop and the round trip that decides when the
//     assertion polls - hundreds of milliseconds of noise on a 300ms budget,
//     and noise that scales with how busy the machine is, which is exactly when
//     the number matters.
//   * the events being waited for (an input dispatched, a `seeked`, a filtered
//     list) are page events. Polling for them from Node turns the measurement
//     into "how fast did Playwright asked".
//
// Every probe returns both `at` (absolute `performance.now()` when it finished)
// and `ms` (elapsed from its own start), so a budget composed of two probes -
// navigate, then wait for the rows - subtracts two `at` values instead of
// summing two round trips through the driver.
//
// Each probe drives a real interaction through the application's own event path
// - a native `click()`, a native `input` event dispatched through the prototype
// value setter so React's value tracker sees a change - rather than calling
// into a store. A probe that reached into the store would measure React's
// render cost and skip the app's own event handling, which is half of what the
// budgets are about.

import type { BrowserContext, Page } from '@playwright/test';

/** What one probe observed. */
export interface ProbeResult {
  /** `performance.now()` when the probe finished. */
  readonly at: number;
  /** Elapsed milliseconds from the probe's own start. */
  readonly ms: number;
  /** Page evidence that the interaction actually did something. */
  readonly evidence: string;
}

export interface OpenTarget {
  readonly path: string;
  readonly testId: string;
  /** When set, the probe also waits for this many `selector` matches. */
  readonly selector?: string;
  readonly count?: number;
}

const DEFAULT_PROBE_TIMEOUT_MS = 30_000;

/**
 * The probe implementation, as one self-contained function.
 *
 * It has to be installable two ways: with `page.evaluate` for a page that is
 * already loaded, and with `addInitScript` for a page about to be loaded - a
 * cold load replaces the document, so a probe installed by `evaluate` would not
 * exist when the budget is being measured.
 */
function installProbeImplementation(timeout: number): void {
  interface Probe {
    idleNow: () => Promise<ProbeResult>;
    measureOpen: (path: string, testId: string, selector: string, count: number) => Promise<ProbeResult>;
    click: (testId: string) => Promise<ProbeResult>;
    clickThenTextChange: (clickTestId: string, watchTestId: string) => Promise<ProbeResult>;
    setRange: (testId: string, value: number) => Promise<ProbeResult>;
    setText: (testId: string, value: string) => Promise<ProbeResult>;
    waitForCount: (selector: string, count: number) => Promise<ProbeResult>;
    waitForAttribute: (testId: string, name: string, value: string) => Promise<ProbeResult>;
    waitForText: (testId: string, prefix: string) => Promise<ProbeResult>;
    measureSeek: (
      rangeTestId: string,
      value: number,
      videoTestId: string,
      positionTestId: string,
    ) => Promise<ProbeResult>;
    attribute: (testId: string, name: string) => Promise<string | null>;
    text: (testId: string) => Promise<string>;
    count: (selector: string) => Promise<number>;
    allAttributes: (selector: string, name: string) => Promise<Record<string, number>>;
  }
  interface ProbeWindow {
    __perf?: Probe;
  }

  const win = window as unknown as ProbeWindow;
  const find = (testId: string): HTMLElement => {
    const element = document.querySelector<HTMLElement>(`[data-testid="${testId}"]`);
    if (element === null) {
      throw new Error(`no element with data-testid="${testId}"`);
    }
    return element;
  };

  /** The instant after the frame that follows a state change has been painted. */
  const nextPaint = (): Promise<number> =>
    new Promise<number>((resolve) => {
      requestAnimationFrame(() => {
        requestAnimationFrame(() => {
          resolve(performance.now());
        });
      });
    });

  const until = async (predicate: () => boolean, what: string): Promise<void> => {
    const deadline = performance.now() + timeout;
    for (;;) {
      if (predicate()) {
        return;
      }
      if (performance.now() > deadline) {
        throw new Error(`timed out after ${String(timeout)}ms waiting for ${what}`);
      }
      await new Promise<void>((resolve) => {
        requestAnimationFrame(() => {
          resolve();
        });
      });
    }
  };

  /**
   * Waits for two consecutive idle periods.
   *
   * One is not "the main thread is free" - the browser grants it between two
   * halves of the same work - and the timeout bounds it, so a screen that never
   * goes idle fails as a measurement instead of hanging.
   */
  const untilIdle = async (): Promise<void> => {
    await new Promise<void>((resolve) => {
      const requestIdleCallback = (
        window as unknown as {
          requestIdleCallback?: (cb: () => void, opts?: { timeout: number }) => number;
        }
      ).requestIdleCallback;
      if (requestIdleCallback === undefined) {
        window.setTimeout(resolve, 0);
        return;
      }
      let calls = 0;
      const check = (): void => {
        calls += 1;
        if (calls >= 2) {
          resolve();
          return;
        }
        requestIdleCallback(check, { timeout });
      };
      requestIdleCallback(check, { timeout });
    });
  };

  /**
   * Writes through the *prototype* value setter.
   *
   * React installs a value tracker on the input instance, so assigning
   * `element.value` updates the tracker too and `onChange` never fires. This is
   * what a real key press does, and it is the only way to drive a controlled
   * input from outside React.
   */
  const setControlledValue = (element: HTMLInputElement, value: string): void => {
    const descriptor = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value');
    if (descriptor === undefined || descriptor.set === undefined) {
      throw new Error('HTMLInputElement.prototype.value setter is unavailable');
    }
    descriptor.set.call(element, value);
  };

  const visible = (element: Element | null): boolean => {
    if (element === null) {
      return false;
    }
    const rect = element.getBoundingClientRect();
    return rect.width > 0 && rect.height > 0;
  };

  const result = (start: number, evidence: string): ProbeResult => {
    const at = performance.now();
    return { at, ms: at - start, evidence };
  };

  win.__perf = {
    idleNow: async () => {
      const start = performance.now();
      const fonts = (document as Document & { fonts?: FontFaceSet }).fonts;
      if (fonts !== undefined) {
        await fonts.ready;
      }
      await untilIdle();
      return result(start, 'fonts ready and main thread idle');
    },

    /**
     * "The screen is open": in-app navigation dispatched, the route root
     * painted, the expected element count present, main thread idle.
     *
     * `pushState` + `popstate` rather than `page.goto` because a document load
     * is a logout (tokens are memory-only) - the same reason 041B and 041C
     * navigate this way.
     */
    measureOpen: async (path, testId, selector, count) => {
      const start = performance.now();
      window.history.pushState({}, '', path);
      window.dispatchEvent(new PopStateEvent('popstate'));
      await until(() => visible(document.querySelector(`[data-testid="${testId}"]`)), `${testId} to be visible at ${path}`);
      if (selector !== '' && count > 0) {
        await until(
          () => document.querySelectorAll(selector).length >= count,
          `${String(count)} x ${selector} at ${path} (now ${String(document.querySelectorAll(selector).length)})`,
        );
      }
      await untilIdle();
      return result(start, `${path} -> ${testId}${selector === '' ? '' : ` + ${String(count)} x ${selector}`}`);
    },

    click: async (testId) => {
      const start = performance.now();
      const element = find(testId) as HTMLElement & { click: () => void };
      element.click();
      await nextPaint();
      return result(start, `clicked ${testId}`);
    },

    /**
     * Click, then wait for another element's *text* to change, then paint.
     *
     * This is how a debounced interaction has to be timed. A plain `click` probe
     * stops at the next paint, which for a debounced control is the paint
     * *before* the debounce fires - it reported 21ms for a control whose
     * `TIMELINE_DEBOUNCE_MS` is 120ms, which is a number about the wrong thing.
     * The debounce is part of what a user waits for, so the measurement has to
     * span it.
     */
    clickThenTextChange: async (clickTestId, watchTestId) => {
      const watcher = find(watchTestId);
      const before = (watcher.textContent ?? '').replace(/\s+/g, ' ').trim();
      const start = performance.now();
      (find(clickTestId) as HTMLElement & { click: () => void }).click();
      await until(
        () => (watcher.textContent ?? '').replace(/\s+/g, ' ').trim() !== before,
        `${watchTestId} to change from "${before}" (now "${(watcher.textContent ?? '').replace(/\s+/g, ' ').trim().slice(0, 40)}")`,
      );
      await nextPaint();
      return result(
        start,
        `clicked ${clickTestId}; ${watchTestId} "${before}" -> "${(watcher.textContent ?? '').replace(/\s+/g, ' ').trim()}"`,
      );
    },

    setRange: async (testId, value) => {
      const element = find(testId) as HTMLInputElement;
      const start = performance.now();
      setControlledValue(element, String(value));
      element.dispatchEvent(new Event('input', { bubbles: true }));
      await nextPaint();
      return result(start, `${testId}=${String(value)}`);
    },

    setText: async (testId, value) => {
      const element = find(testId) as HTMLInputElement;
      const start = performance.now();
      setControlledValue(element, value);
      element.dispatchEvent(new Event('input', { bubbles: true }));
      return result(start, `${testId}="${value}"`);
    },

    waitForCount: async (selector, count) => {
      const start = performance.now();
      await until(
        () => document.querySelectorAll(selector).length >= count,
        `${String(count)} x ${selector} (now ${String(document.querySelectorAll(selector).length)})`,
      );
      await nextPaint();
      return result(start, `${String(count)} x ${selector}`);
    },

    waitForAttribute: async (testId, name, value) => {
      const element = find(testId);
      const start = performance.now();
      await until(
        () => element.getAttribute(name) === value,
        `${testId}[${name}]="${value}" (now "${element.getAttribute(name) ?? 'null'}")`,
      );
      await nextPaint();
      return result(start, `${testId}[${name}]="${value}"`);
    },

    waitForText: async (testId, prefix) => {
      const element = find(testId);
      const start = performance.now();
      await until(
        () => (element.textContent ?? '').replace(/\s+/g, ' ').trim().startsWith(prefix),
        `${testId} to start with "${prefix}" (now "${(element.textContent ?? '').replace(/\s+/g, ' ').trim().slice(0, 40)}")`,
      );
      await nextPaint();
      return result(start, `${testId}="${prefix}…"`);
    },

    /**
     * A seek, end to end: input dispatched, the media element reports `seeked`,
     * *and* the position the player renders matches the target.
     *
     * Both halves are required. A media element that answers `seeked` while the
     * UI stays put would pass a seeked-only assertion, and an assertion on the
     * rendered text alone would pass a UI that never actually moved the media.
     */
    measureSeek: async (rangeTestId, value, videoTestId, positionTestId) => {
      const range = find(rangeTestId) as HTMLInputElement;
      const video = find(videoTestId) as HTMLVideoElement;
      const position = find(positionTestId);
      const start = performance.now();
      const seeked = new Promise<void>((resolve) => {
        video.addEventListener('seeked', () => {
          resolve();
        }, { once: true });
      });
      setControlledValue(range, String(value));
      range.dispatchEvent(new Event('input', { bubbles: true }));
      await seeked;
      const actualSeconds = video.currentTime;
      await until(
        () => (position.textContent ?? '').replace(/\s+/g, ' ').trim().startsWith(String(value)),
        `${positionTestId} to read "${String(value)} ms" (now "${(position.textContent ?? '').trim()}")`,
      );
      await nextPaint();
      return result(
        start,
        `seek ${String(value)}ms -> element at ${String(actualSeconds)}s, rendered "${(position.textContent ?? '').trim()}"`,
      );
    },

    attribute: async (testId, name) => find(testId).getAttribute(name),
    text: async (testId) => (find(testId).textContent ?? '').replace(/\s+/g, ' ').trim(),
    count: async (selector) => document.querySelectorAll(selector).length,
    allAttributes: async (selector, name) => {
      const out: Record<string, number> = {};
      for (const element of document.querySelectorAll(selector)) {
        const raw = element.getAttribute(name);
        if (raw !== null && Number.isFinite(Number(raw))) {
          out[element.getAttribute('data-testid') ?? element.tagName.toLowerCase()] = Number(raw);
        }
      }
      return out;
    },
  };
}

/** Installs the probes into the page's current document. */
export async function installProbes(page: Page, timeoutMs: number = DEFAULT_PROBE_TIMEOUT_MS): Promise<void> {
  await page.evaluate(installProbeImplementation, timeoutMs);
}

/**
 * Installs the probes into every document a context loads, including the first.
 *
 * Required for a cold-load budget: `page.goto` replaces the document, so a
 * probe installed by `evaluate` is gone before the measurement starts.
 */
export async function installProbesForContext(
  context: BrowserContext,
  timeoutMs: number = DEFAULT_PROBE_TIMEOUT_MS,
): Promise<void> {
  await context.addInitScript(installProbeImplementation, timeoutMs);
}

async function callProbe<T>(page: Page, method: string, args: readonly unknown[]): Promise<T> {
  return page.evaluate(
    async ([name, values]) => {
      const perf = (
        window as unknown as {
          __perf?: Record<string, (...rest: unknown[]) => Promise<unknown>>;
        }
      ).__perf;
      if (perf === undefined) {
        throw new Error('installProbes() was not called on this page');
      }
      const fn = perf[String(name)];
      if (fn === undefined) {
        throw new Error(`no probe named '${String(name)}'`);
      }
      return (await fn(...(values as unknown[]))) as never;
    },
    [method, args] as const,
  ) as Promise<T>;
}

/**
 * The app-interactive instant, in milliseconds since navigation start.
 *
 * `performance.now()` is measured from `performance.timeOrigin`, which is the
 * navigation start of the current document, so this single number covers
 * document, chunk download, parse, render, paint, font readiness and the main
 * thread going idle. The budget is that number.
 *
 * It is the probe's `at` and deliberately NOT its `ms`: the probe runs *after*
 * the page has loaded, so its own elapsed time is the fonts-and-idle wait - a
 * handful of milliseconds that says nothing about the load. Reading `ms` here
 * produced a 62ms "app-interactive" against a real ~930ms load, which is the
 * kind of green that is worse than red.
 */
export async function measureAppInteractive(page: Page): Promise<number> {
  return page.evaluate(async () => {
    const perf = (window as unknown as { __perf?: { idleNow: () => Promise<ProbeResult> } }).__perf;
    if (perf === undefined) {
      throw new Error('installProbesForContext() was not called on this context');
    }
    return (await perf.idleNow()).at;
  });
}

export function probeMeasureOpen(page: Page, target: OpenTarget): Promise<ProbeResult> {
  return callProbe<ProbeResult>(page, 'measureOpen', [
    target.path,
    target.testId,
    target.selector ?? '',
    target.count ?? 0,
  ]);
}

export function probeClick(page: Page, testId: string): Promise<ProbeResult> {
  return callProbe<ProbeResult>(page, 'click', [testId]);
}

export function probeClickThenTextChange(
  page: Page,
  clickTestId: string,
  watchTestId: string,
): Promise<ProbeResult> {
  return callProbe<ProbeResult>(page, 'clickThenTextChange', [clickTestId, watchTestId]);
}

export function probeSetRange(page: Page, testId: string, value: number): Promise<ProbeResult> {
  return callProbe<ProbeResult>(page, 'setRange', [testId, value]);
}

export function probeSetTextNow(page: Page, testId: string, value: string): Promise<ProbeResult> {
  return callProbe<ProbeResult>(page, 'setText', [testId, value]);
}

export function probeWaitForCount(page: Page, selector: string, count: number): Promise<ProbeResult> {
  return callProbe<ProbeResult>(page, 'waitForCount', [selector, count]);
}

export function probeWaitForAttribute(
  page: Page,
  testId: string,
  name: string,
  value: string,
): Promise<ProbeResult> {
  return callProbe<ProbeResult>(page, 'waitForAttribute', [testId, name, value]);
}

export function probeWaitForText(page: Page, testId: string, prefix: string): Promise<ProbeResult> {
  return callProbe<ProbeResult>(page, 'waitForText', [testId, prefix]);
}

export function probeMeasureSeek(
  page: Page,
  rangeTestId: string,
  value: number,
  videoTestId: string,
  positionTestId: string,
): Promise<ProbeResult> {
  return callProbe<ProbeResult>(page, 'measureSeek', [rangeTestId, value, videoTestId, positionTestId]);
}

export function probeAttribute(page: Page, testId: string, name: string): Promise<string | null> {
  return callProbe<string | null>(page, 'attribute', [testId, name]);
}

export function probeText(page: Page, testId: string): Promise<string> {
  return callProbe<string>(page, 'text', [testId]);
}

export function probeCount(page: Page, selector: string): Promise<number> {
  return callProbe<number>(page, 'count', [selector]);
}

export function probeAllAttributes(
  page: Page,
  selector: string,
  name: string,
): Promise<Record<string, number>> {
  return callProbe<Record<string, number>>(page, 'allAttributes', [selector, name]);
}
