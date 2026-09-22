# Task 35 — Progress Cancellation Retry Review

## Goal

Expose durable progress, cancellation, selective retry with dependency invalidation, and actionable manual review resolution including realtime updates.

## Context

Binding: progress returns phase/currentStage/completed/failed/retrying/review/skipped/warnings/remaining/percentage (percentage indicator only, never guaranteed ETA). Optional SSE/WebSocket via Redis pub/sub or outbox notifications. Cancel: mark Cancelling → block scheduling → notify saga → finish/cancel in-flight (FFmpeg token) → Cancelled only after durable clean. Retry: manual stage/segment, dependency-aware invalidation, new attempts, immutable artifacts preserved, selected refs updated only after success, quotas/rate respected, cancellation respected. Review: list/inspect/approve/reject/requeue/resolve-with-edit (new manual transcript/translation version + reviewer/reason/metadata + resume). Resolution updates run state. Eligible units continue while others await review. Final completion blocked by unresolved required reviews. Audit all decisions.

## Starting State

Processing/segments/reviews endpoints routed; saga/barrier/lease/retry-budgets exist; QC/review items created; run completion logic exists. No Progress/Cancellation/Retry/Review services, no SSE.

## Scope

Must implement: ProgressService, CancellationService, RetryService, ReviewService, controller logic, SSE endpoint. Must not implement: cost/quota internals (next task) beyond interface calls.

## Instructions

1. Create `ProgressService.GetAsync(tenant,project)`: aggregate RunStageSummaries + StageUnitCompletions for active run → `{ phase, currentStage, completedUnits, failedUnits, retryingUnits, reviewUnits, skippedUnits, warnings[], estimatedRemaining, percentage }` where percentage=`completed/expected*100` rounded, labeled `percentageIndicator` + disclaimer `notEta:true`. Phase derived: `upload|validation|speech|translation|voice|timing|mix|qc|render|completed|failed`.
2. Realtime: `GET /api/v1/projects/{id}/progress/stream` SSE (`text/event-stream`): subscribe Redis `progress:{tenant}:{project}` (publish on every StageCompleted via outbox handler) fallback poll 2s; requires Viewer+; document.
3. `CancellationService.CancelAsync(tenant,project,reason)`: validate Running/ManualReviewRequired else 409; set run Cancelling via RunStateMachine + publish RunCancelledRequested; workers check flag before schedule/commit; FFmpeg linked CancellationToken cancels media jobs; sweeper marks Cancelled only after no Running executions remain; audit.
4. `RetryService.RetryAsync(tenant,project,scope: stage|segment, stageType, segmentId?)`: enforce ManualRetryMaxAttempts + quotas/rate + not Cancelled; create new Attempt (increment), invalidate downstream dependents per StageGraph successors (mark their summaries stale + delete pending executions? Decision: mark dependent StageUnitCompletions as invalidated via new Attempt number, preserve old artifacts immutable, update selected only after new success — document); publish StageWorkRequested with new Attempt; audit.
5. `ReviewService`: `ListAsync`, `GetAsync(reviewId)`, `ApproveAsync/RejectAsync/RequeueAsync/ResolveWithEditAsync(reviewId, reviewer, reason, editedText?)`: transitions via ReviewStateMachine (Open→Approved/Rejected/Requeued/ResolvedWithEdit); ResolveWithEdit creates new TranscriptVersion or TranslationVersion `IsManual=true` + reviewer/reason, marks selected, resumes blocked stage (publish StageWorkRequested or StageCompleted per saga), updates run ManualReviewRequired→Running if no open required remain; all publish ReviewResolved + audit.
6. Wire controllers: `GET .../processing` (run detail), `GET .../progress`, `POST .../cancel`, `POST .../retry` body `{scope, stageType, segmentId?}`, `POST .../segments/{sid}/retry`, review routes `GET .../reviews`, `GET /reviews/{id}`, `POST /reviews/{id}/approve|reject|requeue|resolve` (resolve body `{editedText, reason}`).
7. Idempotency: cancel/retry 24h.

## Requirements

- R1: Progress reflects true unit states, not ETA.
- R2: Cancel blocks new work, durable Cancelled.
- R3: Retry invalidates only dependents, new attempt.
- R4: Approve resumes; reject blocks/fails per policy; edit creates manual version.
- R5: Audit on all manual decisions.

## Edge Cases and Error Handling

- Retry on Cancelled → 409.
- Resolve with empty edit → 400.
- Cancel race with completion → exactly one terminal state (conditional update on run status).
- Review for non-existent segment → 404.
- Unresolved required reviews block RunCompleted (enforced in RenderWorker + here).

## Security and Safety Requirements

- Role checks (cancel/retry Owner+, review Reviewer+); tenant ownership; audit append-only.

## Testing

Create `tests/DubbingPlatform.IntegrationTests/Api/ProgressRetryReviewTests.cs`: `Progress_Reflects_Units`, `Cancel_Blocks_Work`, `Retry_Invalidates_Dependents_Only`, `Approve_Resumes`, `Reject_Blocks`, `Audit_Recorded`, `Resolve_Creates_Manual_Version`.

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~ProgressRetryReviewTests
```

## Completion Criteria

- Progress/cancel/retry/review + SSE work; tests pass.

## Traceability

- Plan Section 24; Functional checklist review/cancel/retry/progress; Completion criteria observe/retry/resume/resolve.
