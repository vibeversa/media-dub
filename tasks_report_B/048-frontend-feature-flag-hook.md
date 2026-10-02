# Task 048 — Frontend Feature-Flag Evaluation Hook

## Status

**COMPLETED.** Both files the task names exist, R1–R5 are satisfied, and the
`Validation` block passes verbatim. The work is
`useFeatureFlag(key): boolean`, one hook, one resolver, and a gate that makes
the second version of either a failing suite. Five call sites in three files
(`EnrichmentGate.tsx` ×3, `AdminPage.tsx`, `LocalGpuPanel.tsx`), one `/me` read
per session, and every default off.

The task's own assumption — "plan names no hook path, key names, or per-flag
defaults" — turned out to be load-bearing in a better way than expected. The
six keys are not new: five of them already existed as *behaviour nobody could
read*. `config/env.ts` resolved `analyticsEnabled`, `diagnosticsEnabled` and
`experimentalFeaturesEnabled` from the three `VITE_ENABLE_*` variables and
**nothing consumed them** (verified by grep: three definitions, zero readers),
and `features/enrichment/enrichmentFlags.ts` had a second, private `/me` reader
with a private parser. So "no shared flag hook exists" understated the state:
there were two flag mechanisms, neither reachable from the other, and 044's own
report had already recorded the cost as **G3 — "the flag read is one extra
`GET /me`"**, naming this task as the place to fix it. That is now fixed: the
session resolution seeds the flag cache from the `/me` document it already
reads, and a test asserts that N mounted surfaces produce **one** request, and
that a seeded session produces **zero**.

## Summary

Task 048 adds the product-wide flag evaluator and makes it the only one.
`useFeatureFlag(key): boolean` resolves from `/me`'s `featureFlags` slice first,
then from build-time bootstrap config (`config/env.ts`'s `VITE_ENABLE_*`
family), then from an all-false default map — one sentence, implemented once in
`resolveFeatureFlag`, with a source label (`ME` / `BOOTSTRAP` / `DEFAULT`) so a
caller can explain a decision instead of guessing. Every default is `false`,
every unknown input resolves `false`, and the resolver is total: a key outside
the vocabulary resolves `false` rather than throwing or reading as `undefined`.
The three capabilities Task 044 gated (`videoIntel`, `lipSync`, `localGpu`) now
read through it, and the admin area's operator-only local-GPU section reads it
directly, so both areas have literal call sites (five calls in three files) while
`RequireAdmin`, `useAdminGuard` and `LocalGpuPanel`'s own permission re-check
stay the authority. A flag can make nothing visible that a permission would not
have allowed, and a test proves it with the real guards and a non-elevated
permission list.

## Files Created/Modified

### Created

| File | What it is |
| --- | --- |
| `frontend/src/config/featureFlags.ts` | The vocabulary (`FEATURE_FLAG_KEYS`, six keys), the fail-closed defaults, the `/me` wire-key table, the pure parser `readMeFeatureFlagSlice`, the pure resolver `resolveFeatureFlag`/`resolveFeatureFlags` (+ `resolveFeatureFlagDetailed` with the source label), and the only impure wrapper `readBootstrapFeatureFlags`/`evaluateFeatureFlag(s)` — which never throws, because an invalid `VITE_*` degrades a flag to off instead of taking the shell down. No React, no I/O. |
| `frontend/src/hooks/useFeatureFlag.ts` | `useFeatureFlag`, `useFeatureFlags`, `useFeatureFlagSnapshot`, `useFeatureFlagsQuery`, `FeatureFlagProvider` (test/e2e seam), `FEATURE_FLAG_STALE_TIME_MS`. One cache entry (`queryKeys.me.featureFlags()`) for the whole session; `enabled` only for an authenticated session; `retry: false`; no focus/reconnect refetch. Emits no telemetry. |
| `frontend/src/hooks/__tests__/useFeatureFlag.test.tsx` | **29 tests** — the suite the task names. True/false per key, unknown key, unreadable `/me`, non-boolean values, alias spellings, `/me`-over-bootstrap both ways, recognised-false-over-bootstrap-true, source labels, one read for N consumers, zero reads when seeded, a working flag surviving a failed refetch, 403/423 leaving flag state unchanged, no telemetry, and the R3 negative matrix (flag ON + `project.view` → forbidden at the section guard **and** a redirect at `RequireAdmin`). |
| `frontend/src/hooks/useFeatureFlag.gate.test.tsx` | **15 tests** — the R1 grep gate, co-located on purpose (see Decisions Made #2). Four rules over comment-stripped source: `WIRE_KEY_UNDECLARED`, `BOOTSTRAP_FLAG_UNDECLARED`, `ME_REQUEST_UNDECLARED`, `ME_DOCUMENT_UNDECLARED`; each rule's subjects are read out of the owning modules (`meFeatureFlagWireKeys()`, `DEPLOY_CONFIG_ALLOWLIST`) so the rule cannot drift from the data. Every rule is first driven with a synthetic source written the way the mistake is written, the allowlists are asserted to be exactly four entries each with prose, the real tree is scanned, and R5's call sites are asserted exhaustively (exactly three files call the hook). |

### Modified

| File | What changed |
| --- | --- |
| `frontend/src/api/queryKeys/keys.ts` | New `me.featureFlags()` scope (`['me','feature-flags']`, beside `preferences`, because it is the same document). **Removed** `enrichment.flags()`: two keys for one document is one key too many, and its only two users were 044's query and its test. |
| `frontend/src/features/auth/authStore.ts` | `resolveIdentityHints` now seeds the flag cache from the `/me` document it already read (`queryClient.setQueryData(queryKeys.me.featureFlags(), readMeFeatureFlagSlice(raw))`), before the status flips. This is what closes 044's G3. |
| `frontend/src/features/enrichment/enrichmentFlags.ts` | Reduced from 260 lines to a vocabulary module: `ENRICHMENT_FLAG_NAMES`, `EnrichmentFlagName`, and the total typed `ENRICHMENT_FLAG_KEY_BY_NAME` map. The private `/me` read, the private parser, the private query, `EnrichmentFlagsProvider` and the private pure predicates are gone — one source, one parser, one resolver. |
| `frontend/src/features/enrichment/EnrichmentGate.tsx` | `EnrichmentGate` and `ProjectEnrichment` call `useFeatureFlag(ENRICHMENT_FLAG_KEY_BY_NAME[…])`. The prop still takes 044's vocabulary (`flag="lipSync"`), the R6 bundle contract is untouched, and the gate still returns `null` before anything else happens. |
| `frontend/src/features/admin/AdminPage.tsx` | The operator-only local-GPU section is gated by `useFeatureFlag('localInference')` directly — **the admin area's call site** — instead of through `EnrichmentGate`. Same zero-chrome property (the wrapper `div` is not emitted when the flag is off), same ordering (guard first, flag second). |
| `frontend/src/features/admin/LocalGpuPanel.tsx` | Reads `useFeatureFlag('localInference')` itself instead of `useEnrichmentFlags()`, so the panel's own flag + permission re-check survives a refactor that moves the section out from under `AdminPage`'s check. The `enabled` prop is kept (it is the panel's spec seam) with its doc corrected. |
| `frontend/src/features/enrichment/index.ts` | Barrel: five removed exports, two added (`ENRICHMENT_FLAG_KEY_BY_NAME`, `ENRICHMENT_FLAG_NAMES`). |
| `frontend/src/hooks/index.ts` | Was `export {}` with "Shared hooks land in later tasks"; now re-exports the flag hook, so the barrel test covers it. |
| `frontend/src/features/enrichment/__tests__/enrichment.test.tsx` | 044's suite, 56 → 54 tests. The world now holds the **wire** `/me` slice (`{ videoIntelligenceEnabled: true }`) instead of 044's private vocabulary; the two parser-matrix tests moved to the shared parser's own suite (Decision #4); the "earlier resolution survives a failed refetch" test now mutates the shared cache entry **and asserts the entry exists**; the pure-predicate test became the capability→key mapping assertion; the snapshot test became "the gate resolves off the shared snapshot and one `/me` read serves it". |
| `docs/rollout.md` | New §4a "Where the browser reads a flag (Task 048)": the two sources, the one-sentence precedence rule, the single evaluation site, both halves of fail-closed, and "a flag grants nothing". It also corrects §4's "flag service" framing — this product has no flag service (plan §6.9), so the browser reads `/me` or the bundle. |
| `docs/test-ownership.md` | `src/hooks` row names the 048 co-located gate and why; new `src/config` row says where the flag resolver's matrix lives and why it is not duplicated. |

## Decisions Made

1. **The hook owns one cache entry; `authStore` seeds it.** Task 044's report
   named the extra `/me` read as this task's job. Two designs would have closed
   it: read the flags from a zustand slice, or keep the query and have the
   session resolution write into it. The query was kept because the query cache
   is what clears on logout (`queryClient.clear()` runs before the status flip),
   which is the hygiene property a flag slice needs and which a store would have
   had to be taught separately. The seed keeps the hook's `queryFn` as a
   genuine fallback — every suite that fakes `authenticated` without running the
   login path depends on it — instead of deleting the code path the tests use.

2. **The R1 gate is co-located, and it carries a pointer to the required suite.**
   Vitest treats a positional filter as a **path pattern**, so
   `npm run test -- src/hooks/useFeatureFlag` selects paths beginning with that
   string. Verified empirically in this repo: `src/components/Select` selects
   `src/components/Select/Select.test.tsx`; `src/hooks/useFeatureFlag` selects
   `src/hooks/useFeatureFlag.gate.test.tsx` and **not**
   `src/hooks/__tests__/useFeatureFlag.test.tsx`; `src/hooks/useFeatureFlag.test`
   selects nothing and exits 1. So the unit suite stayed at the path the task
   names, and the gate lives beside the hook where the validation command can
   reach it. Because that means the task's validation command does not *execute*
   the unit suite, the gate asserts the required suite exists at the required
   path and still declares the four cases the task listed. That is a pointer, not
   evidence — it is labelled as such in the file, and the suite itself is run by
   `npm run test -- src/hooks/__tests__/useFeatureFlag` and by the full suite
   (1835/1835). Co-location is not a new convention here: `src/components/**/X.test.tsx`
   and `src/i18n/pseudo.spec.tsx` already do it.

3. **Six keys, chosen to need no deploy change.** `analytics`,
   `diagnostics`, `experimentalFeatures` (bootstrap: the three
   `VITE_ENABLE_*` variables already allowlisted and already resolved by
   `config/env.ts`) plus `videoIntelligence`, `lipSync`, `localInference`
   (`/me`'s three `MeFeatureFlags` booleans). Inventing a new
   `VITE_ENABLE_VIDEO_INTELLIGENCE` would have meant editing the deploy
   allowlist, `.env.example`, `deploy/config-inject.sh` and
   `deploy/k8s/frontend/configmap.yaml`, and every one of those is a second
   declaration of the same fact — the exact shape R1 exists to forbid. A test
   pins the correspondence: the `VITE_ENABLE_*` names in `DEPLOY_CONFIG_ALLOWLIST`
   are exactly these three.

4. **The parser matrix moved with the parser.** 044's suite had two tests for
   `parseEnrichmentFlags` (garbage documents, alias spellings, non-boolean
   values, unwrapped slice). Keeping them would have meant keeping a second
   parser, and the tests were rewritten against `readMeFeatureFlagSlice` in the
   new suite — same matrix, one implementation, more cases (recognised-but-false
   retained, unknown wire key dropped, `undefined` vs `{}` slice, total result
   map). The count went 56 → 54 in 044's suite and 0 → 29 in the new one; no
   assertion was dropped, two titles moved with the code they test.

5. **`/me` "states nothing" is not "`/me` states false".** Three states, not
   two: stated-true (on), stated-false (off, and bootstrap is *not* consulted),
   unstated (bootstrap decides, then the all-false default). Collapsing the
   third into the second would mean an operator who disabled enrichment in the
   admin panel sees it back because the bundle was built with the switch on;
   collapsing it into "on" would mean a garbage document opens every surface.
   Both directions are asserted.

6. **"Unstated" is enforced by *dropping*, not by throwing.** The parser drops
   a wire key it does not own and a value that is not a boolean, so the key
   simply stays absent. This preserves 044's documented reason for refusing the
   client-side aliases (`{ videoIntel: true }` is a guess, not an answer) while
   keeping the parser total.

7. **A session that is not authenticated has no flags.** 044 did not gate its
   query result on `useIsAuthenticated()` beyond the `enabled` flag, so a seeded
   slice survived an `expired` status until the shell redirected. This task's
   edge case names the non-admin shell explicitly, so `useFeatureFlags` treats
   "not authenticated" as "nothing trustworthy to resolve from" and the seeded
   cache becomes invisible the moment the session is gone. `logout`'s
   `queryClient.clear()` remains the second line. An explicit
   `FeatureFlagProvider` still wins, because a provider is a test seam that has
   no session to check.

8. **The bootstrap read cannot throw.** `getDeployConfig()` throws by design on
   a non-allowlisted or secret-shaped injected value. A flag evaluation is not
   where that surfaces — the surfaces it gates are experimental, and
   `lib/env.ts`'s startup guard plus `scripts/vite-env-audit.sh` are where a
   human sees the error. The try/catch is asserted by poisoning
   `VITE_API_BASE_URL` with a `javascript:` URL (which is a valid URL to zod and
   `UNSAFE_URL` to the audit) and asserting all six flags resolve off with the
   shell still rendering.

9. **No telemetry, ever.** The task allows a flag's key and boolean and nothing
   else; emitting nothing is the only way to guarantee no later refactor widens
   it. `getBufferedEvents()` is asserted empty after a resolution.

10. **The admin call site is a real gate, not a new banner.** `AdminPage` reads
    `useFeatureFlag('localInference')` and conditionally emits the section,
    replacing `<EnrichmentGate flag="localGpu">`. The obvious alternative — a
    flag-gated rollout notice inside `FlagsPanel` — would have needed new copy,
    and `scripts/check-no-hardcoded-copy.mjs` fails every new literal, so it
    would have meant a new i18n key in three locales for a line whose only
    function is to prove the gate was wired. Zero chrome when off, no new copy,
    no behaviour change: the better answer.

11. **A refused action cannot move a flag.** The 403/423 edge case is asserted
    at the cache level: after a resolved ON flag, a denied gated request leaves
    the `me.featureFlags()` entry byte-identical and the hook still reporting
    ON. The hook has no optimistic local state to be inconsistent with.

12. **`docs/rollout.md` §4a is in scope.** §4 told a reader to expect a "flag
    service" and a `DegradedState` banner on flag failure; this product has
    neither (plan §6.9, no DB-backed flags). Documenting the real contract next
    to the promise it implements was cheaper than leaving a rollout document
    that contradicts the code it governs.

## Build/Test Results

### The task's `Validation` block, verbatim

```
$ cd frontend && npm run typecheck
> dubbing-frontend@0.1.0 typecheck
> tsc --noEmit -p tsconfig.json
EXIT=0

$ cd frontend && npm run test -- src/hooks/useFeatureFlag
 RUN  v2.1.8 /workspaces/media-dub/frontend
 ✓ src/hooks/useFeatureFlag.gate.test.tsx (15 tests) 1412ms
   ✓ R5 the hook is wired into the admin and enrichment surfaces > an admin surface calls it 393ms
 Test Files  1 passed (1)
      Tests  15 passed (15)
   Duration  2.74s
EXIT=0
```

(That filter selects the R1/R5 gate; see Decisions Made #2 for why the required
unit suite is not reachable by that string, and what the gate asserts instead.)

### The two new suites, selected directly

```
$ npm run test -- src/hooks/__tests__/useFeatureFlag
 ✓ src/hooks/__tests__/useFeatureFlag.test.tsx (29 tests) 831ms
 Test Files  1 passed (1)
      Tests  29 passed (29)
EXIT=0

$ npm run test -- src/hooks/__tests__/useFeatureFlag src/features/enrichment src/features/admin
 ✓ src/features/admin/__tests__/adminMatrix.test.tsx (24 tests) 1759ms
 ✓ src/features/admin/__tests__/admin.test.tsx (14 tests) 886ms
 Test Files  6 passed (6)
      Tests  201 passed (201)
EXIT=0
```

### The whole frontend suite

```
$ npm run test --prefix frontend      # 153 files, was 151
 Test Files  153 passed (153)
      Tests  1835 passed (1835)        # was 1793: +44 new (29 + 15), -2 moved out of 044's suite
   Duration  215.68s
EXIT=0

$ npm run lint --prefix frontend --max-warnings=0
EXIT=0
$ npm run typecheck --prefix frontend
EXIT=0
$ npm run test:presence --prefix frontend
PRESENCE_OK:27 areas with specs
EXIT=0
```

### Coverage of the two new modules (the 80% gate is global; measured on the two suites that own them)

```
config/featureFlags.ts        lines 98.44  branches 97.77  functions 100.00  statements 98.44
hooks/useFeatureFlag.ts       lines 96.07  branches 100.00  functions  87.50  statements 96.07
features/enrichment/enrichmentFlags.ts  lines 100  branches 100  functions 100  statements 100
```

### The consumed E2E, unchanged and green (044's own command)

```
$ npx playwright test --grep="@optional-enrichment"
  ✓ flags on: both panels render as separate artifacts @optional-enrichment (7.6s)
  ✓ flags on: the operator GPU panel is visible only in Admin (1.9s)
  ✓ flags off: zero enrichment chrome, zero enrichment requests (1.6s)
  ✓ flags off: the admin area shows no GPU section at all (1.1s)
  ✓ flags on, backend broken: the core transcript journey still completes (2.2s)
  ✓ flags on, not provisioned: panels say so without looking broken (1.4s)
  ✓ flags on, GPU health broken: the rest of the admin area is unaffected (1.2s)
  ✓ flags on, ordinary user: no GPU internals reach the page (952ms)
  8 passed (33.2s)
EXIT=0
```

This spec is the strongest evidence that the seed is right: it drives flags
through `page.route` on `/api/v1/me`, the app now resolves them from the
session resolution's own read, and all eight scenarios — including
"zero enrichment requests with the flags off" — still hold.

### The repository gates

```
$ node scripts/check-no-hardcoded-copy.mjs
COPY_GATE_RESULT reason=OK status=PASS files=218 findings=939 new=0
EXIT=0
$ node scripts/check-frontend-topology.mjs
FRONTEND_TOPOLOGY_RESULT reason=OK status=PASS files=297 findings=0
EXIT=0
$ npm run check:frontend                 # node --test deploy/frontend/*.test.mjs
# pass 62  # fail 0                     EXIT=0
$ npm run check:rollout
ROLLOUT_GATE_RESULT reason=OK status=PASS migrations=7 findings=0    EXIT=0
$ npm run check:unit-containers
CI_GATE_RESULT reason=OK status=PASS files=77 tests=1440             EXIT=0
$ npm run check:vite-env
VITE_ENV_AUDIT_RESULT reason=OK status=PASS files=2 keys=15          EXIT=0
$ npm run typecheck:e2e                                                EXIT=0
$ npm run test:tools            # node --test tools/*.test.mjs deploy/frontend/*.test.mjs
1..409  # pass 408  # fail 1      <- tools/npm-audit-gate.test.mjs, PRE-EXISTING (see below)
```

### Red and pre-existing — do not "fix" by suppressing

- `tools/npm-audit-gate.test.mjs` → 1 failure (`a shell-free candidate must
  exist before the shell fallback is reached`). Byte-identical to the 045, 046
  and 047 reports. Unrelated to flags; no file it reads was touched.
- `frontend/e2e/admin.spec.ts` → 3 failures (`elevated login reaches the ops
  dashboard`, `destructive flow requires confirm plus reason`, `non-elevated
  login sees the 403 state`), all timing out on `getByTestId('auth-tenant')`
  during a UI login. **Verified pre-existing**: `git stash push -u` (tree back
  to `aacc2a3`) and the same three tests fail identically, then the stash was
  popped. The other 7 tests in those three specs (`auth.spec.ts`, `shell.spec.ts`)
  pass with the change in place.
- `npm run check:backup-policy` remains `GAP_BLOCKS_RELEASE` (047's D1a/F1) and
  `kustomize build staging + prod` remains 043A Finding 6. Neither is touched by
  this task.

### Not run, and why

`dotnet build` / `dotnet test` were **not** run: this task changed no `.cs`, no
migration, no deploy manifest and no OpenAPI file (`git status` shows changes
only under `docs/`, `frontend/src/`), so the .NET build and test inputs are
byte-identical to the 047 baseline. The .NET SDK is also not on this host's
`PATH` (047's warning; it lives in `/tmp/opencode`, which is wiped on a server
restart). The full cross-layer Playwright rig was not run either: `tests/cross-layer`
is not runnable on this host (MinIO is blocked — 046/047 reports), and the
frontend's own mock-based e2e is the tier this task can move.

## Findings

Every one of these was found by running the thing, not by reading it.

### 1. Three `VITE_ENABLE_*` values were resolved and read by nobody

`config/env.ts` computed `analyticsEnabled`, `diagnosticsEnabled` and
`experimentalFeaturesEnabled` from `VITE_ENABLE_ANALYTICS`,
`VITE_ENABLE_DIAGNOSTICS` and `VITE_ENABLE_EXPERIMENTAL_FEATURES` and handed them
to nobody — three definitions, zero consumers, since Task 043. That is a
configuration value nobody has decided is safe *and* a rollout switch nobody can
throw, which is the same defect wearing a different hat: an input with no
consumer reads identically to an input that is unused on purpose. Two of the six
keys this task ships were those three; a test now pins that every bootstrap key
corresponds to an allowlisted variable, because a reader naming a variable the
allowlist does not carry is a flag that can never be on in a real build.

### 2. "Exists" and "is reachable" are different claims (043C Finding 1, again)

The first version of the resolver indexed `FEATURE_FLAG_DEFAULTS[key]` directly,
which throws for a key outside the vocabulary — so "unknown flag → false" was
true only because TypeScript said so. The runtime floor is now explicit
(`hasOwnProperty` guard, asserted with a cast to a key that does not exist) and
`ME_FEATURE_FLAG_WIRE_KEYS[key]` is read only after the guard. The same shape
reappears at a larger scale in the coverage gate: the first assertion in the 044
blip test was `cache.find(…)?.setState(…)`, i.e. a mutation that passes when it
finds nothing. It now asserts the cache entry exists before mutating it.

### 3. `meReads() === 1` is not "the read landed"

A `waitFor(() => expect(meReads()).toBe(1))` passed as soon as the request went
out, not when the answer arrived — so the first version of the "true and false
for every stated flag" test asserted all-false and looked like a resolver bug.
The same mistake is one line away in the bootstrap-precedence test: the first
probe in that tree reports `true` from **bootstrap**, not from `/me`, so waiting
on it proved nothing about the `/me` half. Both now wait on the value that could
only come from the document.

### 4. The provider has to suppress the read, not just shadow it

`FeatureFlagProvider` overriding the *resolved map* while the query still fired
meant every suite using the seam also issued a real `/me` request: correct, but it
made a hermetic test depend on a network mock (and would have failed outright in
an MSW suite with `onUnhandledRequest: 'error'`). The query is now parameterised
by `enabled`, and the input hook passes `authenticated && override === undefined`.
The public `useFeatureFlagsQuery()` keeps its own meaning, so a caller that wants
the read still gets it.

### 5. A scan gate needs to know the difference between code and a comment

The first pass of `WIRE_KEY_UNDECLARED` failed on `enrichmentFlags.ts` — a
comment explaining why the wire names are exactly what they are. A scanner that
cannot tell a comment from code does not enforce a rule; it forbids a word, and
the first response to a forbidden word is a vaguer explanation. `stripComments`
is now part of the gate, it preserves `//` inside strings (the one real hazard —
`'https://api.example.com'` must not swallow the rest of the line), and both are
asserted on synthetic input. The same trap applies to `/me` request detection,
where `authStore`'s comments are full of `` `/me` `` and the rule is a call shape
rather than a bare literal.

### 6. A declaration is not a call

`ME_DOCUMENT_UNDECLARED` fired on `features/auth/api.ts`, which *defines*
`fetchMeDocument`. The first fix — allowlist the file — would also have exempted
a real call inside it. The rule now uses `(?<!function )`, so the declaration is
not a call and the allowlist stays two entries.

### 7. Vitest's positional filter is a path pattern, not a substring

This is Finding 2 in its most expensive costume. Three probes in this repo:
`npm run test -- src/hooks/useFeatureFlag` selects the co-located gate file and
**not** `src/hooks/__tests__/useFeatureFlag.test.tsx`;
`npm run test -- src/hooks/__tests__/useFeatureFlag` selects the unit suite;
`npm run test -- src/hooks/useFeatureFlag.test` selects **nothing** and exits 1,
even though that string is a prefix of the required file's name. A validation
command that silently matches nothing is worse than one that fails, and one that
silently matches something *else* is worse still — it is the 043C class again,
one level up.

### 8. Everything already green stayed green, including the parts I did not touch

044's import gate (which asserts the exact set of enrichment modules in a
user-loadable chunk) passes **unchanged**, because the vocabulary map kept
`enrichmentFlags.ts` in `EnrichmentGate.tsx`'s static closure — a refactor that
had moved the map into `config/` would have narrowed that list to one file and
required editing a gate assertion to record a real, correct narrowing. Worth
noting because the tempting version of this change does exactly that.

## Incomplete integration points (mine, explicitly)

- **G1 — the required unit suite is not selected by the task's own validation
  command.** `npm run test -- src/hooks/useFeatureFlag` runs the 15 gate tests;
  the 29 unit tests need `npm run test -- src/hooks/__tests__/useFeatureFlag`
  (or the full suite, which runs both). The gate asserts the unit suite exists at
  the required path with its four named cases, but that is a pointer, not
  evidence. **Owner: whoever writes the next frontend task's validation block.**
  A filter that reaches a suite under `__tests__/` is
  `npm run test -- <area>/__tests__/<file-prefix>`; a bare area works for
  directories (`src/features/admin`) and a bare file stem works for co-located
  specs (`useFeatureFlag` selects both files here).
- **G2 — three keys are bootstrap-only and `/me` cannot influence them.**
  `analytics`, `diagnostics` and `experimentalFeatures` have no `MeFeatureFlags`
  field, so a per-tenant or per-user rollout of those three is not expressible
  today; they move per bundle. Adding the field is a backend change plus one
  entry in `ME_FEATURE_FLAG_WIRE_KEYS`, and the resolver then picks it up with no
  other edit. **Owner: the first task that needs a per-tenant rollout of one of
  the three.** Deliberately not invented here: R4 forbids new flag-service
  scope, and `/me`'s DTO is frozen (006).
- **G3 — the hook's `queryFn` is a fallback that production does not use.** With
  `authStore`'s seed, a surface mounting in the running app never issues its own
  `/me` read. The fallback exists for suites (and e2e) that mark a session
  authenticated without running the login path. If that fallback is ever deleted,
  every suite that fakes a session breaks at once — which is the correct
  failure mode, but it means the function is load-bearing for tests and not for
  the product.
- **G4 — no flag is wired to a 036 admin surface other than the local-GPU
  section.** `FlagsPanel` itself is unchanged (036's task owns its logic), and
  `AdminPage` + `LocalGpuPanel` are the two admin call sites. The next flag that
  lands gets a call site, and `useFeatureFlag.gate.test.tsx`'s exhaustive
  call-site assertion will fail until it is added — deliberately, so the set of
  surfaces reading flags cannot grow unnoticed.
- **G5 — `docs/rollout.md` §4 still says "flag service".** §4a now states the
  real contract beside it, but §4's own "Flags fail closed to the
  last-known-good snapshot … the UI marks flagged areas `DegradedState`" sentence
  is 043B's prose and still describes a service and a banner this product does
  not have. **Owner: whoever next touches the rollout section.** The two
  statements are not in conflict — this implementation does both halves of
  fail-closed — but the prose should say so in one place rather than two.
- **G6 — no `useFeatureFlag` call site in the workspace itself.** `analytics`,
  `diagnostics` and `experimentalFeatures` now have a working evaluator and no
  consumer. That is intentional (the task is the evaluator), but it means a
  reader should not assume the three switches do something today.

## Environment

- **The frontend suite needs four env vars** or 32 tests fail on
  `VITE_API_BASE_URL: Required`:
  ```bash
  export VITE_API_BASE_URL=http://localhost:5000 VITE_CDN_ORIGIN=http://localhost:5173 \
         VITE_ENVIRONMENT=local VITE_APP_VERSION=0.1.0-dev
  ```
- **`vi.stubEnv` + `resetEnvCache()` + `resetDeployConfigCache()`** is how a test
  moves bootstrap config. `src/hooks/__tests__/useFeatureFlag.test.tsx` uses it;
  `src/hooks/__tests__/useVersionWatch.test.tsx` is the precedent. Note the
  `afterEach` must un-stub *and* reset both caches, or the next test inherits the
  previous test's config.
- **`frontend/test-results/` and `frontend/coverage/` are gitignored** and were
  deleted after the e2e baseline run so a stray failure artifact is not left
  behind.
- **The frontend e2e starts its own vite dev server** (`frontend/playwright.config.ts`,
  `webServer.port 5173`, `reuseExistingServer` outside CI). Chromium is
  installed at `~/.cache/ms-playwright`. Running the *whole* frontend e2e suite
  exceeds 30 minutes here; the affected specs (`optional-enrichment`,
  `admin`, `auth`, `shell`) run in about two minutes.

## Recommendations for Next Agent (049)

### Repo state

- `main` carries … → 045 → 046 → 047 → **this task**. `HEAD` before it was
  `aacc2a3` ("feat(ops): make the backup job, the policy and the release gate
  real (Task 047)").
- **Four new files**, everything else modified in place:
  - `frontend/src/config/featureFlags.ts`
  - `frontend/src/hooks/useFeatureFlag.ts`
  - `frontend/src/hooks/__tests__/useFeatureFlag.test.tsx`
  - `frontend/src/hooks/useFeatureFlag.gate.test.tsx`
- **Green:** `npm run typecheck --prefix frontend`, `npm run lint --prefix
  frontend` (0 warnings), frontend suite **1835/1835** in 153 files,
  `check:frontend` 62, `check-no-hardcoded-copy` (939 baselined, 0 new),
  `check-frontend-topology` (297 files), `check:rollout`,
  `check:unit-containers`, `check:vite-env`, `test:presence`, `typecheck:e2e`,
  and `npx playwright test --grep="@optional-enrichment"` 8/8.
- **Red, pre-existing:** `tools/npm-audit-gate.test.mjs` (1);
  `frontend/e2e/admin.spec.ts` (3, UI-login timeouts — proven pre-existing by a
  stashed run); `check:backup-policy` (`GAP_BLOCKS_RELEASE`, 047's D1a/F1);
  `kustomize build staging + prod` (043A Finding 6).

### Names worth knowing

- `useFeatureFlag(key: FeatureFlagKey): boolean` — `frontend/src/hooks/useFeatureFlag.ts`.
  Companions: `useFeatureFlags()`, `useFeatureFlagSnapshot()`,
  `useFeatureFlagsQuery()`, `FeatureFlagProvider`, `FEATURE_FLAG_STALE_TIME_MS`
  (5 min). All re-exported from `frontend/src/hooks/index.ts`.
- `FEATURE_FLAG_KEYS` / `FeatureFlagKey` / `FeatureFlagValue` /
  `FEATURE_FLAG_DEFAULTS` / `ME_FEATURE_FLAG_WIRE_KEYS` / `MeFeatureFlagSlice` /
  `EMPTY_ME_FEATURE_FLAG_SLICE` / `FeatureFlagSourceName` /
  `ResolvedFeatureFlag` / `meFeatureFlagWireKeys()` / `readMeFeatureFlagSlice()` /
  `resolveFeatureFlag()` / `resolveFeatureFlagDetailed()` / `resolveFeatureFlags()`
  / `readBootstrapFeatureFlags()` / `evaluateFeatureFlag()` /
  `evaluateFeatureFlags()` — all `frontend/src/config/featureFlags.ts`.
- `queryKeys.me.featureFlags()` → `['me','feature-flags']`.
  **`queryKeys.enrichment.flags()` is gone** (use `me.featureFlags()`); the
  cache value is the **wire-shaped slice**, not a client-shaped map.
- `ENRICHMENT_FLAG_NAMES`, `EnrichmentFlagName`,
  `ENRICHMENT_FLAG_KEY_BY_NAME` — `frontend/src/features/enrichment/enrichmentFlags.ts`
  (44 lines, vocabulary only).
- Gate internals (for writing a new rule):
  `frontend/src/hooks/useFeatureFlag.gate.test.tsx` exports
  `stripComments`, `flagReadRules()`, `findFlagReadOffences(files, rules)`,
  `productionSources()`; the rule ids are `WIRE_KEY_UNDECLARED`,
  `BOOTSTRAP_FLAG_UNDECLARED`, `ME_REQUEST_UNDECLARED`,
  `ME_DOCUMENT_UNDECLARED`. Rules are declared **with their allowlist and the
  reason for each entry**, and both are asserted, so adding a rule means editing
  that assertion too — on purpose.

### If you add a flag (there are four places, and the gate will tell you)

1. `FEATURE_FLAG_KEYS` **and** `FEATURE_FLAG_DEFAULTS` in
   `config/featureFlags.ts` (the map is typed `Record<FeatureFlagKey, boolean>`,
   so a missing default is a typecheck error).
2. `ME_FEATURE_FLAG_WIRE_KEYS[key]` — the exact `MeFeatureFlags` field name, or
   `[]` for a bootstrap-only key. **Do not invent a wire name**: the parser
   drops anything not in this table, and 044 recorded why.
3. `BOOTSTRAP_READERS[key]` — a `(config: DeployConfig) => boolean` if the flag
   has a `VITE_ENABLE_*` source. A new bootstrap flag means a new variable, and
   that is a deploy change: `DEPLOY_CONFIG_ALLOWLIST` in `config/env.ts`, the
   generated `frontend/.env.example`, `deploy/config-inject.sh`, and
   `deploy/k8s/frontend/configmap.yaml` (`npm run check:vite-env
   --allowlist-sync` cross-checks the copies). Prefer a `/me` key if the
   rollout needs to be per tenant.
4. The call site. Then `useFeatureFlag.gate.test.tsx`'s "the complete set of
   call sites is the three this task wired" fails until the file is added to that
   list — which is the intended friction.

### Gotchas that cost time

1. **Vitest positional filters are path patterns, not substrings.** Verified
   here: `src/hooks/useFeatureFlag` does not select
   `src/hooks/__tests__/useFeatureFlag.test.tsx`; `src/hooks/useFeatureFlag.test`
   selects nothing and **exits 1**. Write the validation command with the filter
   that reaches your spec, and check what it actually selected.
2. **Adding a query key means removing the old one.** `queryKeys.enrichment.flags()`
   and `queryKeys.me.featureFlags()` were one cache entry with two names for one
   document; two names are one too many, and the second one was only ever read by
   the module that owned the first.
3. **`vi.stubEnv` alone does nothing** — `resetEnvCache()` (lib) **and**
   `resetDeployConfigCache()` (config) must follow, in `beforeEach` and again in
   `afterEach`, or the next test inherits the stub.
4. **A `waitFor` on a request counter is not a wait for the answer.** Wait on the
   value that could only come from the response.
5. **A cache mutation test must assert the entry exists** before mutating it, or
   it passes on nothing to mutate (this was in 044's suite).
6. **`useQuery` + `enabled: false` still calls `useQuery`**; the provider case
   therefore needed `enabled` parameterisation, not a conditional hook call
   (which would have violated `react-hooks/rules-of-hooks`).
7. **New UI copy fails `scripts/check-no-hardcoded-copy.mjs`.** Any string in
   `src/**` outside `src/i18n/locales/` is a new finding. Budget for three
   locale files plus the pseudo bundle (derived from `en`, so nothing to do).
8. **`check:backup-policy` is red by design** (`GAP_BLOCKS_RELEASE`). Do not
   soften it; 047's Decisions Made #1 explains why.
9. **The frontend e2e `admin.spec.ts` fails on `main`** (3 tests, UI login
   timeouts). If you touch the login path, that is the first spec to tell you
   whether you fixed it or broke it.
10. **`.NET` SDK is not on `PATH`** here and `/tmp/opencode` is wiped on a server
    restart; `bash dotnet-install.sh --channel 10.0 --install-dir
    /tmp/opencode/dotnet10 --no-path` (note `10`, not `10.0`) reinstalls it.
