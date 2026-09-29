# Cross-layer seam specs (Task 040B)

Seven specs, one per named seam, all on the Task 040A rig. They assert where
layers **meet**, not what each route returns: per-route status-code matrices stay
in `tests/DubbingPlatform.IntegrationTests` (Tasks 006-013, R2).

```bash
docker compose -f tests/cross-layer/docker-compose.cross.yml up -d
npx playwright test --grep="@cross-layer"
docker compose -f tests/cross-layer/docker-compose.cross.yml down
```

Read `tests/cross-layer/README.md` first - it is the rig runbook and records the
API behaviours every seam depends on.

## Map

| Spec | Seam | Owning tasks | Pass criteria |
| --- | --- | --- | --- |
| `seam-upload-storage.spec.ts` | frontend upload -> object storage bytes -> server validation -> ready state | 023 + Plan A ingestion | The bytes the client PUT through the API's pre-signed part URL are byte-identical to the object in storage; the server records the declared size and the completed part; the `upload_sessions` row is persisted. Failure half: a completion with a missing part is refused `UPLOAD_INCOMPLETE` and leaves the session not ready; an aborted session cannot be completed; an unauthenticated read is 401 and leaks no detail; two unknown ids are indistinguishable. |
| `seam-processing-sse.spec.ts` | processing start -> SSE events -> workspace refetch as source of truth | 024/026 + 008 | A started run is reported on the stream with matching ids, and the workspace - not the client's optimistic state - is where the state is read; the run, the active-run pointer and the transition are persisted. Failure half: a concurrent start is refused `RUN_ALREADY_ACTIVE` naming the active run and persists no run; an unauthenticated stream read is 401 and does not name the run. |
| `seam-review-mutation.spec.ts` | review resolution -> versioned mutation + audit + invalidation | 031 + 003/009/011 | A resolve returns an advanced version and persists exactly one `review_decisions` row; reopen -> resolve-with-edit mints a real `transcript_versions` row; the read-back status matches the mutation. Failure half: a stale `expectedVersion` is refused `409` and persists no decision; an unauthorised read is 401 and does not describe the review. |
| `seam-export-download.spec.ts` | export creation -> signed-URL download + completeness metadata | 033 + 012A | Completeness metadata parses and describes the export; the download is a `302` to a signature-bearing, time-bounded URL; the bytes fetched through it are byte-identical to the stored object. Failure half: a tampered path is refused by object storage and returns no bytes; an incomplete export refuses with a named code. |
| `seam-voice-invalidation.spec.ts` | voice change -> dependent invalidation / retry + consent gate | 029 + 010 | A different voice is reported as a change, the stored assignment pointer moves, and the response states whether dependent output is stale. Failure half: re-assigning the same voice reports `changed:false` and does not churn the assignment; an unknown speaker is 404; clearing the voice is refused and leaves the stored pointer untouched. |
| `seam-stale-conflict.spec.ts` | stale edit -> 409 -> refresh UX with draft preserved | 027/028 + 003/009 | Two writers act on the same version; the second is refused `409` naming the version, the winner's text stands, the loser's draft is not persisted as a version, and re-applying it at the refreshed version succeeds. Failure half: an empty edit is refused `400` and does not consume the version. |
| `seam-notification.spec.ts` | backend event -> durable notification -> centre render + deep link | 034 + 002/012B | The notification is listed with type/severity/title and a `resourceType`+`resourceId` deep link; the unread count includes it; marking it read persists a `read_at` and decrements the count on a *different* request. Failure half: two unknown ids are indistinguishable; an unauthenticated centre read is 401 and lists nothing. |

## What each seam is not

- **Not an endpoint contract test.** Each call is a means to a cross-layer
  assertion. Status-code matrices live in 006-013.
- **Not a pipeline test.** Five of the seven seams act on entities the pipeline
  would create (segments, review items, speakers, exports, notifications). The
  rig cannot run the pipeline - see "Scope honesty" below - so the seeder creates
  the row the seam mutates and the seam proves the **write path across
  frontend, API and database**. Ingestion is not proven by these specs.
- **Not a UI test.** `page.goto` proves the served bundle is reachable and is the
  origin of the interaction. 041A-D own journeys, visual, a11y and perf.

## Scope honesty

The rig's workers are `control` and `ai`; the FFmpeg-backed `media-preparation`,
`media-render` and `export` workers are not in the stack, the seeded
`ContentObject` has no bytes in object storage, and media validation therefore
never completes - a started run sits in `Pending` at `MediaValidation`. That was
established, not assumed (040A reported it; 040B re-checked the container for
`ffmpeg`/`ffprobe` and found neither).

So the seeder creates the minimum domain state the seams mutate. A seam here is a
test of a mutation over known state, not of the DAG. A seam that genuinely needs
pipeline completion must add those workers and a real media upload first.

## Isolation

Two rules, both learned by hitting them:

1. **One login per worker.** `POST /auth/login` is rate limited to 5/min per IP.
   `openSeamContext()` is memoised, so a seven-seam suite cannot exhaust the
   budget and fail with `429` on an unrelated request.
2. **Run-starting seams get their own project.** A project has exactly one run
   slot: a start pins an active run and a second start is `409
   RUN_ALREADY_ACTIVE`. The 040A harness smoke already starts a run on the pilot
   project, so `seam-processing-sse` uses the separate pipeline project
   (`SEED.pipelineProjectId`). This is the 040B edge case "seam passes alone but
   fails in full suite" - the fix is per-spec isolation, never a shared-tenant
   shortcut.

Specs are `test.describe.serial` within a file, so dependent assertions observe
one mutation rather than racing each other.

## Quarantine policy (R5, per 046)

**Nothing is quarantined.** There is no flake suppression, no retry, and no skip
in any of the seven specs: `playwright.config.ts` sets `retries: 0` and
`forbidOnly` under CI. If a seam flakes, fix the cause - a fixed-sleep wait, a
shared fixture, a missing isolation boundary - and record it. If a seam is
genuinely blocked, quarantine it with an owner and an issue reference in this
file rather than silencing it in the spec.

## Helpers

All from `../harness/index.js`; never reach into `harness/*.ts` directly.

- `openSeamContext()` / `openPipelineSeamContext()` - memoised client, token,
  project ids and the seeded fixture ids.
- `sameId()` - normalises the three id shapes (public `prj_`, 32-hex N, dashed D).
- `putPart` / `putObject` / `getObject` / `fetchSignedUrl` - real object storage.
- `countRows` / `readProjectRow` / `readAssignedVoiceProfileId` /
  `countOpenReviewItemsForRun` - R3 server-side assertions via `docker exec` psql.
- `ApiClient.requestRaw` + `ApiClient.errorCode` - for the failure halves, where
  the status and code *are* the assertion.

## Adding a seam

One file per seam, importing only from the barrel, tagging the suite
`@cross-layer <name>`, asserting both sides (client state **and** a server row),
and covering a named failure half. Add a row to the map above.
