# Task 041A - Cross-Feature Journeys and Full Smoke

## Status

**BLOCKED** - on a second P0 that belongs to Task 023, not 041A. The first P0
(login, which made the product unusable) is **fixed and proven** in this task.

## Summary

Fixed the login contract end-to-end: the committed OpenAPI bundle described an
email+password model the API never implemented, so every sign-in returned 400 and
no user could use the product. Corrected the bundle's `AuthLoginRequest` to the
API's real `{ tenantId, externalSubject }`, regenerated the TypeScript client, and
adapted the login form, its copy and 21 test call sites. Verified in a real
browser against the real rig: the form now signs in, honours `?next=`, and renders
the seeded project from a live API response. Also made the contract gate
severity-aware so it distinguishes a request the server would *reject* from one it
would merely lose information on - which reduced the reported divergence from 24
blunt "differing" bodies to **2 genuinely fatal** ones. Journeys and the section 24
smoke are still not written, because the remaining 2 fatal breaks are both in the
upload path, whose protocol the bundle misrepresents wholesale (see Decisions).

## Files Created/Modified

### Production fix - login contract (the P0)

| File | Change |
| --- | --- |
| `src/DubbingPlatform.Api/OpenApi/openapi.v1.json` | `AuthLoginRequest` is now `{ tenantId (uuid), externalSubject (1..256) }`, required both, matching `LoginRequest`. Operation summary, description and example corrected (they advertised the removed fields and a "tenant credentials" model that does not exist). |
| `frontend/src/api/generated/{schemas.ts,client.ts,OPENAPI_VERSION}` | Regenerated via `node tools/generate-client.mjs`. 3-line net change. |
| `frontend/src/features/auth/LoginPage.tsx` | Form collects `tenantId` + `externalSubject` (`auth-tenant-id`, `auth-external-subject`) instead of tenant slug + email + password. Trims both values before submit. |
| `frontend/src/i18n/locales/en/auth.json` | `tenant`/`email`/`password` keys replaced by `tenantId`/`externalSubject`; `loginError` reworded. Also repaired genuine encoding corruption: `signingIn` and `redirecting` held bytes `D8 B8 C2 80 D8 AE` (a mangled Arabic character pair) in the committed file. |
| `frontend/src/api/errors/normalizeError.ts` | `INVALID_CREDENTIALS` no longer blames the user for an email/password/tenant triple; the platform has no such credential. |

### Tests updated to the new contract (no test deleted or weakened)

| File | Change |
| --- | --- |
| `frontend/src/features/auth/__tests__/authStore.test.ts` | 12 login call sites. The "wrong password" case became an unknown `externalSubject`, which is what actually fails in a passwordless API. |
| `frontend/src/features/auth/__tests__/authMatrix2.test.ts` | 8 login call sites. |
| `frontend/src/api/__tests__/httpClient.test.ts` | 1 call site. |
| `frontend/src/features/auth/__tests__/sessionFlow.test.tsx` | `fillForm` uses the new testids; error-copy assertion updated. |
| `frontend/e2e/auth.spec.ts` | **019's spec.** `loginThroughUi` selectors updated. The API stays mocked, so its coverage is unchanged. |

### Contract gate made trustworthy

| File | Change |
| --- | --- |
| `tools/check-api-contract.mjs` | Added `isUnderDescribed()` and `classify()`. Empties the server's silent bodies into a separate `underDescribed` list, tags each divergence `fatal` or `shape`, and fails only on `fatal` + `undeclared`. |
| `tools/check-api-contract.test.mjs` | 9 -> 13 cases. New coverage for under-described bodies, `isUnderDescribed` boundaries, `classify`, and the fatal/survivable split in the report. |

## Decisions Made

**1. The API is authoritative, so the bundle is what was wrong.** The evidence is
one-sided: there is no password anywhere in the domain, `AuthService.LoginAsync`
resolves `(tenantId, externalSubject)` against `tenant_users` and has no credential
to verify, and the auth integration tests exercise that shape. The bundle's
email+password entry had no implementation behind it. Changing the API to match the
bundle would have meant inventing password auth; correcting the bundle to match the
API costs three lines and makes the product work.

**2. I edited 019's `e2e/auth.spec.ts`, which brushes against R3 ("no feature-spec
authorship").** R3's purpose is that 041A must not claim authorship of the auth
feature's coverage. I did not: the spec still mocks the API, still asserts the same
four behaviours, and I changed only the selectors it fills, because the contract it
drives changed. Leaving it broken would have made the frontend suite red. I am
flagging it explicitly so the boundary is visible rather than assumed.

**3. I did not fix the upload contract, and that is the reason for BLOCKED.** It is
not a field rename. The bundle models a **one-shot** upload -
`UploadInitiateRequest { fileName, contentType, sizeBytes, partCount }` returning
`UploadSession { id, status, partUrls, receivedParts }`. The API implements
**two-phase multipart**: `CreateUploadRequest { fileName, contentType, declaredSize,
clientSha256Hex? }` -> `CreateUploadResponse { uploadId, multipartUploadId, partSize,
expiresAt, status }` -> `GetPartUrlsRequest { partNumbers }` -> per-part presigned
PUTs -> `complete` -> poll `UploadStatusResponse { completedParts, missingParts,
partSize, declaredSize }`. Request, response and mechanism all differ, and the
response field sets are *disjoint* (`id`/`partUrls`/`receivedParts` do not exist on
the wire). `frontend/src/features/uploads/useResumableUpload.ts:551` sends
`{ sizeBytes, partCount }` because it was written against the fiction. Making this
work is a redesign of Task 023's upload engine and its session/resume model, with
its own verification loop - not a contract patch, and not 041A's surface.

**4. No journey or smoke spec was written, and no scaffolding either.** Journeys
require upload, so they would be red. I also did not add an unused
`e2e/support/` layer "ready for" 041A's completion: 046 owns auth/reset/SSE helpers,
and unused scaffolding is a liability that will be written twice. Better to hand
over a two-item repair list than a speculative harness.

**5. The gate now fails only on genuinely fatal divergences.** A bundle that
under-specifies a request loses information; a bundle that requires a field the
server rejects, or whose fields are entirely disjoint, produces a 400 before the
action runs. Only the second justifies blocking a build, and conflating them would
have had the next agent "fix" correct client code in response to the emitter's
silence.

**6. I did not wire the gate into CI.** It still reports 2 fatal breaks (upload), so
gating today would redden every task's suite. Sequence: repair upload, confirm the
gate is clean, then wire it beside `check-api-drift`.

**7. An empty database is not a product defect.** My first login retry returned 500
(`42P01: relation "tenant_users" does not exist`, 0 tables) because I brought the
rig up without running the seeder, which is what applies migrations. Seeding fixed
it. Recorded because the 500 looks alarming and the cause is rig startup.

## Build/Test Results

### The P0 is fixed - real browser, real rig, real API

Before, through the UI:

```
request body the browser sent: {"email":"owner@cross-layer.invalid","password":"CHANGE_ME","tenantSlug":"cross-layer"}
auth/login responses: [{"status":400,"method":"POST"}]
error shown to user: Email, password, or tenant is incorrect. Check them and try again.
```

After:

```
request body sent: {"tenantId":"11111111-1111-1111-1111-111111111111","externalSubject":"cross-layer-owner"}
auth/login statuses: [200]
url after submit: http://127.0.0.1:54173/projects
error shown: (none)

LOGIN SUCCEEDED and ?next= was honoured.
projects page mentions the seeded project: true
```

The last line matters: it is not a redirect into a shell, it is the seeded project
rendered from a live `GET /projects`.

### The gate, before and after this task

```
before:  operations compared 26 | divergent 25 | login FATAL
after:   operations compared 26
         FATAL (a request built from the bundle would be rejected): 2
           POST /api/v1/projects/{projectId}/uploads
           POST /api/v1/projects/{projectId}/uploads/{uploadId}/parts
         divergent but survivable: 4
         server document under-describes (not judged): 18
         declared in the server but absent from the bundle: 5
gate exit: 1
```

The 4 survivable ones are worth naming for whoever finishes the repair:
`PUT /me/preferences` (the bundle uses a *response* schema as the request - a real
bug, not fatal), `POST /projects` (the client cannot send `description`,
`processingSettings` or `settings`), `PATCH /projects/{id}`, and
`POST /projects/{id}/exports`. The 5 undeclared are all
`/api/v1/admin/{unmatched}`, an intentional catch-all in `AdminController.cs:441-447`
that correctly has no client. The 18 under-described are the emitter's silence; 040B
proved several of those exact bodies work, so the bundle is probably right there.

### Tests and gates

```
$ npm run test:tools
tests 13 | pass 13 | fail 0

$ node tools/check-api-drift.mjs
check-api-drift: generated client matches the committed bundle.     DRIFT_EXIT=0

$ npm --prefix frontend run typecheck
> tsc --noEmit -p tsconfig.json                                     (no output)

$ npm --prefix frontend run lint
> eslint . --max-warnings=0                                         LINT_EXIT=0

$ npm --prefix frontend test
 Test Files  143 passed (143)
      Tests  1578 passed (1578)
   Duration  175.62s

$ cd frontend; npx playwright test e2e/auth.spec.ts        # 019's spec, selectors updated
  ok 1 login happy path @auth
  ok 2 expiry redirects to login and returns to the destination @auth
  ok 3 logout clears data and back-button reveals nothing @auth
  ok 4 forbidden page carries a request-access hint @auth
  4 passed (23.6s)

$ npx playwright test --grep="@cross-layer"                # 040A/040B regression
  24 passed (1.2m)
```

### R5 quarantine input - one measured flake, below threshold

`src/app/__tests__/pagesMatrix.test.tsx > top-level routes > renders the review
studio shell` failed in one full run and passed in the next, and passes in
isolation (29/29 in 7.5 s). Two full runs of the identical tree: 1 failure in
3156 test executions (~0.03%), against R5's 2/50 threshold, so it is **not**
quarantined - it is recorded. The full run's 409 s of aggregate jsdom time across 143
files points at resource contention rather than the test. Owner: 018/041 (shell and
routing). When 041A is unblocked, `e2e/journeys/README.md` must carry this table
plus any new entries; there is no `retries` escape hatch - `playwright.config.ts`
keeps `retries: 0`.

## Recommendations for Next Agent (041B)

### The one thing that unblocks 041A, 041B, 041C and 041D

**Task 023 must rebuild the upload client against the real two-phase protocol.** It
is the last fatal divergence, and 041B's 12 screens are all behind authentication,
so 041B is blocked on 023 exactly as 041A was. Concretely:

1. Bundle `UploadInitiateRequest` -> `{ fileName, contentType, declaredSize, clientSha256Hex? }`
   (the API's `CreateUploadRequest`; note `DeclaredSize` is `[Range(1, long.MaxValue)]`,
   not `[Required]`, so it is optional in the schema but the client must send it).
2. Add the parts request as `{ partNumbers: number[] }` (the API's `GetPartUrlsRequest`;
   the bundle has an inline `{ partNumber }` singular, which is simply wrong).
3. Replace the fictional `UploadSession` response with the API's real shapes:
   `CreateUploadResponse { uploadId, multipartUploadId, partSize, expiresAt, status }`,
   `GetPartUrlsResponse { urls }`, and
   `UploadStatusResponse { completedParts, missingParts, partSize, declaredSize, expiresAt }`.
   Note the real flow has **no** `id` and **no** `partUrls` on the create response.
4. Rewrite `useResumableUpload.ts` around create -> get part URLs -> presigned PUT per
   part -> `complete` -> poll status. The current engine is built on the one-shot
   fiction (`useResumableUpload.ts:551` sends `sizeBytes`/`partCount`), and its
   `partCount`/resume model has to be re-derived from `partSize` and
   `completedParts`/`missingParts`.
5. `npm run check:api-contract` must reach 0 fatal. Only then wire it into CI next
   to `check-api-drift` - together they close the loop bundle-vs-client and
   bundle-vs-server, which is how a P0 shipped silently twice.

Working reference for the real protocol: `tests/cross-layer/seams/seam-upload-storage.spec.ts`
already drives the full two-phase flow against MinIO, including the signed part PUT
and the `UPLOAD_INCOMPLETE` refusal. It is a working, executable specification.

### Why the contract drifted, and the durable fix

`openapi.v1.json` is **hand-authored**. It is not generated from the code, and until
this task nothing compared it to the server - `check-api-drift` only checks
client-vs-bundle. The durable fix is to generate the bundle from the API's own
emitted document (`GET /openapi/v1.json`, mapped anonymously at `Program.cs:469`).
Caveat, learned the hard way: the ASP.NET emitter writes an **empty inline schema
for 18 of the 26 request bodies**, so a naive regeneration would delete real client
types. Annotate those `[FromBody]` parameters (or configure the emitter) before
trusting a regenerated bundle as a drop-in replacement.

### Current repo state

- `main` is this task's commit on top of 040B. Working tree clean apart from
  `master-prompt.md`, a pre-existing scratch file left uncommitted.
- **Login works.** Product, not just API: form -> 200 -> `?next=` honoured ->
  seeded project rendered from a live response.
- 040A/040B cross-layer rig unchanged and green at 24/24.
- `e2e/journeys/` and `e2e/smoke/` do **not** exist. `playwright.config.ts`
  `testDir` is `./tests/cross-layer`, so root-level `e2e/` is not picked up - that
  config will need widening when 041A is unblocked, along with adding `@journeys`
  and `@smoke` project scoping.

### Gotchas that will cost you a run each

1. **Run the seeder before probing the API by hand.** `docker compose up -d` alone
   gives you a 0-table database and a `500 INTERNAL_ERROR` with
   `42P01: relation "tenant_users" does not exist`. The seeder migrates:
   `dotnet run --project tests/cross-layer/seed/CrossLayerSeed.csproj -- --connection "Host=127.0.0.1;Port=55432;Database=dubbing;Username=dubbing;Password=CHANGE_ME;SSLMode=Disable" --tenant <guid> --user <guid> --project <guid> --pipeline-project <guid> --reset`
2. **Never `localhost`** - always `127.0.0.1`. Docker Desktop resets IPv6 to
   published ports; Chromium and Node resolve `localhost` to `::1` first.
3. **Never query the DOM at `domcontentloaded`** on this SPA - React has not
   mounted and you will invent defects. Wait for a real element.
4. **`process.exit()` after a `fetch` crashes Node on Windows** (`UV_HANDLE_CLOSING`,
   `0xC0000409`) *after* printing, so a tool reports failure then dies natively
   instead of exiting 1. Set `process.exitCode` and return.
5. The frontend container bind-mounts `frontend/dist` read-only, so
   `npm --prefix frontend run build -- --mode cross-layer` is enough to see frontend
   changes in the rig - no image rebuild.
6. `ar` and `ru` have only `common.json`/`nav.json`; there is no `auth.json` in
   them, so they fall back to `en` (`FALLBACK_LOCALE = 'en'`, `i18n.ts:27`). Renaming
   an `en` key has no parallel file to update - but a *new* `en` key with no `ar`/`ru`
   counterpart is a gap for the locale tasks, not for 041A.
7. `check-api-contract` needs a running API and **fails closed** by design. Point it
   elsewhere with `--url` or `API_CONTRACT_URL`.

### Still open from earlier reports

- **Integration suite: 38 failed / 14 passed** (Auth/Project filter), unchanged
  across 040A/040B/041A. Pre-existing and undiagnosed;
  `MePreferencesTests.Missing_Tenant_Claim_401_Tenant_Required` is still the top
  follow-up. Take a baseline before blaming your change.
- **Task 046 unowned** (fixtures, reset, auth seeds, scrubber, per-worker tenant
  isolation). 041A needs its auth/reset/SSE helpers and does not have them.
  Standgaps to retire when it lands: `tests/cross-layer/harness/seed.ts`,
  `harness/environment.ts`, `seed/Program.cs`. Per-**worker** isolation is still
  outstanding - `workers: 1` serialises rather than isolates.
- **The rig cannot run the pipeline** - no `ffmpeg`/`ffprobe` in the worker images
  and no media bytes in storage, so a run sits `Pending` at `MediaValidation`. Even
  with upload fixed, a journey that needs *finished* segments or a *rendered*
  export cannot pass until the media workers and a real upload exist.
- No log scrubbing (`.artifacts/` holds unscrubbed traces; ids only, no secrets) and
  no CI wiring (042B). `/health/ready` fails on this stack (no Redis service); the
  harness probes `/health/live`.

### Naming and config conventions

- Contract gate: `tools/check-api-contract.mjs`, `npm run check:api-contract`.
  Tool tests: `tools/*.test.mjs` via `npm run test:tools` (`node --test`).
- Bundle edits must be followed by `node tools/generate-client.mjs`; drift is then
  checked with `node tools/check-api-drift.mjs`. Editing the bundle alone fails
  `check-api-drift` on the `OPENAPI_VERSION` hash - which is the intended alarm.
- Login testids are now `auth-tenant-id` and `auth-external-subject`; there is no
  password field. Update any spec or test that fills the old ones.
- Committed secrets stay `CHANGE_ME`; the contract gate reads no credentials.
