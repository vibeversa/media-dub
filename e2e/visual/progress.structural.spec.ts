// Task 041B, R3: dynamic progress is asserted structurally, never pixel-compared.
//
// A progress meter, spinner or waveform has no single correct frame, so
// comparing pixels across frames produces diffs that are not regressions. The
// contract that *is* checkable is that the surface exists, is exposed to
// assistive technology, and reports its state in text - and that is what these
// tests assert.
//
// `screens.spec.ts` masks the same surfaces in its images, so a meter can advance
// between runs without failing the matrix. Masking and asserting are deliberate
// halves of one decision: mask it because it moves, assert it because it is
// still a real thing the user depends on.

import { expect, test } from '@playwright/test';

import { PROJECT_ID } from './support/matrix.js';
import { navigateInApp, openVisualSession, type VisualSession } from './support/session.js';

let session: VisualSession;

// Not `mode: 'serial'`: see the note in screens.spec.ts. One worker is enough to
// share the session, and these assertions should all report even if one fails.

test.beforeAll(async ({ browser }) => {
  session = await openVisualSession(browser);
});

test.afterAll(async () => {
  await session?.close();
});

test('@visual progress surfaces are labelled, not just painted', async () => {
  const { page } = session;

  // The upload screen is where the rig renders an actual <progress> element.
  // In-app navigation, because a document load would log the session out, and the
  // screen's own root as the readiness signal - `app-shell` is already mounted
  // across route changes and would return before the panel had fetched.
  await navigateInApp(page, `/projects/${PROJECT_ID}/media`, 'page-project-media');

  const meters = page.getByRole('progressbar');
  const count = await meters.count();

  if (count === 0) {
    // No active upload is in flight on a freshly seeded project, which is the
    // honest state. Assert the *absent* case is well-formed rather than skipping:
    // an empty state must not be a blank region with no explanation.
    // The dropzone is the screen's real resting state with nothing in flight.
    await expect(page.getByTestId('page-project-media')).toBeVisible();
    await expect(page.getByTestId('upload-dropzone')).toBeVisible();
    await expect(page.getByTestId('upload-overall')).toHaveCount(0);
    return;
  }

  // When a meter is present it must be a real progressbar with a name and a
  // numeric value - that is what a screen reader and a visual diff both need.
  for (let index = 0; index < count; index += 1) {
    const meter = meters.nth(index);
    await expect(meter).toBeVisible();

    const accessibleName = await meter.getAttribute('aria-label');
    const labelledBy = await meter.getAttribute('aria-labelledby');
    expect(
      (accessibleName !== null && accessibleName !== '') || labelledBy !== null,
      `progressbar ${index} must have an accessible name`,
    ).toBe(true);

    const value = await meter.getAttribute('aria-valuenow');
    const max = await meter.getAttribute('aria-valuemax');
    expect(value, `progressbar ${index} must report aria-valuenow`).not.toBeNull();
    expect(Number(max ?? 100)).toBeGreaterThan(0);
    expect(Number(value)).toBeGreaterThanOrEqual(0);
    expect(Number(value)).toBeLessThanOrEqual(Number(max ?? 100));
  }
});

test('@visual the workspace progress readout is text, not only a bar', async () => {
  const { page } = session;
  await navigateInApp(page, `/projects/${PROJECT_ID}`, 'page-project-details');

  // Whatever the state, the screen must state it in words somewhere: a
  // bar with no adjacent text is exactly the "painted but not communicated"
  // failure a pixel comparison cannot catch.
  const body = await page.getByTestId('app-shell').innerText();
  expect(body.trim().length, 'the workspace must render readable state text').toBeGreaterThan(0);

  // Progress/percentage, when present, must be expressed numerically and legibly
  // rather than as a bare glyph.
  const percent = /\d+\s*%/.test(body);
  const wordy = /\b(pending|running|ready|complete|failed|queued|processing)\b/i.test(body);
  expect(percent || wordy, 'workspace must express progress as text').toBe(true);
});
