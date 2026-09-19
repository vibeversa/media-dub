# Task 29 — Timing Optimization

## Goal

Fit generated dialogue into source windows via bounded optimization with deterministic scoring and explicit classification.

## Context

Binding: dimensions onset/voiced-duration/window/lead-lag/internal-silence/rate-limit/stretch-limit. Defaults preferred ±50ms, max ±100ms, rate ±15%, stretch 1.15x. Measure generated duration, compare to window, loop: alternate candidate → prosody/rate → rewrite → stretch. Limits max candidates 3, preview attempts 3, rewrites 2. syncScore from duration/onset/rate/stretch/silence penalties. Classify SyncAcceptable/SyncAcceptableWithWarning/SyncRetryable/ManualReviewRequired. Persist SyncResult. Update selected translation/audio when changed. Record attempts+executions. Exhausted + out-of-tolerance → review. Barrier per segment.

## Starting State

Generated final audio + translation candidates exist. No TimingOptimizationWorker/Service, no SyncResult logic.

## Scope

Must implement: TimingOptimizationService/Worker, scoring, bounded loop, classification, review routing. Must not implement: timeline/mixing and later.

## Instructions

1. Create `src/DubbingPlatform.Application/Services/TimingOptimizationService.cs`: `OptimizeAsync(tenant,project,run,segment,ct)`: load target window (segment Start/End + AllowableLead/Lag from TimingOptions Preferred/Max), generated duration via FFprobe; compute `durationErr=actual-target`, `onsetErr=0` (placement fixed at segment start; document), `rateDelta`, `stretchPenalty`, `silencePenalty`; `syncScore = 100 - (w1*|durationErr|/10 + w2*|onsetErr|/10 + w3*|rateDelta|*100 + w4*stretchExcess*50 + w5*silenceMs/100)` weights w1=1,w2=1,w3=0.5,w4=2,w5=0.2 clamp 0..100 (document formula); loop max Candidates 3 / Previews 3 / Rewrites 2: try alternate translation (from stored alternatives) → adjust prosody rate within ±15% → request rewrite via TranslationService (counts as rewrite) → time-stretch via FFmpeg `atempo` within 1.15x (preview artifacts only); after each try re-measure; stop when |durationErr|≤50ms (Acceptable) or ≤100ms (AcceptableWithWarning) else continue; classify: ≤50 Acceptable, ≤100 Warning, else if attempts remain Retryable (RetryPending) else ManualReviewRequired.
2. Persist SyncResult per final attempt (Status mapped from classification) + update IsSelected on chosen translation/audio; record all preview ProviderExecutions + FFmpeg stretch args.
3. Create `TimingOptimizationWorker : BaseConsumer<StageWorkRequested>` (TimingOptimization, scope Segment, queue ai.provider): claim, call service, Complete (Acceptable*) or MarkReviewRequired (Manual) + barrier.
4. Enforce hard caps: never exceed rate 15% or stretch 1.15x even if still out-of-tolerance (go review instead); log violations as invariant errors.
5. Config: `Timing:` options from Task 8 (Preferred 50, Max 100, Rate 15, Stretch 1.15, Candidates 3, Previews 3, Rewrites 2).

## Requirements

- R1: Tight windows respect caps (no violations).
- R2: Loop stops after configured attempts.
- R3: SyncResult stored with score + status.
- R4: Review created when exhausted.
- R5: Selected refs updated on change.

## Edge Cases and Error Handling

- Zero-duration generated → PROVIDER_INVALID_RESPONSE, no stretch.
- Stretch would exceed 1.15x → skip stretch, try rewrite or review.
- Rewrite budget exhausted → review, not infinite loop.
- Lease lost → discard previews.

## Security and Safety Requirements

- FFmpeg atempo via ArgumentList; temp previews cleaned except persisted artifacts; no secrets.

## Testing

Create `tests/DubbingPlatform.UnitTests/Pipeline/TimingTests.cs`: `Respects_Limits`, `Stops_After_Attempts`, `No_Violation`, `SyncResult_Stored`, `Exhausted_Creates_Review` (mock TTS durations).

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~TimingTests
```

## Completion Criteria

- Timing bounded/scored/classified; tests pass.

## Traceability

- Plan Section 18; Assumptions 72–74; Functional checklist timing bounds; Tests checklist timing bounds.
