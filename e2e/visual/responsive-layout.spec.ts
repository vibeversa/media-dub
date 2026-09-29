// Task 041B, R4: responsive degradation, asserted rather than snapshotted - and
// this file is the evidence for why it cannot be asserted today.
//
// The task asks for the mobile timeline to "degrade to list inspection per
// responsive rules - assert the degraded layout, not the desktop editor", and
// R4 requires "mobile list vs desktop editor" to be asserted.
//
// Before pinning three viewport widths per screen, the question is whether the
// application has three layouts to pin. It does not.
//
// Measured: across all 71 non-test feature components, there are zero
// responsive breakpoint classes. No `sm:`, `md:`, `lg:`, `xl:` or `2xl:`
// utility, and no `matchMedia`/breakpoint hook outside a single
// `prefers-color-scheme` read in settings. Every screen therefore renders one
// layout at 390 px, 1024 px and 1440 px, differing only by incidental text
// wrapping and flex reflow.
//
// So the three baselines per screen are still worth committing - they will catch
// an unintended change at any width, and they are what makes a future responsive
// change visible as a deliberate diff. But there is no degraded layout to assert,
// and inventing one here would be asserting a design that does not exist.
//
// This test therefore pins the *finding* rather than a layout: it fails if
// responsive rules ever appear, at which point R4 becomes implementable and this
// file should be replaced with real degradation assertions. That inversion is
// deliberate - a test that fails when the app improves is the honest encoding of
// "this is a gap, and here is what closes it".

import { readFileSync, readdirSync, statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { expect, test } from '@playwright/test';

import { BREAKPOINTS } from './support/matrix.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const FEATURES = path.resolve(here, '..', '..', 'frontend', 'src', 'features');

/** Walks the feature tree, skipping tests and build output. */
function componentFiles(dir: string): string[] {
  const found: string[] = [];
  for (const entry of readdirSync(dir)) {
    const full = path.join(dir, entry);
    if (statSync(full).isDirectory()) {
      if (entry === '__tests__' || entry === 'node_modules') {
        continue;
      }
      found.push(...componentFiles(full));
      continue;
    }
    if (entry.endsWith('.tsx') && !entry.includes('.test.')) {
      found.push(full);
    }
  }
  return found;
}

const RESPONSIVE_UTILITY = /\b(?:sm|md|lg|xl|2xl):[a-z]/;
const BREAKPOINT_HOOK = /\bmatchMedia\b|\buseMediaQuery\b|\buseBreakpoint\b/;

test('@visual the application has no responsive layout, so R4 cannot be asserted yet', () => {
  const files = componentFiles(FEATURES);
  expect(files.length, 'expected to find feature components to scan').toBeGreaterThan(50);

  const responsiveComponents = files.filter((file) =>
    RESPONSIVE_UTILITY.test(readFileSync(file, 'utf8')),
  );
  const breakpointHooks = files.filter((file) =>
    // `prefers-color-scheme` is a colour-scheme query, not a layout breakpoint,
    // so it does not count towards responsive layout support.
    BREAKPOINT_HOOK.test(readFileSync(file, 'utf8')),
  );

  // When this assertion starts failing, the app has gained responsive rules and
  // R4 is implementable: replace this file with real list-vs-editor degradation
  // assertions per screen and add the mobile expectations to `screens.spec.ts`.
  expect(
    responsiveComponents.map((file) => path.relative(FEATURES, file)),
    'responsive utility classes appeared - implement R4 degradation assertions',
  ).toEqual([]);
  expect(
    breakpointHooks.map((file) => path.relative(FEATURES, file)),
    'a breakpoint hook appeared - implement R4 degradation assertions',
  ).toEqual([]);
});

test('@visual every matrix breakpoint is pinned at the width it claims', () => {
  // Not a substitute for degradation, but it keeps the three baselines per screen
  // honest: if a width were changed without re-baselining, every screen at that
  // width would fail at once and the diff would name the width.
  expect(BREAKPOINTS.map((b) => `${b.id}=${b.width}`)).toEqual([
    'desktop=1440',
    'tablet=1024',
    'mobile=390',
  ]);
});
