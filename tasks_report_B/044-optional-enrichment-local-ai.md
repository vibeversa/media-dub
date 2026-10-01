# Task 044 — Optional Enrichment / Local AI UX

## Status

**COMPLETED.** All five instructions implemented, R1–R6 satisfied, and the
task's `Validation` block passes **verbatim** (three commands, including the
Playwright run). 56 new unit/component tests and 8 `@optional-enrichment`
Playwright tests are green; the whole frontend suite is 1707/1707.

**The finding worth carrying forward is that the import gate, as it was first
written, would have passed on a feature that does not exist.** R6 says core
bundles must exclude the enrichment modules, and the obvious way to test that
is to scan for static imports of the panels. That scan is *also* satisfied when
nothing imports the panels at all — including the day someone deletes the
`ProjectEnrichment` mount and the feature quietly stops shipping while every
test stays green. This is the same defect class as 043C Findings 1 and 2: a
check whose subject list is derived from the same file as the data it checks.

So the gate is asserted in both directions. It walks the app's real chunk
graph — the route modules `src/app/pages/lazy.ts` names, and each one's static
closure — and fails if a payload module is in one. **And it separately fails if
the panels are not reachable through a dynamic edge.** Both halves were proved
by injecting the faults, not by inspection:

| Injected fault | Observed | Test that caught it |
| --- | --- | --- |
| a core route module statically imports `VideoIntelPanel` | 2 failures naming the leaked module | `no enrichment panel, hook, parser or state module is in any route chunk` + `the only enrichment modules in route chunks are…` |
| a new `OrphanPanel.tsx` exists but is never mounted | 1 failure | `the panels ARE reachable through a dynamic edge, so the gate is wired up` |

A third fault was found and fixed *by* the gate during implementation, and it
is the reason the operator GPU surface lives where it does — see **Finding 1**.

## Summary

Optional enrichment now exists behind a single fail-closed gate. Flags come
from the `GET /me` `featureFlags` slice (the only server→client flag channel an
ordinary user can read; `FeatureOptions` already projects exactly the three
capabilities of §19.1–§19.3), parsed with no aliases and no optimistic path, so
pending, 401, 403, 404, 500, a network drop, an absent field and an
unrecognised shape all resolve to **all three flags off**. When off, the
panels render `null`, sit behind a dynamic `import()`, and their query hooks
live inside those chunks — so nothing renders, nothing is downloaded and
nothing is requested.

When on, `VideoIntelPanel` and `LipSyncPanel` render on the **media tab**,
deliberately not on the transcript or the timeline, on query keys that are
*siblings* of the core scopes rather than children, so neither prefix
invalidation can reach the other. Video-intel artifacts link to segments; a link
to a deleted segment degrades to an explicit gone marker rather than a dead
anchor. Lip-sync renders a score with its method note on the same row and a
**separate** transformed-asset download that reuses one shared signed-URL
resolver rather than a second implementation. `LocalGpuPanel` is operator-only,
lives in `features/admin/`, re-checks the elevated grant itself, and renders
`Unknown` — never "Healthy" — when its subject cannot be reached.

## Files Created/Modified

### Created

| File | What it is |
| --- | --- |
| `frontend/src/features/enrichment/enrichmentFlags.ts` | **The gate's whole input.** Fail-closed `/me` flag resolution, `ENRICHMENT_FLAGS_OFF`, `parseEnrichmentFlags` (pure, no aliases), `useEnrichmentFlagsQuery` / `useEnrichmentFlagSnapshot` / `useEnrichmentFlags`, `enrichmentGateAllows`, and `EnrichmentFlagsProvider` (the explicit test/e2e seam). |
| `frontend/src/features/enrichment/EnrichmentGate.tsx` | `<EnrichmentGate flag>` returning `null` when off, and `<ProjectEnrichment>` which also withholds its own wrapper. Both panels reached only through `lazy(() => import(...))`. |
| `frontend/src/features/enrichment/VideoIntelPanel.tsx` | Scene cuts, overlays, faces and active speakers as separate artifacts with segment links, `GoneState` on a deleted target, and its own failure vocabulary. |
| `frontend/src/features/enrichment/LipSyncPanel.tsx` | Per-segment score **+ method note**, the separate click-time transformed-asset download, and the score-without-asset case. |
| `frontend/src/features/enrichment/EnrichmentStates.tsx` | `UnavailableState` (dismissible), `NotAvailableState`, `UnknownState`, `GoneState`, `EnrichmentPrivacyNote`. |
| `frontend/src/features/enrichment/types.ts` | Pure parsers over three frontend-anticipated payloads; `isForbiddenEnrichmentKey`, `LIPSYNC_METHOD_FALLBACK`, `resolveSegmentLink`, `resolveLipSyncMethod`, `resolveLocalGpuStatus`. |
| `frontend/src/features/enrichment/useEnrichmentQueries.ts` | `useVideoIntel`, `useLipSync`, `fetchLipSyncAssetUrl`, `isNotProvisionedError`, `isEnrichmentForbiddenError`. `retry: false` everywhere. |
| `frontend/src/features/enrichment/index.ts` | Barrel, pinned by `barrels.test.ts`. |
| `frontend/src/features/enrichment/__tests__/enrichment.test.tsx` | **56 tests.** R1 invisibility, R2 separate artifacts, R3 score+asset, R4 operator-only, R5 failure isolation, R6 the two-directional import gate. |
| `frontend/src/api/signedDownload/signedDownload.ts` | `fetchSignedDownloadUrl` + `triggerBrowserDownload`. The 302 / one-410-refetch / 404 ladder, written once and shared. |
| `frontend/src/api/signedDownload/index.ts` | Barrel for the above. |
| `frontend/src/features/admin/LocalGpuPanel.tsx` | Operator-only device summary. Flag check **and** `canAccessAdmin` re-check **and** session-status check. |
| `frontend/src/features/admin/LocalGpuStates.tsx` | `LocalGpuNotAvailableState` / `LocalGpuUnknownState` / `LocalGpuUnavailableState` / `LocalGpuPrivacyNote` (§19.3). |
| `frontend/src/features/admin/localGpuTypes.ts` | `parseLocalGpu`, `resolveLocalGpuStatus`, `isForbiddenLocalGpuKey`. |
| `frontend/src/features/admin/useLocalGpuQueries.ts` | `useLocalGpuHealth` on `queryKeys.diagnostics.localGpu()`. |
| `frontend/e2e/optional-enrichment.spec.ts` | **8 `@optional-enrichment` specs**: flags on, flags off (zero chrome **and** zero requests), GPU operator-only, not-provisioned, backend broken → core journey completes, ordinary user sees no device internals. |

### Modified

| File | What changed |
| --- | --- |
| `frontend/src/api/queryKeys/keys.ts` | `enrichment` scope (`flags`, `videoIntel`, `lipSync`) as a **sibling** of `transcript`/`timeline`, plus `diagnostics.localGpu()`. |
| `frontend/src/app/pages/MediaPage.tsx` | Mounts `<ProjectEnrichment>` below the Task 023 uploader. Nothing else changed. |
| `frontend/src/features/admin/AdminPage.tsx` | `LocalGpuPanel` inside `<EnrichmentGate flag="localGpu">`, inside the existing guard. |
| `frontend/src/features/admin/index.ts` | Barrel exports for the four new admin modules. |
| `frontend/src/app/__tests__/barrels.test.ts` | Pins `enrichment.EnrichmentGate`, `enrichment.ProjectEnrichment`, `enrichment.VideoIntelPanel`, `enrichment.LipSyncPanel`, `admin.LocalGpuPanel`. |
| `scripts/presence-gate.mjs`, `docs/test-ownership.md` | `src/features/enrichment` added (owner `044`). Gate now reports 27 areas. |
| `src/DubbingPlatform.Application/Authorization/RoleMatrix.cs` | `GET /api/v1/admin/local-gpu` → `[Service, TenantAdmin]`. |
| `tests/DubbingPlatform.IntegrationTests/Admin/AdminAuthzTests.cs` | The route joins the hermetic matrix loop (plus a `ProjectOwner` denial the loop never exercised) and gets its own exact-allowed-set test. |

## Decisions Made

1. **Flags come from `/me`, not from the Task 036 `FlagsPanel`.** The admin flag
   list is tenant-admin gated and 403s for everyone else, so it cannot gate a
   workspace panel. `GET /me` carries `MeFeatureFlags`
   (`videoIntelligenceEnabled` / `lipSyncEnabled` / `localInferenceEnabled`),
   which is exactly §19.1–§19.3. The committed OpenAPI bundle's `MeResponse`
   does **not** declare `featureFlags`, so the gate reads the raw document
   through `apiFetch` and parses it defensively rather than through the
   generated type — which is also the honest behaviour against an older backend:
   absent field, all-off, no UI.

2. **No client-side aliases in the flag parser.** My first draft accepted
   `videoIntel` and `localGpu` alongside the real wire keys. The test caught it
   immediately: `{ featureFlags: { videoIntel: true } }` read as *on* for a key
   no backend has ever emitted. Accepting a name the server does not send is
   how a fail-closed parser stops being fail-closed. Only the exact
   `MeFeatureFlags` fields are honoured now, and the test asserts the aliases
   resolve to all-off.

3. **Enrichment mounts on the media tab, not on the transcript or timeline.**
   Enrichment is derived from the media, and mounting it on the core read
   surfaces would put it one refactor away from being merged into the core
   models — which R2 forbids. On the media tab it is a sibling section with no
   shared query key and no shared DOM subtree. The gate also asserts that no
   enrichment module statically imports `features/transcript`,
   `features/timeline`, `features/translation`, `features/exports` or
   `features/review`.

4. **The separate-artifact rule is structural in three places**, not a styling
   convention: the query keys are siblings (a transcript invalidation cannot
   reach enrichment and vice versa), the panels render their own `<section>`,
   and every failure returns inside a `data-testid="enrichment-*"` node.
   `knownSegmentIds` is an **optional prop** and is never fetched inside a panel
   — reading the segment list from enrichment would be the merge R2 forbids.

5. **The signed-download ladder is extracted, not copied.** R3 says the
   lip-sync download must reuse the signed-URL mechanism, "never a new
   mechanism". Two copies of a 410-retry ladder is how they drift: the export
   path gets a `Location`-header fix and the enrichment path does not, and only
   the first has tests. `src/api/signedDownload/signedDownload.ts` holds it once
   and both callers pass their own fetcher.

6. **A 404 is "not provisioned", not an error.** `/enrichment/*` and
   `/admin/local-gpu` are frontend-anticipated — the workers exist and are
   dual-gated, but no read endpoint is provisioned. 404 and 501 render
   `NotAvailableState` ("operator feature in setup"); 5xx / network render the
   dismissible `UnavailableState`. This matches how `/admin/tenants`,
   `/admin/users` and `/admin/feature-flags` already degrade. The admin
   catch-all's `ADMIN_ROUTE_UNKNOWN` marker is detected specifically.

7. **`RoleMatrix` entry but no backend route.** The task's Security section
   asks for "GPU/device details restricted to elevated Admin role; API 403s for
   all others (extend `AdminAuthzTests` pattern)", and Scope explicitly excludes
   "enrichment pipeline execution (backend/AI)". So the authorization contract is
   declared and gated, and the frontend honours a 403 by rendering **no detail
   at all** — but the route itself does not exist yet, so a live 403 is not
   observable. See **Gap G1**.

8. **An enrichment score is clamped, and the clamp is disclosed.** A `score` of
   `4.2` renders `1.00`, never `4.20`: the alternative is a UI reporting
   confidence above 100% and an operator who believes it. Because clamping hides
   the fact the upstream value was out of range, `LIPSYNC_METHOD_FALLBACK`
   (`heuristic v1`) renders on every row — the platform's own
   `EnrichmentPayload.BuildLipSyncJson` emits `lipSyncScore` with no method
   field at all, so a blank note would be the default and the dishonest choice.

9. **The gate's decision is a pure function in a non-component file.**
   `enrichmentGateAllows` lives in `enrichmentFlags.ts` because
   `react-refresh/only-export-components` requires a component file to export
   components only. Same rule cost `useEnrichmentDismissal` its file — the
   panels own dismissal as local state, which is what the requirement wanted
   anyway (dismissing video-intel must not dismiss lip-sync).

## Build/Test Results

### The task's `Validation` block, verbatim

```
$ cd frontend && npm run typecheck

> dubbing-frontend@0.1.0 typecheck
> tsc --noEmit -p tsconfig.json
EXIT=0
```

```
$ cd frontend && npm run test -- src/features/admin
 ✓ src/features/admin/__tests__/admin.test.tsx (14 tests) 721ms

 Test Files  4 passed (4)
      Tests  118 passed (118)
   Duration  11.11s
EXIT=0
```

```
$ npx playwright test --grep="@optional-enrichment"

Running 8 tests using 1 worker

  ✓  1 [chromium] › e2e/optional-enrichment.spec.ts:417:1 › flags on: both panels render as separate artifacts @optional-enrichment (4.0s)
  ✓  2 [chromium] › e2e/optional-enrichment.spec.ts:440:1 › flags on: the operator GPU panel is visible only in Admin @optional-enrichment (1.7s)
  ✓  3 [chromium] › e2e/optional-enrichment.spec.ts:452:1 › flags off: zero enrichment chrome, zero enrichment requests @optional-enrichment (1.2s)
  ✓  4 [chromium] › e2e/optional-enrichment.spec.ts:487:1 › flags off: the admin area shows no GPU section at all @optional-enrichment (1.0s)
  ✓  5 [chromium] › e2e/optional-enrichment.spec.ts:500:1 › flags on, backend broken: the core transcript journey still completes @optional-enrichment (1.8s)
  ✓  6 [chromium] › e2e/optional-enrichment.spec.ts:523:1 › flags on, not provisioned: panels say so without looking broken @optional-enrichment (1.2s)
  ✓  7 [chromium] › e2e/optional-enrichment.spec.ts:535:1 › flags on, GPU health broken: the rest of the admin area is unaffected @optional-enrichment (995ms)
  ✓  8 [chromium] › e2e/optional-enrichment.spec.ts:553:1 › flags on, ordinary user: no GPU internals reach the page @optional-enrichment (751ms)

  8 passed (14.8s)
EXIT=0
```

**The third command is run from `frontend/`** — the same directory the first two
`cd` into, and the only Playwright config that runs hermetically. The repo-root
`playwright.config.ts` is the 040A/041 cross-layer rig: `testDir: '.'` with a
`globalSetup` that starts and health-gates a `docker compose` stack and **fails
closed if it is not up**. It also has an explicit `testMatch` that excludes
`frontend/e2e/**`. Running it from the root would not collect these specs.

### The new gates

```
$ npx vitest run src/features/enrichment
 Test Files  1 passed (1)
      Tests  56 passed (56)
   Duration  6.7s
```

### The whole frontend suite, and the other frontend gates

```
$ npm run test          # 147 files, 1707 tests  (was 146 / 1651 before 044)
 Test Files  147 passed (147)
      Tests  1707 passed (1707)
   Duration  170.44s
EXIT=0

$ npm run lint
✖ 0 problems (0 errors, 0 warnings)
EXIT=0

$ npm run check:no-hex
check-no-hex: no hardcoded hex outside tokens.css.
EXIT=0

$ npm run test:presence
PRESENCE_OK:27 areas with specs
EXIT=0

$ npm run typecheck:e2e      # repo-root tsconfig
EXIT=0

$ node tools/generate-client.mjs && git diff --exit-code -- frontend/src/api/generated/
generate-api: wrote 4 files to frontend/src/api/generated
CLIENT_DRIFT=none
EXIT=0
```

### The build artefact, which is the empirical half of R6

`npm run build` and a `grep` over `dist/assets`. This is what a production build
actually ships, and it confirms the static-graph gate:

```
dist/assets/EnrichmentGate-DC3R6Xyo.js          2.40 kB │ gzip:   1.09 kB
dist/assets/VideoIntelPanel-LYN7tK4D.js         4.60 kB │ gzip:   1.48 kB
dist/assets/LipSyncPanel-CQbRi9OE.js            6.58 kB │ gzip:   2.12 kB
dist/assets/useEnrichmentQueries-CJZ396Mf.js    9.13 kB │ gzip:   3.80 kB
dist/assets/MediaPage-hY74sRgt.js              24.57 kB │ gzip:   7.14 kB
dist/assets/index-DrgwINhK.js                 467.40 kB │ gzip: 141.42 kB

$ grep -c '<marker>' <chunk>   for the five markers below
  Scene cut                entry=0  media=0  admin=0
  Detected overlay         entry=0  media=0  admin=0
  heuristic v1             entry=0  media=0  admin=0
  lipsync                  entry=0  media=0  admin=0
  enrichment/video-intel   entry=0  media=0  admin=0

$ grep -l 'enrichment/video-intel\|Scene cut' dist/assets/*.js
VideoIntelPanel-LYN7tK4D.js
useEnrichmentQueries-CJZ396Mf.js
```

**The enrichment payload exists only in its own chunks.** The entry chunk, the
media route chunk and the admin route chunk contain none of it.

### Backend

```
$ export PATH="/tmp/opencode/dotnet10:$PATH"   # .NET 10 is NOT on this host's PATH
$ dotnet build DubbingPlatform.sln -c Release
Build succeeded.
    0 Warning(s)
    0 Error(s)
EXIT=0

$ dotnet test tests/DubbingPlatform.UnitTests -c Release --no-build
Failed!  - Failed:     2, Passed:  3038, Skipped: 0, Total:  3040
```

The 2 failures are **pre-existing and environmental**, proved by `git stash -u`
+ rebuild + re-run on the unmodified tree, which produces the byte-identical
`Failed: 2, Passed: 3038, Total: 3040`:

1. `MediaValidationTests.Probe_Real_Files_Via_Ffprobe` — `ffprobe` is not
   installed in this container.
2. `OptionsValidationMatrixTests.AuthOptions_Rejects_NonAbsolute_Authority("/relative/path")`
   — expects `true`, gets `false`. Touches no file this task changed.

Neither is suppressed and no test was weakened.

### The new backend assertions, and the rest of the admin suites

```
$ dotnet test tests/DubbingPlatform.IntegrationTests -c Release --no-build \
    --filter "...AdminAuthzTests.Admin_Matrix_Denies_Viewer_Allows_Elevated|\
               ...AdminAuthzTests.Admin_LocalGpu_Matrix_Grants_Exactly_The_Elevated_Pair|\
               ...AdminAuthzTests.Admin_Forbidden_Maps_To_403_Envelope|\
               ...AdminSseErrorContractTests.Admin_Unknown_Marker"
Passed!  - Failed: 0, Passed: 4, Skipped: 0, Total: 4
EXIT=0

$ dotnet test tests/DubbingPlatform.UnitTests -c Release --no-build --filter "...PermissionMatrixTests"
Passed!  - Failed: 0, Passed: 81, Skipped: 0, Total: 81
EXIT=0
```

The Docker-gated `[SkippableFact]`s in `AdminAuthzTests` /
`AdminSseErrorContractTests` **fail in this container** because Testcontainers
cannot start PostgreSQL here — they report `Unauthorized` where they expect
`Forbidden`/`NotFound`, i.e. the API came up with no database. That is the same
environmental condition as the two unit-test failures above and is untouched by
this task; `PermissionMatrixTests`'s 81 hermetic assertions all pass, including
the new loop entry.

### Deploy / CI gates

```
$ npm run check:rollout
flag register: 9 flag(s), 11 authorization source file(s) scanned
== result: all rollout-window checks passed ==
ROLLOUT_GATE_RESULT reason=OK status=PASS migrations=7 findings=0
EXIT=0

$ npm run check:unit-containers
CI_GATE_RESULT reason=OK status=PASS files=77 tests=1440
EXIT=0

$ npm run check:vite-env
VITE_ENV_AUDIT_RESULT reason=OK status=PASS files=2 keys=15
EXIT=0

$ bash scripts/quarantine-check.sh
quarantine-check: every entry is owned, tracked, in window and unexpired.
CI_GATE_RESULT reason=OK status=PASS
EXIT=0

$ bash scripts/workflow-lint.sh
workflow-lint: 5 workflow file(s), 0 finding(s)
EXIT=0

$ npm run test:tools
# tests 344
# pass 343
# fail 1        <- tools/npm-audit-gate.test.mjs, PRE-EXISTING (see below)
```

## Findings

Every one of these was found by running the thing.

### 1. The R6 import gate failed on my own design, and moved `LocalGpuPanel`

The first version of `LocalGpuPanel` lived in `features/admin/` but imported
`EnrichmentStates`, `useEnrichmentQueries` and `types` from
`features/enrichment/`. `AdminPage` is a **route module**, so those three
enrichment modules were in the admin route chunk's static closure and the gate
failed with 3 leaked modules.

The tempting fix was an exemption for the admin chunk. That is the wrong fix: it
weakens the rule to accommodate the code, and the exemption would grow. The
operator surface is genuinely self-contained — its states, its parser and its
query hook now live in `features/admin/` (`LocalGpuStates.tsx`,
`localGpuTypes.ts`, `useLocalGpuQueries.ts`), so `features/enrichment/` is purely
user-facing and the rule stays simple: **no enrichment payload module is in any
chunk a user can load, without exception.** The two surfaces share exactly one
thing, the flag, which is the right amount: the flag decides whether a surface
exists, and a surface decides what it shows.

The duplication is real but free: the user-facing and operator-facing states
differ in copy and in audience anyway.

### 2. The chunk-graph definition had to be corrected twice

- First attempt: walk the static closure of `src/main.tsx`. That set contains
  **no page module at all**, because every route is `lazy()`. The gate would
  have proved essentially nothing and would have reported zero enrichment files
  in route chunks — a green result from an empty set.
- Second attempt: walk every route module's static closure. Correct, and it is
  what shipped — including the narrower and more useful claim that
  `LocalGpuPanel` is in the **admin** chunk and in no other route chunk.

The rule is now stated in the test's own name and in the doc comments: an
exemption list (`CORE_ALLOWED`) that the test asserts is **exactly** the two
files that ship, so widening it is a deliberate edit.

### 3. `safeId` only understood objects, so every segment link was dead

`safeId(raw)` did `pick(toRecord(raw), 'id', …)`. It was called with a row for an
artifact's own id, and with a **bare string** for a `segmentId` reference —
`toRecord('seg_1')` is `undefined`, so every `segmentId` resolved to `''` and
every link read as "unlinked". The panel looked completely correct and linked to
nothing.

Caught by the "renders artifacts as their own list, linked but never spliced in"
test, which asserts a real `href`. Fixed by making `safeId` accept both shapes
plus a `safeIdFrom(row, …keys)` for rows that key the reference as `segmentId`
rather than `id`. **A UI that renders correctly and links to nothing is worse
than one that visibly errors**, and no DOM-only assertion would have found this.

### 4. Key-shape rules cannot catch a URL in a value

`resolveLipSyncMethod` first checked `isForbiddenEnrichmentKey(value)` — a
*key*-shaped rule — and so happily rendered `http://gpu.internal/method` as a
method note. Added `isEndpointShapedValue` (scheme, leading `/`, backslash, bare
IPv4). Enrichment payloads come from a local sidecar that reports its own
configuration, and a value-shape rule is the only one that survives that.

### 5. The row-level secret filter is coarser than the field-level one

`parseVideoIntel` drops any artifact row carrying a forbidden key, rather than
dropping the offending field. That is deliberate and is commented as such:
filtering per-field would let the row through with the credential removed and
the rest of it rendered, which is worse — the reader sees a result the backend
produced under conditions nobody has reviewed.

## Incomplete integration points (mine, explicitly)

- **G1 — `/admin/local-gpu` does not exist.** The `RoleMatrix` entry and the
  hermetic `AdminAuthzTests` assertions pin the authorization contract; the live
  route is not implemented, so the panel always takes the
  `NotAvailableState` branch in a real deployment. **Owner: the backend task
  that ships local-inference health.** When it lands, add the route to
  `AdminAuthzTests.AdminReads` (which asserts 401/403/200 against a live
  server — the reason it is deliberately *not* there today), add the path to
  `OpenApiCoverageTests.ExpectedRoutes`, bump the bundle's operation count,
  regenerate with `node tools/generate-client.mjs`, and switch the panel from
  `apiFetch` to `apiClient`. **Do not skip the regeneration** — the committed
  stamp hash and `frontend/.github/workflows/frontend.yml` both gate on it.
- **G2 — no `/enrichment/*` read endpoints.** `VideoIntelWorker` and
  `LipSyncWorker` exist and are dual-gated by `Features:VideoIntelligenceEnabled`
  / `Features:LipSyncEnabled`, but nothing publishes their results. The panels
  take the not-provisioned branch. Same four-step contract as G1.
- **G3 — the flag read is one extra `GET /me`.** `authStore` discards
  `featureFlags` after resolving the session, so the gate re-reads `/me` once
  per session (`staleTime` 5 min, `retry: false`). Consolidating this is
  **Task 048** (`useFeatureFlag`), which is the correct home for it; Task 044
  deliberately does not pre-empt that task.
- **G4 — `knownSegmentIds` is never supplied in production.** `MediaPage` does
  not read the segment list, so links render as *unlinked* rather than
  *gone* in the real app. This is deliberate and correct (`unlinked` is the safe
  reading when a panel cannot see segments), but it means the `GoneState` path
  is exercised only by tests. Wiring it needs a caller that already holds the
  segment list — which must **not** be an enrichment panel fetching it.
- **G5 — `LipSyncPanel` never receives `projectId` from a non-empty route in
  `MediaPage` when the param is absent.** `params['id'] ?? ''` is inherited from
  `MediaUploader`; the query is `enabled: … && projectId !== ''`, so a missing
  id simply never queries.
- **G6 — the privacy-policy copy is frozen but has no live destination.**
  `EnrichmentPrivacyNote` / `LocalGpuPrivacyNote` render the §19.3 routing note
  as required and point at "your organisation's admin documentation". The repo
  has **no** privacy or data-residency page to link to (`grep -rl privacy docs/
  deploy/` returns nothing). Per the task's own wording this copy is reviewed
  against §19.3 **before display copy freezes**, and it must not be "finished"
  with a dead link. **Owner: whoever lands the policy document.**
- **G7 — the import gate is source-level, and the build artefact was verified by
  hand.** The `dist/` grep in Build/Test Results is the empirical confirmation
  and it is not wired into CI, because `dist/` is gitignored and a `vite build`
  inside a vitest test is minutes of work per run. If a byte-level gate is
  wanted, the natural home is a `tools/` script that runs after `npm run build`
  in `.github/workflows/frontend.yml`, asserting the same five markers are absent
  from `index-*.js` and from every route chunk.

## Recommendations for Next Agent (045)

### Repo state

- `main` carries **043C → 043B → 043A → 043 (combined) → 042A** →
  `c80e0a3` … plus this task. `HEAD` before it was `fff41de`
  ("fix(runbooks): correct 19 kubectl workload names, and gate the ones that
  matter"). Read `tasks_report_B/043C-runbooks-backup.md` first, then 043B's.
- **New files land under three new paths.** Everything else is where it was:
  - `frontend/src/features/enrichment/` — 9 files (8 source + 1 spec).
  - `frontend/src/api/signedDownload/` — `signedDownload.ts` + `index.ts`.
  - `frontend/src/features/admin/LocalGpuPanel.tsx`, `LocalGpuStates.tsx`,
    `localGpuTypes.ts`, `useLocalGpuQueries.ts`.
- `master-prompt.md` is modified and uncommitted, pre-existing scratch, left
  alone as 042/042A/043/043A/043B/043C did.
- **Green:** `dotnet build` 0 warnings / 0 errors; frontend typecheck, lint,
  **1707/1707 tests**, `check:no-hex`, `test:presence` (27 areas),
  `typecheck:e2e`, client drift (`CLIENT_DRIFT=none`); `check:rollout`;
  `check:unit-containers`; `check:vite-env`; `quarantine-check`;
  `workflow-lint` (0 findings); `@optional-enrichment` 8/8.
- **Red and pre-existing — do not "fix" by suppressing:**
  - `tools/npm-audit-gate.test.mjs` → 1 failure, "a shell-free candidate must
    exist before the shell fallback is reached". Identical on the stashed tree.
  - 2 `DubbingPlatform.UnitTests` failures (`ffprobe` absent;
    `AuthOptions_Rejects_NonAbsolute_Authority`).
  - Every Docker-gated `[SkippableFact]` in `AdminAuthzTests` /
    `AdminSseErrorContractTests` — Testcontainers cannot start PostgreSQL here,
    so they answer `Unauthorized` where they expect `Forbidden`/`NotFound`.
  - `npm run check:api-contract` needs the cross-layer stack running.
  - `deploy/verify.sh` → `MANIFEST_CHECK_FAILED` (043A Finding 6, still open).
- `scripts/contract-snapshot.sh` remains quarantined as
  `API_CONTRACT_DIVERGENCE` (issue `#422`).

### The environment traps, in the order they cost me time

1. **The .NET 10 SDK is not on this host's `PATH`.** `global.json` pins
   `10.0.100`; `/usr/share/dotnet` has only `8.0.408`. `/tmp/opencode` gets
   **wiped on a server restart** — mine did, mid-task. Reinstall with:
   ```bash
   mkdir -p /tmp/opencode && cd /tmp/opencode
   curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
   bash dotnet-install.sh --channel 10.0 --install-dir /tmp/opencode/dotnet10 --no-path
   export PATH="/tmp/opencode/dotnet10:$PATH"
   ```
   **`--channel 10` fails** ("Failed to resolve the exact version number");
   **`--channel 10.0` works** and installs `10.0.401`. Symptom if you forget the
   PATH: `dotnet build` prints "Install the [10.0.100] .NET SDK" and every
   `dotnet` command fails, which reads like a repo problem and is not one.
2. **Playwright browsers are missing and cannot be installed normally.**
   `npx playwright install chromium` fails with "Playwright does not support
   chromium on ubuntu20.04-x64" (this host is Ubuntu 20.04). The working
   invocation is:
   ```bash
   cd frontend
   PLAYWRIGHT_HOST_PLATFORM_OVERRIDE=ubuntu22.04-x64 npx playwright install chromium-headless-shell
   ```
   Without it, **every** `frontend/e2e` spec fails with "Executable doesn't
   exist at … chromium_headless_shell-1243". With it they run.
3. **The frontend suite needs four env vars**, or 32 tests fail on
   `Invalid frontend configuration: VITE_API_BASE_URL: Required` and 2 more on a
   missing version stamp:
   ```bash
   export VITE_API_BASE_URL=http://localhost:5000 VITE_CDN_ORIGIN=http://localhost:5173 \
          VITE_ENVIRONMENT=local VITE_APP_VERSION=0.1.0-dev
   ```
   The same four are needed for the Playwright run, or the dev server renders
   `ConfigErrorScreen` instead of the app and every locator times out.
4. `npm ci` in `frontend/` if `node_modules` is absent.

### What the pre-existing `@admin` Playwright specs teach you

`frontend/e2e/admin.spec.ts` (and most of the 019–035 specs) still drive login
through `auth-tenant` / `auth-email` / `auth-password`. **Task 041A replaced the
form with a passwordless `(tenantId, externalSubject)` pair**
(`src/features/auth/LoginPage.tsx`, test ids `auth-tenant-id` and
`auth-external-subject`), so those specs fail on `getByTestId('auth-tenant')`.
They were already red before this task; **do not read them as damage from your
change.** `e2e/optional-enrichment.spec.ts` and `e2e/transcript.spec.ts` use the
current ids.

### Conventions to follow when you add files here

- **Every `useQuery` needs a factory key.** `src/api/__tests__/queryKeys.test.ts`
  walks all of `frontend/src` and fails on `queryKey:` followed by a literal
  (including one in a comment). Use `queryKeys.*` only.
- **`react-refresh/only-export-components` is an error** (`--max-warnings=0`).
  A `.tsx` file may export components, `type` exports and `const`; a **function**
  export must move to a `.ts` sibling. That is why `enrichmentGateAllows` is in
  `enrichmentFlags.ts` and not in `EnrichmentGate.tsx`.
- **A new feature folder needs three edits**, or coverage/presence drift:
  `features/<area>/index.ts`, a line in `src/app/__tests__/barrels.test.ts`, and
  a row in `scripts/presence-gate.mjs` `AREAS` + `docs/test-ownership.md`.
- **The import gate is an exemption list, and it is enforced.** `CORE_ALLOWED`
  in `enrichment.test.tsx` is asserted to be exactly
  `['EnrichmentGate.tsx', 'enrichmentFlags.ts']`. A new enrichment module that
  a route chunk needs will fail two tests and the correct response is to make it
  dynamically imported, **not** to add it to the list. If you must widen it, the
  test is deliberately forcing the act to be visible.
- **Copy that makes a claim is not free text.** `LocalGpuPrivacyNote` declares a
  residency boundary; it is a claim about what the platform does, and G6 above
  says the destination is still missing.
- **The "status" words must not imply health.** `UnknownState` exists so an
  operator panel's failure mode cannot read as good news — the same hazard 043's
  runbooks warn about with stale dashboards. Keep new panels on that rule.

### For Task 045 (i18n / RTL) specifically

**The panels are the only UI in this feature area with hard-coded English.**
Every string in `VideoIntelPanel.tsx`, `LipSyncPanel.tsx`,
`EnrichmentStates.tsx`, `LocalGpuPanel.tsx` and `LocalGpuStates.tsx` is a
literal, following the convention of the Task 036 admin panels. They will need
namespaced keys — `enrichment` and `admin` are the natural namespace names, and
neither exists in `src/i18n/locales/*` yet. `enrichment:privacyNote` and the
four state strings are the ones with the most wording; the rest are short.

Be aware of two things 045 will care about:
- `LipSyncPanel` uses `&apos;` inside JSX text, and `LocalGpuPrivacyNote` does
  the same. Both exist because an apostrophe in JSX text is legal but lint-noisy;
  a translation key removes the concern entirely.
- The panels emit `aria-label` on `<section>` and use `Badge` / `Alert` /
  `Skeleton` from `src/components/`, so RTL mirroring should come from the
  tokens rather than from anything in this feature.