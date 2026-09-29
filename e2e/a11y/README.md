# Accessibility audit (Task 041C)

WCAG 2.2 AA, audited on the real rig (frontend bundle + API + PostgreSQL +
object storage + transport, mock AI only), over the same 12 screens and the same
seeded fixtures as the 041B visual matrix.

```bash
docker compose -f tests/cross-layer/docker-compose.cross.yml up -d
npm run test:a11y            # the gate
npx playwright test --grep="@a11y"
npm run typecheck:e2e        # types for these specs (Playwright does not check them)
```

Read `tests/cross-layer/README.md` first - it is the rig runbook. The a11y suite
signs in once, navigates in-app, and pins the clock to the seeder's instant, for
the same reasons 041B does; see `e2e/visual/README.md` for that reasoning.

## What is asserted, and how

| Requirement | Spec | How it is checked |
| --- | --- | --- |
| R1 keyboard-complete | `keyboard.spec.ts` | Tab-order walk per screen; sign-in, review and export driven by Tab/Enter only; no `fill()`/`click()` in the flows |
| R2 focus-visible, labelled, contrast | `focus-contrast.spec.ts`, `axe.spec.ts` | Computed focus indicator at every stop; resolved accessible names; contrast computed from rendered colours in **both** themes |
| R3 motion-respecting | `motion.spec.ts` | Computed `transition-duration`/`animation-duration` with and without `prefers-reduced-motion` |
| R4 dialog + media | `dialogs-media.spec.ts` | Focus trapped in and restored from a dialog; transport controls reachable and named |
| R5 live regions, no spam | `live-region.spec.ts` | MutationObserver over every live region, counting announcements across a replayed SSE progress stream |

`axe-core` is injected as source and evaluated in the page (see
`support/axe.ts`), with `runOnly` set to `wcag2a`, `wcag2aa`, `wcag21a`,
`wcag21aa`, `wcag22aa` and `best-practice`. `best-practice` is included
deliberately: the landmark and heading-order rules live there, and neither is a
"real" rule that can be waived for a product reason.

## Per-screen matrix

"State" is what the rig actually renders, because a finding on an empty-state
screen is a weaker signal than one on a populated screen and the difference is
invisible in a pass/fail.

| Screen | State under the rig | axe | keyboard | focus | contrast (light/dark) | motion | dialog | media | live | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `login` | unauthenticated form | pass | pass | pass | pass / pass | n/a | n/a | n/a | pass | Now a `<main>` landmark; see fix 1 |
| `dashboard` | one tenant, seeded usage rows | pass | pass | pass | pass / pass | n/a | n/a | n/a | pass | Card titles are `h2`; see fix 2 |
| `projects` | two seeded projects | pass | pass | pass | pass / pass | pass | pass | n/a | pass | Row action Enter bug; see fix 7 |
| `wizard` | empty create form, step 1 | pass | pass | pass | pass / pass | n/a | n/a | n/a | n/a | |
| `upload` | dropzone, nothing in flight | pass | pass | pass | pass / pass | pass (no motion at rest) | pass (resting) | n/a | n/a | |
| `workspace` | MediaReady, one completed run | pass | pass | pass | pass / pass | pass | pass | n/a | pass | |
| `transcript` | one seeded segment; **error state** | pass | pass | pass | pass / pass | n/a | n/a | n/a | n/a | The error state is a backend 500, see "Findings" |
| `translation` | one segment, one translation | pass | pass | pass | pass / pass | n/a | pass | n/a | pass | Dirty-draft guard is a real modal now |
| `voices` | one speaker, two stock voices | pass | pass | pass | pass / pass | n/a | pass | n/a | n/a | Listbox/option misuse; see fix 4 |
| `review` | queue with one seeded open item | pass | pass | pass | pass / pass | n/a | n/a | pass | pass | |
| `quality` | no QC issues; empty state | pass | pass | pass | pass / pass | n/a | n/a | pass | n/a | |
| `exports` | one completed seeded export | pass | pass | pass | pass / pass | n/a | pass | n/a | pass | Inline "dialog" became a real modal |
| `timeline` *(not in the matrix)* | `GET /segments` 500s, so the workspace renders its error state | - | - | - | - | - | - | pass (error state) | - | See "Findings" |

`n/a` means the screen has nothing of that kind - no dialog, no media - and the
test asserts that absence honestly rather than skipping silently.

## Waivers

**There are none.** `support/waivers.ts` is empty, and `waivers.spec.ts` asserts
that it is empty on every run so the day it is not, the diff is the review
event.

The registry is not decorative. A waiver needs four things, and
`assertWaiversAreCurrent` fails if any is missing:

| Field | Why it is required |
| --- | --- |
| `manualVerification` | An unreviewed finding is not a false positive. What was checked by hand, and how |
| `issue` | A claim nobody can track is a claim nobody will re-check |
| `expiresOn` | The task rejects a skip-rule without one |
| `justification` | Why this is not a defect here |

Three further rules are enforced:

* a waiver naming a screen the audit does not scan fails;
* a waiver that stops matching any violation fails as **stale** (checked at scan
  time in `applyWaivers`) - a waiver that masks nothing is dead weight;
* the expiry check is itself tested against a synthetic expired entry, so a
  green check is not a check that has never run.

The worked example of a well-formed entry is a comment in
`support/waivers.ts`, deliberately kept as a comment so it cannot be mistaken
for a live waiver.

## Production defects this audit found

Seven real defects, all fixed in the owning component rather than waived. Each
was found by measuring behaviour, not by reading code, and several would have
been invisible to a screenshot.

### 1. Every component shipped unstyled (serious, app-wide)

Each primitive carried its CSS in an inline `<style>{...}</style>` tag. The app's
own Content Security Policy (`style-src 'self'`, `frontend/index.html`) blocks
inline styles, so **not one of those rules ever applied**. The browser logged a
CSP violation per component and rendered the raw elements.

The 041B visual baselines had captured that unstyled state - the dashboard
baseline is an unstyled list of text, not a dashboard. A visual gate is green on
whatever it was pointed at; this is what that looks like.

Fix: the 33 inline blocks moved verbatim to `frontend/src/styles/components.css`,
imported from `styles/index.css`. Class names unchanged, so nothing else moved.

### 2. Reduced motion did not reduce motion (serious, app-wide)

`@media (prefers-reduced-motion: reduce)` used the `*` selector with no
`!important`, so it has zero specificity and **any class-based rule wins**. The
app's own `.dp-btn { transition: opacity 120ms ease }` is one class, so every
button in the application still animated while the user had asked for no motion
at all.

Fix: `!important` on the reduced-motion declarations, which is what a user
preference is supposed to do. `motion.spec.ts` now measures both the
reduced and the no-preference context, so a "green" R3 cannot mean "nothing ever
moved".

### 3. The focus trap did not trap (serious, R4)

`useFocusTrap` filtered its tabbable list with `el.offsetParent !== null`. The
HTML spec says `offsetParent` is `null` when the element or **any ancestor** has
`position: fixed` - and `Modal`/`Drawer` render their overlay as
`position: fixed`. So every control inside a dialog reported
`offsetParent === null`, the list came back empty, and Tab walked straight out
of the dialog into the page behind it.

It passed `useFocusTrap`'s own unit test, because that test renders without the
fixed-position overlay.

Fix: `getClientRects().length > 0`, which is the correct "is this rendered"
primitive regardless of ancestor positioning. The audit's own dialog helper had
the same bug and had to be fixed the same way.

### 4. Three "dialogs" were not dialogs, and one was a dialog inside a dialog (serious, R4)

* `ExportCard`, `TranslationWorkspace`'s unsaved-draft guard and
  `PreferencesForm`'s guard each rendered an inline `role="dialog"` panel. No
  `aria-modal`, no trap, no Escape, no focus restore - a false claim, and a
  screen reader still walked the page behind them.
* `ImpactDialog` rendered a `role="dialog"` **inside** `Modal`'s dialog, so one
  interaction announced two.

Fix: the first three are the `Modal` primitive now (testids kept on the inner
content, so the 028/033/035B suites still address them); `ImpactDialog`'s inner
`role` is gone because the primitive already provides it.

### 5. Enter on a project row action did the wrong thing (serious, R1)

`DataGrid` activates a row on Enter, and `ProjectTable`'s action cell stopped
**click** propagation so an action click would not activate the row. The key path
was not stopped. Pressing Enter on **Delete** therefore fired the button *and*
activated the row, navigating to the project instead of opening the confirmation.
Archive, Cancel and Retry had the same defect, and none of them were usable from
the keyboard.

Fix: `DataGrid` ignores Enter when the event target is a control inside a cell -
the control's own activation is what the user asked for. The grid's doc comment
already claimed this behaviour; the code now matches it.

### 6. The project filter in the review studio was unusable from the keyboard (serious, R1)

It wrote through to the URL on every keystroke. Each character replaced the
search params, React Router re-rendered, and focus was lost after the first
character - a keyboard user could type `p` and nothing else. The audit's
keyboard walk hit this and could not scope the queue at all.

Fix: a local draft committed on blur or Enter, like every other filter on that
screen. The URL is still the source of truth once committed.

### 7. Dark-theme form controls were invisible, and landmarks/headings were wrong (serious, R2)

* `AuthLayout` was the only document in the app with **no `main` landmark**; the
  sign-in heading and both fields sat outside every landmark. It is also the only
  layout still using a hardcoded Tailwind palette (`bg-slate-50`/`bg-white`), so
  in dark mode a near-white `--color-text` rendered on a white card - the `h1`,
  the labels and the inputs all at 1.09:1.
* `Card` and `Panel` hardcoded their heading level, so the dashboard went
  `h1` -> `h3`. Both now take `titleLevel` (default 2 and 3 respectively).
* 52 form controls across the feature tree are written as bare
  `<input>`/`<select>`/`<textarea>` with no `dp-*` class, so they took the
  browser's light-mode defaults: white background, near-white text in dark
  theme, 1.09:1. Fixed in the base layer rather than 52 `className` additions,
  because a rule that depends on every call site remembering a class is a rule
  that will be forgotten at the next call site.
* `SpeakerList` was a `role="listbox"` of `role="option"` rows that each wrapped
  a real `<button>`: `nested-interactive` (serious), a listbox keyboard model that
  did not exist, and a `tabIndex={0}` scroll container that put a 320x8 sliver in
  the tab order. It is a plain list of buttons with `aria-current` now.

## Findings this task did not fix

Reported rather than waived, because each belongs to a different task.

### `GET /segments?pageSize=200` returns 500 (backend, 027/032 + API)

The transcript, timeline and quality features request `pageSize=200`
(`TRANSCRIPT_PAGE_SIZE`, `TRANSLATION_PAGE_SIZE`, `QUALITY_PAGE_SIZE`). The API
caps a page at 100 and throws:

```
System.ArgumentOutOfRangeException: PageSize must be in 1..100. (Parameter 'pageSize')
   at PaginatedResult`1.Create(...) in SegmentsController.List
```

which surfaces as an unhandled 500, not a 400. Two defects in one: the client
asks for more than the contract allows, and the server answers a client mistake
with an internal error.

This is what 041B reported as an undiagnosed "real client-side defect" on the
transcript screen. It is not client-side. Until it is fixed, the transcript
screen renders an error state and the timeline workspace never renders a player,
so the media assertions in `dialogs-media.spec.ts` run against the error state
and annotate the skip rather than passing vacuously.

The two-part fix: bring the three constants inside the documented range, and have
`SegmentsController.List` validate `pageSize` and return 400 rather than letting
`PaginatedResult.Create` throw.

### The review queue needs a project scope before it shows anything

`/review` renders an empty queue until a project id is typed into the filter.
That is correct behaviour (a cross-project queue would be unbounded), and the
keyboard flow types the id - but it is worth knowing that the screen 041B pinned
a baseline of is, by default, an empty state.

### `ar` translations are still partial

Direction is driven by the locale, and `ar/nav.json` has only `dashboard` and
`projects`, so the RTL layout mirrors with mostly English text. The audit scans
LTR only, deliberately: direction mirrors geometry and axe evaluates the
accessibility tree, so scanning it twice per screen would double the runtime and
prove nothing. The one thing mirroring can genuinely break - a dialog that traps
focus but renders off-screen - is `dialogs-media.spec.ts`'s territory and is
covered there.

## Known gaps

- **No CI wiring.** 042B owns it. `npm run test:a11y` needs the rig up, so it
  cannot run on a bare checkout.
- **Colour-contrast is measured in Chromium only.** It is the one criterion that
  depends on the rendering backend; the number is computed from the same engine
  that produced the visual baselines, deliberately.
- **Focus Appearance (2.4.11 / 2.4.13) is asserted in cheap form.** The audit
  checks that the ring is at least 2px and that the focused control is inside the
  viewport, not the full AAA criterion. The header is not sticky today, so there
  is nothing to obscure focus; when a sticky header lands, the viewport check is
  the one that will need to become a real obscuration check.
- **No log scrubbing.** `.artifacts/` holds unscrubbed failure screenshots.
  They contain synthetic seeded data only. 046 owns the scrubber; 042B must
  apply it before any CI attach.
- **Per-worker isolation is still outstanding** (046): `workers: 1` serialises
  the suites rather than isolating them.

## Layout

| File | Purpose |
| --- | --- |
| `axe.spec.ts` | The axe-core scan, 12 screens x 2 themes, plus scan-integrity guards |
| `keyboard.spec.ts` | R1: tab-order walks per screen, and the sign-in / review / export flows |
| `focus-contrast.spec.ts` | R2: focus indicator, labels, and a contrast pass that computes its own ratios |
| `motion.spec.ts` | R3: reduced motion, measured with and without the preference |
| `dialogs-media.spec.ts` | R4: focus trap, focus restore, and media operability |
| `live-region.spec.ts` | R5: announcement caps, live-region shape, and the secrets-in-AT check |
| `waivers.spec.ts` | The waiver registry's own rules, and audit coverage |
| `support/axe.ts` | Engine injection, the WCAG tag set, waiver application, failure formatting |
| `support/session.ts` | The audit session: two motion contexts, announcement capture, tab-order walks |
| `support/screens.ts` | Which screens are audited, which checks apply, and what the rig renders |
| `support/waivers.ts` | The waiver registry and its validity rules |

## Adding a screen

Add it to `SCREENS` in `e2e/visual/support/matrix.ts`. The a11y suite derives its
list from there, and `waivers.spec.ts` asserts the count is 12, so the diff that
adds a thirteenth screen has to say so deliberately.
