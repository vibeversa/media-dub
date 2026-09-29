// Task 041C: axe-core runner.
//
// Three decisions shape this file.
//
// 1. **axe runs in the page, not in a wrapper package.** `axe-core` is injected
//    as source and evaluated against the live DOM, so the scan sees the same
//    document a user does. `@axe-core/playwright` would do the same thing behind
//    another abstraction; injecting directly keeps the dependency to the engine
//    and makes the tag set an explicit, reviewable argument.
//
// 2. **The tag set is WCAG 2.2 AA and nothing looser.** `wcag2a`, `wcag2aa`,
//    `wcag21a`, `wcag21aa`, `wcag22aa` and `best-practice` are all in. The
//    `best-practice` tag is included deliberately: it is where landmark and
//    heading-order problems land, and neither is a "real" rule that can be
//    waived away for a product reason.
//
// 3. **A violation is reported with enough context to fix it.** Rule id, impact,
//    help text, the WCAG tags it maps to, the target selector, and the failure
//    summary. A bare "3 violations" is not actionable and would get waived by
//    the next person who could not tell what it meant.

import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';

import type { Page } from '@playwright/test';

const require = createRequire(import.meta.url);

/** The axe engine source, read once and reused for every scan. */
const AXE_SOURCE: string = readFileSync(require.resolve('axe-core/axe.min.js'), 'utf8');

/**
 * Tags that make up "WCAG 2.2 AA".
 *
 * `best-practice` is included even though it is not part of WCAG, because the
 * task asks for a full audit and the landmark/heading checks are the ones that
 * catch a screen with no `main` or a heading hierarchy that starts at `h3`.
 * Every rule reachable through these tags is either a WCAG success criterion or
 * a de-facto standard; there is no rule in the set that can be waived for
 * product reasons alone.
 */
export const WCAG_22_AA_TAGS: readonly string[] = [
  'wcag2a',
  'wcag2aa',
  'wcag21a',
  'wcag21aa',
  'wcag22aa',
  'best-practice',
];

export interface AxeNodeFailure {
  readonly target: readonly string[];
  readonly html?: string;
  readonly failureSummary?: string;
}

export interface AxeViolation {
  readonly id: string;
  readonly impact: string | null;
  readonly help: string;
  readonly description: string;
  readonly helpUrl: string;
  readonly tags: readonly string[];
  readonly nodes: readonly AxeNodeFailure[];
}

export interface AxeScanResult {
  readonly violations: readonly AxeViolation[];
  readonly passes: number;
  readonly incomplete: ReadonlyArray<{
    readonly id: string;
    readonly help: string;
    readonly nodes: readonly AxeNodeFailure[];
  }>;
  /** Rule ids skipped for this scan, with the waiver that authorised it. */
  readonly waived: ReadonlyArray<{ readonly ruleId: string; readonly waiver: string }>;
}

/** Injects the engine. Idempotent: a second call on the same page is a no-op. */
export async function installAxe(page: Page): Promise<void> {
  const already = await page.evaluate(() => (window as { axe?: unknown }).axe !== undefined);
  if (already) {
    return;
  }
  await page.evaluate((source) => {
    // The engine is evaluated as a classic script: it is a self-contained UMD
    // bundle that publishes `window.axe`, and `eval` inside `page.evaluate` runs
    // in the page's own realm with no bundler in the way.
    // eslint-disable-next-line no-eval
    (0, eval)(source);
  }, AXE_SOURCE);
}

interface RawAxeNode {
  readonly target: readonly string[];
  readonly html?: string;
  readonly failureSummary?: string;
}

interface RawAxeResult {
  readonly violations: ReadonlyArray<{
    id: string;
    impact: string | null;
    help: string;
    description: string;
    helpUrl: string;
    tags: readonly string[];
    nodes: readonly RawAxeNode[];
  }>;
  readonly passes: readonly unknown[];
  readonly incomplete: ReadonlyArray<{
    id: string;
    help: string;
    nodes: readonly RawAxeNode[];
  }>;
}

/**
 * Runs axe over the whole document.
 *
 * `include`/`exclude` exist for one reason: a modal rendered *alongside* its
 * page (the export dialog, the dirty-navigation dialogs) is `aria-modal`-less by
 * construction, because the primitive was written before this audit. Those are
 * audited by `dialogs.spec.ts` against the `dialog` role and the focus trap
 * instead, so the document scan must not double-count them as "background
 * content visible behind a modal" - a violation the page cannot fix without
 * unmounting the dialog. The waiver registry is the honest channel for such
 * cases; the exclusion list is deliberately empty and a scan asserts it stays
 * empty, so this escape hatch cannot grow unnoticed.
 */
export async function runAxe(
  page: Page,
  options?: { readonly include?: readonly string[]; readonly exclude?: readonly string[] },
): Promise<RawAxeResult> {
  await installAxe(page);
  // `axe.run` returns a promise when called with a context, an options object
  // and no callback, and the awaited value is the full result object. The
  // callback form would have to stash the result on `window` and read it back
  // in a second round trip, which is the same amount of code and one more place
  // for a stale value to hide.
  return page.evaluate(
    (config) =>
      (window as unknown as { axe: { run: (ctx: unknown, options: unknown) => Promise<unknown> } }).axe.run(
        document,
        config,
      ) as Promise<RawAxeResult>,
    {
      runOnly: { type: 'tag', values: [...WCAG_22_AA_TAGS] },
      resultTypes: ['violations', 'incomplete'],
      ...(options?.include !== undefined ? { include: options.include } : {}),
      ...(options?.exclude !== undefined ? { exclude: options.exclude } : {}),
    },
  );
}

/**
 * Applies the waiver registry to a raw scan.
 *
 * A waiver is a claim that a specific rule on a specific screen is a known
 * false positive or an accepted, dated exception. It is honoured only if it
 * carries an expiry and an issue link, and it is only honoured if the rule
 * actually fired - an unused waiver is dead weight that hides the day the
 * underlying problem got fixed, so it is reported separately and the test fails
 * on it.
 */
export function applyWaivers(
  result: RawAxeResult,
  screenId: string,
  waivers: readonly Waiver[],
): AxeScanResult {
  const relevant = waivers.filter((waiver) => waiver.screen === screenId);
  const byRule = new Map<string, Waiver>();
  for (const waiver of relevant) {
    byRule.set(waiver.ruleId, waiver);
  }

  const violations: AxeViolation[] = [];
  const waived: Array<{ ruleId: string; waiver: string }> = [];
  const usedRules = new Set<string>();

  for (const violation of result.violations) {
    const waiver = byRule.get(violation.id);
    if (waiver === undefined) {
      violations.push({
        id: violation.id,
        impact: violation.impact,
        help: violation.help,
        description: violation.description,
        helpUrl: violation.helpUrl,
        tags: violation.tags,
        nodes: violation.nodes,
      });
      continue;
    }
    usedRules.add(violation.id);
    waived.push({ ruleId: violation.id, waiver: waiver.issue });
  }

  // Waivers that did not match a violation are reported so the registry cannot
  // accumulate entries that mask nothing.
  const staleWaivers = [...byRule.values()].filter((waiver) => !usedRules.has(waiver.ruleId));

  return {
    violations,
    passes: result.passes.length,
    incomplete: result.incomplete,
    waived,
    ...(staleWaivers.length === 0
      ? {}
      : {
          incomplete: [
            ...result.incomplete,
            ...staleWaivers.map((waiver) => ({
              id: `stale-waiver:${waiver.ruleId}`,
              help: `Waiver ${waiver.issue} no longer matches any violation. Remove it.`,
              nodes: [] as readonly RawAxeNode[],
            })),
          ],
        }),
  };
}

/** A dated, linked exception for one axe rule on one screen. */
export interface Waiver {
  readonly ruleId: string;
  readonly screen: string;
  /** Why the finding is not a real defect. Recorded in `e2e/a11y/README.md`. */
  readonly justification: string;
  /** Issue link. A waiver without one is rejected - see `waivers.spec.ts`. */
  readonly issue: string;
  /** ISO date. A waiver without one is rejected. */
  readonly expiresOn: string;
  /** What was checked by hand, and how, to conclude it is a false positive. */
  readonly manualVerification: string;
}

/** Renders violations as a single assertion-friendly block. */
export function formatViolations(violations: readonly AxeViolation[]): string {
  if (violations.length === 0) {
    return 'no violations';
  }
  return violations
    .map((violation) => {
      const targets = violation.nodes
        .slice(0, 3)
        .map((node) => node.target.join(' '))
        .join(' | ');
      const wcag = violation.tags.filter((tag) => tag.startsWith('wcag')).join(', ');
      return (
        `\n  - [${violation.impact ?? 'unknown'}] ${violation.id}: ${violation.help}\n` +
        `      wcag: ${wcag}\n      url: ${violation.helpUrl}\n` +
        `      at: ${targets}\n` +
        (violation.nodes[0]?.failureSummary ?? '')
      );
    })
    .join('');
}

/** Renders axe's own `incomplete` results - things it could not decide. */
export function formatIncomplete(
  incomplete: ReadonlyArray<{ id: string; help: string; nodes: readonly AxeNodeFailure[] }>,
): string {
  if (incomplete.length === 0) {
    return 'none';
  }
  return incomplete
    .map((entry) => {
      const targets = entry.nodes
        .slice(0, 2)
        .map((node) => node.target.join(' '))
        .join(' | ');
      return `\n  - ${entry.id}: ${entry.help} (at ${targets})`;
    })
    .join('');
}
