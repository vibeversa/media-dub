# Visual regression (Task 041B)

12 screens x 3 breakpoints x 2 themes x 2 directions, pinned on the real stack
(frontend bundle + API + PostgreSQL + object storage + transport, mock AI only).

```bash
docker compose -f tests/cross-layer/docker-compose.cross.yml up -d
npm run test:visual          # verify against committed baselines
docker compose -f tests/cross-layer/docker-compose.cross.yml down
```

Read `tests/cross-layer/README.md` first - it is the rig runbook.

## The matrix

12 screens: login, dashboard, project list, project-create wizard, upload,
workspace, transcript, translation, voices, review, quality control, outputs and
exports. `support/matrix.ts` is the single source of truth; the axis-integrity
tests assert it is 12 x 3 x 2 x 2 with no cell missing or duplicated, that no
screen waits on `app-shell`, and that every screen names a readiness signal.

Two routes are worth knowing because they are not what the label suggests:

- **The workspace is `/projects/{id}`** (the Overview, `ProjectDetailsPage`).
  There is no `/projects/{id}/workspace` child route, so that path renders a bare
  tab bar - which is how the first baseline set came to pin twelve empty screens.
- **Review is the cross-project queue at `/review`.** The project tab route
  `/projects/{id}/review` is registered but paints no panel on a seeded project,
  only the tab bar. Pinning it would have committed twelve baselines of an empty
  region, which is worse than no gate because it looks green.

| Axis | Values | Driven by |
| --- | --- | --- |
| Breakpoint | desktop 1440, tablet 1024, mobile 390 | `page.setViewportSize` |
| Theme | light, dark | the shell's `theme-switcher` -> `documentElement[data-theme]` |
| Direction | LTR, RTL | the shell's `locale-switcher` -> `en`/`ar` -> `documentElement[dir]` |

Sizes are choices inside the task's bands (desktop >= 1280, tablet 768-1279,
mobile < 768); a test asserts each width stays inside its band.

**There is no `?dir=` parameter.** Task 045's override was never built - direction
is a function of the store locale, so RTL is reached by switching locale to `ar`,
which is how a user reaches it. The switcher buttons have no `data-testid` and are
addressed by their accessible name from `nav.json`; the direction test also asserts
`document.lang` matches the intended locale, so a future locale change cannot make
"rtl" quietly mean something else.

## Determinism (R2)

Four things had to be true before a single baseline was trustworthy. All four were
found by running the suite, not by reading the code, and each is a way a visual
gate can be green while meaning nothing.

- **One sign-in for the whole matrix.** Tokens are never persisted - `authStore`
  keeps them in memory and writes only a logout *timestamp* - so a document load
  logs the browser out, and `POST /auth/login` is rate limited to 5/min per IP. The
  suite therefore owns one context, one login and one *never-authenticated* second
  page for the login screen (`support/session.ts`).
- **No `page.goto` between screens.** A document load destroys the in-memory
  session, so navigation is client-side (`history.pushState` + `popstate`, which
  React Router resolves). Verified: after `page.goto('/projects')` the browser
  lands on `/login?next=/projects`.
- **Wait for the screen's own root, then for the content to stop changing.** The
  first version waited on `app-shell`, which is wrong twice over: the shell stays
  mounted across route changes, so the wait returned immediately, and the workspace
  fills in its cost, config and activity panels *after* its root appears. Captures
  were a coin flip. `waitForContentStable` now polls a fingerprint of visible
  testids and their text lengths until it is unchanged for 600 ms, and **fails
  loudly** if a screen never settles rather than capturing a churning page.
- **Fixture ids are derived, not random.** `seed/Program.cs` used
  `Guid.NewGuid()` for every fixture id, and the exports screen renders
  `exp_<32 hex>` as visible text - so those twelve baselines could never match.
  Ids now come from `DeterministicId(options, label)`, a SHA-256 of
  tenant+project+label. This is the fixture determinism Task 046 was meant to own;
  deriving ids from stable labels means the rig no longer waits on 046 to be
  reproducible.
- **Clock pinned** to `2026-01-15T12:00:00.000Z` via `page.clock.setFixedTime`,
  which is the same instant the seeder stamps rows with. Real timers keep running
  (`clock.install()` would freeze the app's own scheduling and leave a debounced
  fetch unfired, which looks like a rendering bug and is not one).
- **Fixed project id**: `prj_33333333333333333333333333333333`, from the seeder's
  fixed GUID.
- **Fonts awaited** (`document.fonts.ready`) before every screenshot, and
  `--disable-lcd-text` plus `scale: 'css'` pin rasterisation.
- **Animations disabled** on capture.

No live data and no real clock. Everything rendered comes from the seeder.

## Dynamic progress is never pixel-compared (R3)

Progress meters and spinners have no single correct frame, so `screens.spec.ts`
masks them (`mask:`) rather than diffing them. Masking alone would let a real
regression through, so `progress.structural.spec.ts` asserts the same surfaces
*structurally*: a `progressbar` role must exist when a meter is painted, must
carry an accessible name, and must report `aria-valuenow` within
`0..aria-valuemax`; and the workspace must express its state as text, not only as
a bar. Mask and assertion are two halves of one decision.

## Responsive degradation is a documented gap, not a snapshot (R4)

**The application has no responsive layout.** Measured across all 71 non-test
feature components: zero `sm:`/`md:`/`lg:`/`xl:`/`2xl:` utility classes, and no
`matchMedia`/breakpoint hook outside one `prefers-color-scheme` read in settings.

So there is no "mobile list vs desktop editor" switch to assert, and R4 cannot be
satisfied today. Rather than assert a design that does not exist,
`responsive-layout.spec.ts` **pins the finding**: it fails the moment responsive
rules appear, at which point the file should be replaced with real degradation
assertions and `screens.spec.ts` should gain mobile expectations.

The three widths per screen are still committed. They catch an unintended change
at any width, and they make a future responsive change show up as a deliberate,
reviewable diff rather than a surprise.

## Font-substitution policy

Baselines are **Chromium-only** on the machine that generated them, and
`snapshotPathTemplate` deliberately omits `{platform}` so a baseline cannot be
silently forked per OS.

Approved substitutions (a diff confined to these is not investigated):

| Symptom | Accepted cause |
| --- | --- |
| Glyph shape differs, metrics identical | A different font file with the same metrics |
| Text shifted by <= 1 px on one axis | Sub-pixel rounding at a fractional device scale |
| Whole-page height differs by <= 2 px | Scrollbar gutter appearing/disappearing |

Anything else - different metrics, reflowed lines, missing glyphs, shifted
blocks - is a real difference and must be investigated, never auto-blessed. There
is no retry or auto-update in CI: `playwright.config.ts` keeps `retries: 0`.

## Baselines and re-baselining (R5)

Baselines live flat in `e2e/visual/__screenshots__/`, one PNG per cell, named
`<screen>-<breakpoint>-<theme>-<direction>.png` - Playwright slugifies the test
title, so the whole matrix coordinate is in the filename and greppable without
opening anything. 144 files, no per-screen subdirectories, because the name already
carries the screen.

Re-baselining is a **review event**:

```bash
npm run test:visual:update
git diff --stat e2e/visual/__screenshots__   # read the diff, do not rubber-stamp it
git commit -m "chore(visual): re-baseline <reason> (reviewed by <name>)"
```

Rules:
- One reason per re-baseline commit, naming what changed and why.
- A PR that changes a screen's UI must include its re-baseline, or explicitly
  defer with an issue link. A new screen needs a `SCREENS` entry in
  `support/matrix.ts`, which the coverage test then enforces.
- Never regenerate the whole set to make a run green. That is the one failure mode
  this gate exists to prevent.

## N/A entries

A matrix entry that cannot apply is marked in `SCREENS[].notApplicable` **with a
reason** and skipped at runtime with that reason as the skip message - never
deleted, because a deleted cell is invisible. The axis-integrity test still counts
it, so an N/A cannot quietly reduce coverage.

## Scope

- Owns no feature specs and no journeys. `frontend/e2e/**` stays with 019-036;
  `e2e/journeys/` and `e2e/smoke/` are 041A's and do not exist yet.
- 041C (a11y) and 041D (perf) build on this harness but own their own specs.
- Screens reflect the rig's data, and the rig cannot run the pipeline (no
  `ffmpeg`/`ffprobe` in the workers, so a run sits `Pending` at `MediaValidation`).
  Project-scoped screens therefore pin seeded/empty states. That is a real baseline
  of what a user sees, but it is not a baseline of a completed pipeline - do not
  read it as one.

## Known gaps

- **No log scrubbing.** `.artifacts/` holds unscrubbed failure screenshots; they
  contain synthetic seeded data only. 046 owns the scrubber; 042B must apply it
  before any CI attach.
- **`ar` translations are partial** - `ar/nav.json` has only `dashboard` and
  `projects`, and the rest of the chrome falls back to English. RTL baselines
  therefore pin a **mirrored layout with mostly English text**. The mirroring is
  the point of the axis and is fully covered; translated copy is 045's gap.
- **No per-OS baselines** (Chromium only, see the font policy).
