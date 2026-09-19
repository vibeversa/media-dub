# Task 038 — Backend Metrics, Frontend Telemetry, Analytics, Correlation

**Required/Optional:** Required
**Complexity:** S

## Goal
Wire product-grade observability — backend metrics, safe frontend telemetry, allowlisted analytics with opt-out, and end-to-end correlation — without leaking sensitive content.

## Context
SSE/notification/read-model/upload/review/export latencies need backend counters; the Task 018 telemetry harness needs its event taxonomy finalized; analytics must be allowlisted + opt-out per privacy rules; correlation IDs (Task 017) must flow end-to-end. Sensitive content (tokens/URLs/media/transcript) must never enter telemetry.

## Starting State
Task 018 (telemetry baseline) and Task 026 (SSE client) done. Depends on Tasks 018 and 026.

## Scope
Included: backend latency/failure metrics, frontend telemetry taxonomy + scrubbing, allowlisted analytics + opt-out, E2E correlation IDs.
Excluded: admin ops dashboard rendering (Task 036), E2E test scenarios (Task 041), CI quality gates (Task 042).

## Instructions
1. Add backend metrics in `src/DubbingPlatform.Api/Observability/BackendMetrics.cs` (OpenTelemetry counters/histograms): SSE connections/reconnects, notification projection failures, read-model query latency, upload-funnel stages, review/export/preview latencies; every metric labeled with `tenant_id` hash (never raw id) + endpoint/operation, never user content.
2. Finalize frontend telemetry in `frontend/src/telemetry/events.ts`: taxonomy page/API/UI/upload/SSE with payload schema `app/env/browser/OS/route/correlationId`; implement `frontend/src/telemetry/scrub.ts` stripping tokens/URLs/media/transcript/stack internals before emission; scrubber unit-tested with adversarial fixtures.
3. Implement allowlisted analytics in `frontend/src/telemetry/analytics.ts`: permitted events only — `project.created`, `upload.*`, `processing.*`, `review.*`, `translation.edited`, `voice.changed`, `export.*`; any non-allowlisted event is dropped + logged in dev; user opt-out persisted in preferences (Task 006 key) disables analytics while keeping error/crash telemetry.
4. Propagate E2E correlation in `frontend/src/api/client/correlation.ts` + `src/DubbingPlatform.Api/Middleware/CorrelationMiddleware.cs`: generate `correlationId` per user action, send as header, echo in error envelopes (Task 013), join frontend + backend spans; review/version-conflict errors carry the id so support can trace both sides.
5. Add `tests/DubbingPlatform.IntegrationTests/Observability/ObservabilityTests.cs`: metric emission smoke (SSE connect → counter, failed projection → failure counter), correlation propagation (action id appears in backend span + error envelope), scrubber never emits sensitive fixtures.

## Requirements
- R1: Backend emits SSE/reconnect, notification-failure, read-model-latency, upload-funnel, review/export/preview-latency metrics.
- R2: Frontend telemetry always includes app/env/browser/OS/route/correlation and never tokens/URLs/media/transcript.
- R3: Analytics emits only allowlisted events; opt-out disables analytics, preserves crash telemetry.
- R4: Every user action carries a correlation ID visible in both frontend spans and backend errors.
- R5: Scrubber proven by adversarial unit tests (sensitive fixtures → fully redacted output).

## Edge Cases and Error Handling
- Telemetry endpoint down → buffer locally (cap 100 events), drop oldest, never block UI or retry-storm.
- Opt-out toggled mid-session → in-flight analytics batch dropped, subsequent events suppressed.
- Missing correlation on legacy call path → middleware mints one, marks `correlation_propagated=false`.
- Metric cardinality explosion (high-cardinality route params) → route-template labeling only, ids hashed.
- SSE reconnect storm → reconnect counter + backoff (Task 026) prevents metric flood.

## Security and Safety Requirements
- Scrub tokens/secret-bearing headers/signed URLs/media bytes/transcript text before any emission, frontend or backend.
- Tenant labeling uses one-way hashes; no raw tenant/user ids in metric labels.
- Analytics allowlist enforced at the emit call-site; dynamic event names rejected by type signature.

## Testing
- Create `tests/DubbingPlatform.IntegrationTests/Observability/ObservabilityTests.cs`: metric smoke, correlation propagation, failure-counter paths.
- Create `frontend/src/telemetry/__tests__/telemetry.test.ts`: taxonomy conformance, scrubber adversarial fixtures, allowlist drop-behavior, opt-out suppression.
- Type: backend integration + frontend unit; run `npm run test -- src/telemetry`.

## Validation
```bash
dotnet test --filter FullyQualifiedName~ObservabilityTests
cd frontend && npm run test -- src/telemetry
```

## Completion Criteria
- Backend metrics, scrubbed frontend telemetry, allowlisted analytics with opt-out, and E2E correlation IDs operate with zero sensitive leakage; `ObservabilityTests` + telemetry unit tests pass.

## Traceability
- Plan B §14.1–§14.4, §18.1–§18.2. Depends on Tasks 018 and 026.

## Review Fix — Scope Clarification (Completion Gate)
- **No new correlation bootstrap:** correlation IDs + error taxonomy are established in 013/017; metrics hooks are added in backend tasks (002/005/008). This task VALIDATES completeness: taxonomy conformance, scrubbing/leakage tests (no tokens/URLs/media/transcript), analytics allowlist + opt-out, SSE/notification/read-model latency coverage.
