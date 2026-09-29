# Task 041A - Cross-Feature Journeys and Full Smoke

## Status

**BLOCKED**

The target file `execution_tasks_B/041-e2e-visual-a11y-perf.md` is marked
`REVIEW FIX - SUPERSEDED (split)` on its first line and redirected to 041A + 041B +
041C + 041D, so this report covers **041A** (journeys + full smoke), the first
split unit. 041B (visual), 041C (a11y) and 041D (performance) are untouched.

041A is blocked by a P0 defect in another task's surface: **the product's login is
broken, so no journey and no full smoke can get past its first step.** This is
proved end-to-end in a real browser below, not inferred.

## Summary

Attempted the section 24 full smoke against the 040A rig and it failed at step one:
the login form submits `{ email, password, tenantSlug }`, the API answers `400`,
and the user is shown "Email, password, or tenant is incorrect" for credentials
the server never received. The cause is that the committed OpenAPI bundle
`src/DubbingPlatform.Api/OpenApi/openapi.v1.json` is hand-authored and describes
an authentication model the API does not implement, while
`tools/check-api-drift.mjs` only checks that the generated TypeScript client
matches that bundle - never that the bundle matches the server. So every generated
request body is typed from a document that misdescribes the API, and every
existing gate passes.
Shipped the missing gate (`tools/check-api-contract.mjs` + 9 tests) that diffs the
bundle against the API's own emitted document, which reports **25 of 26** JSON
request bodies divergent. No journey or smoke spec was written: on this foundation
they would either be red or would fake the one step that is broken.

## Files Created/Modified

| File | Change |
| --- | --- |
| `tools/check-api-contract.mjs` | **New.** Contract-truth gate: fetches the API's own `GET /openapi/v1.json` and diffs every JSON request body against the committed bundle, operation by operation. Exits 1 on divergence, fails closed when the API is unreachable. |
| `tools/check-api-contract.test.mjs` | **New.** 9 `node:test` cases for the gate's comparison logic (`$ref` resolution incl. cycle guard, prefix alignment, required-vs-properties comparison, one-sided bodies, undeclared operations, report formatting). |
| `package.json` | **Modified.** Added `test:tools` and `check:api-contract` scripts. |

No product code, no existing spec, and no cross-layer rig file was changed.

## Decisions Made

**1. 041 is superseded, so I delivered 041A and reported it BLOCKED rather than
re-merging 041A-D.** The target file's first line is an explicit review fix, and
the split exists so each piece is reviewable. Executing the combined file would
have discarded that decision.

**2. I did not write the journey specs on a seeded session.** Two options existed
for bypassing the broken login, and both are wrong:

- The app's own E2E affordance, `localStorage['dubbing.e2e.session']`
  (`frontend/src/app/session/e2eSeed.ts`), seeds only `{ status, permissions }` and
  makes `AuthProvider` **skip the restore** (`AuthProvider.tsx:40`). There is no
  token, so every API call would 401. It is explicitly a no-backend affordance for
  the hermetic `@shell` suite and cannot authenticate against a real stack.
- Obtaining a real token out-of-band and injecting it would test nothing about the
  login path while appearing to.

Both are the "shared-tenant shortcut" that 040B's edge-case guidance rules out.
The honest position is that 041A's precondition is a working login.

**3. I did not fix the login contract, because the fix is 019's product decision.**
Two directions exist and they are not equivalent:

- *Change the API to match the bundle* would mean inventing password
  authentication - a credential store, hashing, verification, lockout. There is no
  password anywhere in the domain; the implemented model is a passwordless
  external-subject identity. Clearly not intended.
- *Change the bundle to match the API* is the correct direction (the API is the
  implemented, tested surface; the bundle entry has no implementation), but it
  cascades: the bundle, the regenerated client, `LoginPage.tsx`'s three inputs,
  `frontend/src/i18n/locales/en/auth.json`, the `INVALID_CREDENTIALS` copy in
  `frontend/src/api/errors/normalizeError.ts`, 20 login call sites across
  `authStore.test.ts` (12) and `authMatrix2.test.ts` (8), plus
  `sessionFlow.test.tsx` and `httpClient.test.ts`, and the 019-owned
  `frontend/e2e/auth.spec.ts` which mocks the API and fills
  `auth-tenant`/`auth-email`/`auth-password`.

That is a redesign of the login screen plus a rewrite of 019's test surface,
inside a task whose scope line says `Excluded: feature specs (019-036)`. It also
requires a product call I should not make silently: whether asking a user for a
tenant GUID plus an external subject is the intended UX. So the diagnosis is
handed over with the exact file list instead.

**4. I shipped the gate but deliberately did not wire it into a suite.** The
repository is divergent right now, so making `check-api-contract` a hard gate
would turn every other task's green suite red - including 040A/040B's, which this
task must not regress. The intended sequence is: regenerate the bundle from the
server document, confirm the gate is green, then wire it into CI. The tool's header
comment says this so the next agent does not "helpfully" wire it in early.

**5. `admin/{unmatched}` is a true positive, not noise.** The gate reports 5
undeclared operations, all of them the same route. It is a real, intentional
catch-all in `AdminController.cs:441-447` that throws `ADMIN_ROUTE_UNKNOWN` for
unknown admin paths, and ASP.NET's emitter renders the catch-all as
`/api/v1/admin/{unmatched}`. Correctly absent from a hand-written bundle, and
correctly reported.

**6. I corrected my own probe rather than reporting a false defect.** My first
browser run reported the tenant input missing (`tenant: 0`). It was a race: I
queried at `domcontentloaded`, which fires before React mounts, and the three
element counts ran sequentially, so the app mounted between the first and second.
Re-run with a proper wait, all three fields are present. The *login* finding is
unaffected - it was observed from the actual HTTP response. Do not treat the tenant
field as a defect.

## Build/Test Results

### The blocker, in a real browser (Task 041A instruction 2, the section 24 smoke)

```
$ node (repro against the 040A rig: http://127.0.0.1:54173 + :58080)
form fields present: { tenant: 1, email: 1, password: 1 }

request body the browser sent: {"email":"owner@cross-layer.invalid","password":"CHANGE_ME","tenantSlug":"cross-layer"}
auth/login responses: [{"status":400,"method":"POST"}]
error shown to user: Email, password, or tenant is incorrect. Check them and try again.
still on /login: true
```

Every credential the form collects is discarded by the server, which requires
`tenantId` and `externalSubject`. The user cannot authenticate, and the error
message misattributes the failure to their credentials.

The same contrast over plain HTTP, isolating the payload as the only variable:

```
A_frontend_bundle_shape {email,password,tenantSlug} -> 400
B_api_actual_shape       {tenantId,externalSubject}  -> 200 OK
```

### Root cause: the bundle and the API disagree

```
$ Invoke-WebRequest http://127.0.0.1:58080/openapi/v1.json   # the API's own document
$ (committed bundle).components.schemas.AuthLoginRequest

server  (LoginRequest)      {"required":["tenantId","externalSubject"],
                             "properties":{"tenantId":{"type":"string","format":"uuid"},
                                           "externalSubject":{"type":"string","minLength":1,"maxLength":256}}}
bundle  (AuthLoginRequest)  {"required":["tenantSlug","email","password"],
                             "properties":{"tenantSlug":{...},"email":{"format":"email"},"password":{"format":"password"}}}
```

Path coverage is fine (all 75 real paths are declared; the bundle simply omits the
`/api/v1` prefix the live document carries), so this is not a missing-endpoints
problem. It is a request-body problem, and it is systemic.

### The new gate

```
$ npm run test:tools
pass 9
fail 0

$ npm run check:api-contract
operations with a JSON request body compared: 26
divergent: 25
declared in the server but absent from the bundle: 5

  POST /api/v1/auth/login
    reason: different required fields and different properties
    server: LoginRequest required=["externalSubject","tenantId"] properties=["externalSubject","tenantId"]
    bundle: AuthLoginRequest required=["email","password","tenantSlug"] properties=["email","password","tenantSlug"]
  ...
gate exit: 1
```

25 of 26 request bodies diverge. Beyond login, the notable ones a consumer would
hit on the journey path:

| Operation | Server | Bundle |
| --- | --- | --- |
| `POST /projects` | requires `sourceLanguage`,`targetLanguage`; also accepts `description`,`processingSettings`,`settings` | `name`,`sourceLanguage`,`targetLanguage` only - the client cannot send `processingSettings` |
| `PATCH /projects/{id}` | no body | `name`,`processingSettings`,`settingsVersion` |
| `POST /projects/{id}/processing` | no body | `configHash` |
| `PUT /me/preferences` | `UpdatePreferencesRequest { preferences }` | `PreferencesResponse {}` - a *response* schema used as the request |
| `POST /projects/{id}/exports` | `format`,`allowPartial`,`profile`; none required | requires `format` |
| `POST /auth/logout` | no body | `AuthRefreshRequest { refreshToken }` |

Fail-closed behaviour, since a contract gate that passes because it could not
reach the server is worse than no gate:

```
$ node tools/check-api-contract.mjs --url http://127.0.0.1:59999
check-api-contract: could not fetch http://127.0.0.1:59999/openapi.v1.json: fetch failed
  Start the API, e.g. `docker compose -f tests/cross-layer/docker-compose.cross.yml up -d`.
  This gate fails closed on purpose: an unreachable server must not read as a pass.
exit code: 1
```

### Regression check on the existing green suite

The 040A/040B cross-layer suite must not regress, since this task adds a failing
gate to the repo and it would be easy to blame that for an unrelated red:

```
$ npx playwright test --grep="@cross-layer"
  ok 22 [cross-layer-chromium] > seams\seam-voice-invalidation.spec.ts:39:3 > @cross-layer voice-invalidation > assigning a different voice invalidates the dependent output and is persisted (3.2s)
  ok 23 [cross-layer-chromium] > seams\seam-voice-invalidation.spec.ts:142:3 > @cross-layer voice-invalidation > re-assigning the same voice changes nothing and does not add a row (851ms)
  ok 24 [cross-layer-chromium] > seams\seam-voice-invalidation.spec.ts:173:3 > @cross-layer voice-invalidation > refuses an unknown speaker, and refuses to clear the voice pointer (277ms)

  24 passed (1.3m)
```

Unchanged at 24/24. Note what this also proves: the cross-layer seams, which drive
the API directly, are unaffected by the login defect - which is why 040A/040B
passed and this defect sat undetected. The seams never go through the frontend's
generated client, so they exercise a contract the bundle never touches.

## Recommendations for Next Agent (041B)

### Read this first: 041A is not done, and one decision unblocks it

The login contract must be settled before 041A (journeys + smoke) **or** 041B
(visual) can proceed, because 041B's 12 screens are all behind authentication.

The recommended fix, in order:

1. **Decide the login contract.** The API is the implemented, integration-tested
   surface and has no password concept, so the bundle should be corrected to
   `{ tenantId, externalSubject }` - not the reverse.
2. **Regenerate the bundle from the server** rather than hand-editing it. The API
   maps `app.MapOpenApi().AllowAnonymous()` (`Program.cs:469`), so
   `GET /openapi/v1.json` is the source of truth and the hand-authored file is what
   let this drift in the first place. Also resolves the other 24 divergences and
   the 5 undeclared operations in one step.
3. **Regenerate the client** (`tools/generate-client.mjs` / `make generate-api`) so
   `frontend/src/api/generated/` follows, then `npm run check:api-contract` must go
   green before anything else.
4. **Then decide the login UX** and update, together:
   - `frontend/src/features/auth/LoginPage.tsx` - the three inputs become tenant id
     and external subject (or keep a friendly name that the client resolves).
   - `frontend/src/i18n/locales/en/auth.json` - `email`, `password`, `tenant` keys
     and the `loginError` copy.
   - `frontend/src/api/errors/normalizeError.ts:112` - the `INVALID_CREDENTIALS`
     message, which currently blames the user for a request the server never
     understood.
   - `frontend/src/features/auth/__tests__/authStore.test.ts` (12 login call
     sites), `authMatrix2.test.ts` (8), `sessionFlow.test.tsx` (`auth-password`
     testid + error copy), `frontend/src/api/__tests__/httpClient.test.ts:91`.
   - `frontend/e2e/auth.spec.ts:108-110` - the 019-owned feature spec fills
     `auth-tenant`/`auth-email`/`auth-password` against a mocked API. **This is
     019's spec; coordinate rather than rewrite it from 041A.**
5. **Wire `check:api-contract` into CI** once it is green, alongside
   `check:api-drift`. Together they close the loop: bundle-vs-client and
   bundle-vs-server.

### Current repo state

- `main` is at the 040B commit plus this task's commit. Working tree is clean
  apart from `master-prompt.md`, a pre-existing scratch file left uncommitted.
- Cross-layer rig unchanged and still green (see Regression below). 040A/040B
  deliverables untouched.
- Added: `tools/check-api-contract.mjs`, `tools/check-api-contract.test.mjs`,
  and two root npm scripts.

### Gotchas learned here that will cost you a run each

1. **Never `localhost`** - use `127.0.0.1`. Docker Desktop resets IPv6 connections
   to published ports, so Chromium's `localhost` (which resolves to `::1` first)
   never reaches the container.
2. **Do not query the DOM at `domcontentloaded`.** The SPA has not mounted; you
   will "discover" fields that do not exist. Wait for a real element. This produced
   a false defect in my first run and I nearly reported it.
3. **`process.exit()` crashes Node on Windows** if an async handle is still
   closing: `Assertion failed: !(handle->flags & UV_HANDLE_CLOSING)`, exit
   `0xC0000409`, *after* the message has printed. Any tool or test that calls
   `process.exit()` after a `fetch` will print correctly and then die of a native
   crash instead of exiting 1 - a CI step cannot interpret that. Set
   `process.exitCode` and return instead. `tools/check-api-contract.mjs` does this
   deliberately; the comment explains why. This is the same assertion seen in
   earlier Playwright runs on this machine.
4. **The API's OpenAPI document is served anonymously** at
   `http://127.0.0.1:58080/openapi/v1.json` (~406 KB, 76 paths, 134 schemas). Use
   it as the contract source of truth rather than the committed bundle.
5. **The committed bundle omits the `/api/v1` route prefix** that the live document
   carries. Align on the prefix-stripped form; my first comparison reported 76
   missing and 75 phantom paths purely from this before I corrected it.
6. **`playwright.config.ts` `testDir` is `./tests/cross-layer`**, so root-level
   `e2e/` (where 041A's journeys belong per the task file) is *not* picked up. That
   config will need widening for 041A; it has not been touched.
7. Docker Desktop must be running:
   `Start-Process "$env:LOCALAPPDATA\Programs\DockerDesktop\Docker Desktop.exe"`,
   then wait ~25 s for `docker info`.

### Still open from earlier reports

- **Integration suite: 38 failed / 14 passed** in the Auth/Project filter,
  unchanged across 040A/040B. Pre-existing and undiagnosed. Take a baseline before
  blaming your own change.
- **Task 046 unowned** (fixtures, reset, auth seeds, log scrubber, per-worker
  tenant isolation). 041A's instruction 1 says to reuse 046's helpers, so when it
  lands, `tools/check-api-contract.mjs` has nothing to retire but the 040A/040B
  standgaps (`harness/seed.ts`, `harness/environment.ts`, `seed/Program.cs`) do.
- **The rig still cannot run the pipeline** - no `ffmpeg`/`ffprobe` in the worker
  images, no media bytes in object storage, so a run sits `Pending` at
  `MediaValidation`. Journeys that need finished segments or a rendered export
  cannot be written until the media workers and a real upload are added.
- Per-**worker** isolation is still outstanding; `playwright.config.ts` pins
  `workers: 1`, which serialises rather than isolates.
- No log scrubbing and no CI wiring (042B owns the latter).

### Not started, by design

041B (visual regression), 041C (WCAG 2.2 AA) and 041D (performance budgets) are
untouched. All three need an authenticated session, so all three inherit this
blocker. 041B in particular will need the frontend's theme and direction switching
to be verified as reachable from the login screen.

### Naming and config conventions

- Gate scripts live in `tools/*.mjs`, are Node-stdlib-only, and use
  `#!/usr/bin/env node` with a comment header explaining *why* the gate exists.
- New gate tests are `tools/*.test.mjs` run with `node --test`; there was no
  precedent in the repo, so this establishes one. `npm run test:tools`.
- Committed secrets stay `CHANGE_ME`; the contract gate reads no credentials.
