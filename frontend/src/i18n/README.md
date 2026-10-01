# i18n, RTL and locale formatting

The localization foundation. Everything that renders a human-facing string in
this product goes through the four pieces documented here, and every one of them
has a gate that fails.

Task 045 owns: the i18next wiring, the pseudo locale, the RTL layer, the
locale/timezone/number helpers, the default-locale chain, and the copy-extraction
gate. Feature tasks own the **copy**: a screen's words belong to the screen.

---

## 1. Layout

```
src/i18n/
  i18n.ts              i18next init: resources, fallbackLng, missing-key handler
  index.ts             the barrel (re-exports everything below)
  resources.ts         the bundle registry — EN/AR/RU, namespace list, key lister
  direction.ts         RTL detection, `?dir=` override, <html dir>/<html lang>
  localePreference.ts  preference → stored → browser → en
  missingKeys.ts       the in-process missing-key collector (tests + dev)
  format.ts            Task 018's surface, now delegating to lib/ (see below)
  locales/
    en/<ns>.json       19 namespaces, the fallbackLng for everything
    ar/{common,nav}.json
    ru/common.json
    pseudo.ts          the pseudo generator — derived, never committed
  README.md            this file
  pseudo.spec.tsx      pseudo + RTL + fallback chain (R3, R4, R5)
src/lib/dates/         locale/timezone date helpers (the only Intl date call sites)
src/lib/formatting/    locale number/currency/percent/bytes/plural helpers
src/styles/rtl.css     the RTL layer (three things logical properties cannot do)
```

---

## 2. Adding a string

1. Put the key in `src/i18n/locales/en/<namespace>.json`. Nested objects are
   fine; keys are read as `ns:a.b.c`.
2. Read it with `useTranslation()` and `t('ns:key')`. **Never** a bare key: the
   default namespace is `common`, and a bare `t('save')` resolves against
   `common` instead of failing where you would notice.
3. Interpolate with `{{name}}`: `t('ns:key', { name })`. React already escapes;
   `escapeValue` is off on purpose and double-escaping would corrupt placeholders.
4. For a count, use i18next's suffix convention and provide **every** category the
   locale needs: `items_one` / `items_other` for English, `_one/_few/_many/_other`
   for Russian, all six for Arabic. `pseudo.spec.tsx` asserts the shipped
   bundles are complete for their own locale.
5. Run `node scripts/check-no-hardcoded-copy.mjs`. A new literal outside the
   baseline fails.

The gate's own scan is AST-based (`scripts/check-no-hardcoded-copy.mjs`, rules in
one exported table), so it sees `>Save<`, `title="Save"`,
`aria-label={`Export ${id}`}`, `error?.message ?? 'Could not load'`, and a
`margin-left` in the same pass.

### Exempt from the gate

`src/i18n/` (the bundles), `src/components/` (Task 016 primitives take copy as
props), `src/api/`, `src/telemetry/`, `src/types/`, `src/mocks/`, `src/config/`,
and every `*.test.*` / `*.spec.*` / `*.stories.*` file. Each is listed with a
reason in `EXEMPT_PATHS`, and the suite asserts that list and that every path in
it still exists.

---

## 3. RTL

`<html dir>` is set from the active locale: `ltr` unless the locale's base
language is RTL (`ar`, `ckb`, `dv`, `fa`, `he`, `ps`, `sd`, `ug`, `ur`, `yi`).
`<html lang>` is set from the **full tag** — `ar-EG`, `en-US` — because `dir`
answers "which way does this read" and `lang` answers "which locale is this", and
they are not the same question.

### The CSS is already logical

Task 016 wrote the component stylesheet with `inline-size`, `margin-inline`,
`inset-inline-start`, `text-align: start` and `border-inline-start-*`. Setting
`dir="rtl"` therefore mirrors the header, sidebar, cards, tables, drawers,
switches, tooltips and breadcrumbs with **no** `[dir="rtl"]` override block.
A direction-override block is a symptom that something was written physically.

`src/styles/rtl.css` is short because of that, and covers only the three things
logical properties cannot do:

| Class | Why |
| --- | --- |
| `.dp-icon-mirror` | A glyph that encodes a direction (a back arrow) needs flipping; a magnifier does not. Opt-in, so a wrong guess is visible in the markup. |
| `.dp-icon-no-mirror` | The explicit opt-out for a glyph the font already mirrors. |
| `[dir='rtl'] { font-family }` | Several latin-first UI stacks have no Arabic or Hebrew glyphs; listing those faces *before* the latin stack is what prevents tofu. |
| `.dp-bidi-isolate` | Wraps an ID, a code or a pasted timestamp so the bidi algorithm does not reorder it inside RTL prose. |
| `.dp-numerals-ltr` | Pins western digits for a timecode or a score inside an RTL run. |
| `.dp-scroll-inline` | A scroll panel must not pin its content to a physical edge, or it opens scrolled to the middle in RTL. |

### `?dir=rtl` — the manual override

```
http://localhost:5173/dashboard?dir=rtl
```

**Presentation only.** It reads one query parameter and writes exactly one DOM
attribute. It deliberately does **not**:

- change the active locale (so you get English copy in a mirrored layout — mixing
  the two hides a chrome bug behind Arabic's different reflow),
- touch `useAppStore`, i18next, auth, tenant scoping, or any API request,
- survive a navigation that rewrites the query string.

Accepted values are exactly `ltr` and `rtl`, case-insensitive, surrounding space
tolerated. Anything else (`?dir=sideways`, `?dir=`) is **ignored**, not guessed
at: a typo in a debug switch must not silently mirror a production screen.

### The timeline canvas stays LTR

`features/timeline/Waveform.tsx` pins `dir="ltr"` on the `<canvas>`, with the
reason in the markup. `drawWaveform` paints with physical canvas coordinates and
`seekFromClientX` maps `clientX - rect.left`, so both the drawing and the hit
test are left-to-right by construction. Inheriting `dir="rtl"` would not change
either — a 2D context has no direction — but it would flip the focused range
control's own direction and let a screen reader read an LTR timeline as RTL. The
chrome around it (section, label, scrub input) mirrors normally. Mirroring the
canvas itself is a change to playback semantics and is deliberately not made here.

---

## 4. The pseudo locale

`pseudo` is `en` with every string bracketed and expanded by ~35%
(`Retry` → `[ŕèţŕýŕè]`). It exists to find three things cheaply:

1. **Hard-coded copy.** Under `pseudo` there is no English left in the bundles,
   so anything still reading English did not come from i18n.
2. **Non-expanding labels.** A label that grows by 35% and a sibling that does
   not breaks the row; a fixed-width chip overflows.
3. **Missing keys.** i18next renders the key name; under `pseudo` that is
   `[nav:dashboard]`, unmissable in a screenshot.

### Why there is no `locales/pseudo.json`

There is no committed pseudo bundle, and that is the point.

A committed one is a second copy of every English string in the product. It goes
stale the moment anyone adds a key; a stale one produces exactly the "missing
key" failure it exists to detect; and the resulting red test is indistinguishable
from a real missing key — so the reflex is to regenerate it, which is how a
second copy of the truth became the first thing to break.

`locales/pseudo.ts` derives the bundle from `EN_RESOURCES` at module load, which
makes it **structurally incapable** of missing a key. `pseudo.spec.tsx` still
asserts full key coverage, so the derivation is proven rather than assumed.

Rules (`pseudoLocalize`, pure):

- ASCII letters map to an accented look-alike; non-ASCII is left alone, so a key
  that legitimately contains `العربية` still shows that it resolved.
- +35% padding, taken from the string's own accents, then wrapped in `[ ]`.
- `{{placeholders}}` are lifted out before transformation and restored byte for
  byte — a pseudo bundle that mangled `{{count}}` would make a plural test pass
  for the wrong reason.
- Keys are never transformed, only values, so `_one`/`_few`/`_other` keeps
  selecting the same category everywhere.

`pseudo` is registered with i18next but is **not** in `SUPPORTED_LOCALES`, so it
can never appear in the locale switcher or the preferences form.

---

## 5. Missing keys

A missing key resolves through `fallbackLng` (`en`), and failing that renders the
key name. Never a blank string, never a thrown error, never a blank screen.

Two observers watch the same `missingKeyHandler`:

| | Channel | Gate |
| --- | --- | --- |
| Production | `trackMissingTranslation` telemetry (allowlisted scalars, kill switch, user opt-out) | none needed |
| Tests | `getMissingTranslations()` / `missingTranslationKeys()` in `missingKeys.ts` | in-process, never gated, `resetMissingTranslations()`-able |

Telemetry is the wrong channel for R4: it is gated on a build flag *and* the
user's opt-out, so a suite that only asserted on telemetry would assert nothing
whenever the flag is off. `pseudo.spec.tsx` therefore also asserts the collector
is live before relying on it being empty — a collector that never records makes
every "no missing keys" assertion in the file vacuously true.

---

## 6. Dates, numbers and timezones

`src/lib/dates/dates.ts` and `src/lib/formatting/formatting.ts` hold the only
`Intl.DateTimeFormat` / `Intl.NumberFormat` call sites in the product.
`src/i18n/format.ts` re-exports the Task 018 names from them rather than keeping
a second copy: two date formatters is how the transcript timestamps get
`medium` and the export list gets `short` forever, and only one of them has
tests.

### The fallback chains

| | Chain |
| --- | --- |
| Locale | `preference` (`/me` `locale`) → `stored` (`localStorage['dubbing.locale']`, read through `useAppStore.locale`) → `browser` (`navigator.languages`) → `en` |
| Timezone | `preference` (`timezone` preference) → `UTC` |

`stored` sits between `preference` and `browser` because it is the same thing by
another transport — an explicit choice the user made in this browser. Without it,
a user who switched to Arabic and signed out gets English on the login screen.
The task specifies `preference → browser → en`; this refines it, and
`docs/i18n.md` records why.

One consequence worth stating: `useAppStore.locale` is seeded with `'en'` when
`localStorage` has nothing, so on a browser with no stored choice the `stored`
rung resolves to `en` and the `browser` rung is unreachable. In practice that is
moot — `MeController` defaults `MeResponse.Locale` to `en-US` and always emits the
field, so the `preference` rung wins against any current backend. The `browser`
rung exists for an older backend that omits it, and the only thing it changes
there is the tag (`en` vs `en-US`), i.e. the date format. Distinguishing "the user
chose `en`" from "the store defaulted to `en`" would need a second store field
and buys a date-format difference on a path no supported deployment takes.

`resolveExplicitTimeZone` is the other half: a render may use the browser zone,
but an audit row or an export manifest must not, or two operators in two
browsers see different strings for the same event. It returns the preference or
`UTC`, never the browser zone.

Every step can fail and every failure is `en`/`UTC`: an invalid preference
(`"not a locale"`), an unsupported one (`"xh"`), a non-string one (`42`), a blank
`localStorage` value, a `navigator` reporting `["zz-ZZ"]`. `resolveLocale` returns
the reason for every candidate it refused, so "no preference at all" is
distinguishable from "a preference we could not honour" — the second is a data
problem somebody has to fix, the first is not.

### Two details that are load-bearing

- **`formatTimestamp` uses two `Intl` calls, not one.** `timeZoneName` cannot be
  passed alongside `dateStyle`/`timeStyle`; the `*Style` shortcuts and the
  individual component options are mutually exclusive and asking for both throws.
  The `catch` would then have returned `toISOString()`, i.e.
  `2024-01-15T12:00:00.000Z` — the "never throws" guarantee quietly turning into
  a machine string on screen.
- **An invalid locale tag resolves to `en` copy, not to ISO-8601.** Before Task
  045 an unformattable tag threw out of `Intl` and fell through to
  `toISOString()`, so a typo in a preference row put a machine timestamp on
  screen. `resolveLocale` now resolves the tag before formatting.

---

## 7. Bundles are compiled in, never fetched

There is no i18next `backendConnector` and no `loadPath`. A translation bundle
fetched at runtime is a JSON file chosen by someone else's server that rewrites
every button in the product — including "Delete project" and any error text a
user copies into a support ticket.

Shipping a locale means committing a directory under `src/i18n/locales/` and
adding it to `RESOURCES`. `ar` and `ru` are deliberately partial: `en` fills the
rest, and `pseudo.spec.tsx` asserts the plural categories each partial bundle
needs are all present (a Russian bundle with `items_one`/`items_other` and no
`items_few` is not a missing-key warning — it silently renders English for 2–4
items).

---

## 8. Gates

| Gate | Command | What it fails on |
| --- | --- | --- |
| Copy extraction + RTL physical sides | `node scripts/check-no-hardcoded-copy.mjs` | a literal outside `scripts/hardcoded-copy-baseline.json`, or a per-file count that went up |
| Missing-key coverage | `npm run test --prefix frontend -- src/i18n` | a key the pseudo run could not resolve, a plural family missing a category, a bundle drift |
| Physical CSS sides | same gate, rule `physical-side` | `ml-*`, `pr-*`, `left-*`, `borderLeft`, `text-left`, … anywhere outside the baseline |
| Typecheck / lint | `npm run typecheck --prefix frontend`, `npm run lint --prefix frontend` | — |

The copy gate is a **ratchet**, not a zero-tolerance gate, and that is deliberate:
it landed on a tree with 939 pre-existing literals across the feature areas
owned by Tasks 019–036, whose own task files list "feature screen copy" as their
deliverable. New literals fail; existing counts may only go down; inflating the
baseline is a two-place edit (the per-file map *and* the per-rule totals, which
are cross-checked against each other and reported as `COPY_GATE_BASELINE_DRIFT`
if they disagree). `--strict` ignores the baseline entirely.

Its tests are `deploy/frontend/hardcoded-copy.test.mjs`
(`npm run check:frontend`), and they do the two halves that matter: every rule is
driven with a synthetic source written the way the mistake is actually written,
and every rule is driven with the correct code the task exists to produce. A gate
that fires on `t('nav:primary')` gets disabled within a week, and a disabled gate
is worse than no gate because it is believed.

---

## 9. Known limits

- **The copy gate is a ratchet.** 939 literals remain in the feature areas, all
  recorded in the baseline with a per-file breakdown. `docs/i18n.md` carries the
  current count.
- **`pseudo.spec.tsx` has no layout engine.** jsdom does not lay out, so "no
  layout crash" means the tree rendered with every landmark attached, and "no
  overlapping chrome" is asserted structurally (distinct, non-nested siblings).
  The pixel half of the RTL claim is Task 041B's visual matrix.
- **The Arabic bundle covers `common` and `nav` only.** Everything else renders
  English under `ar`, which is the intended partial-bundle behaviour, not a bug.
- **No remote bundles, no over-the-air locale updates.** Adding a language is a
  code change.