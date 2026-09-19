# Task 24 — Transcription Worker

## Goal

Produce versioned segment transcripts with word timestamps, confidence handling, provider fallback, review routing, and barrier updates.

## Context

Binding: segment-scoped executions, bounded-batch dispatch (batch 50), per-segment or batched provider calls where supported. TranscriptVersion fields provider/model/language/text/confidence/word-artifact/selected/review. Record provider executions. Low confidence (<0.70 default `Transcription:ConfidenceThreshold=0.70`): retry per budget → fallback provider if compatible → mark review required if still low. Never fail entire project for one segment unless `failurePolicy=fail-fast` (default `per-segment-review`). Best-version selection deterministic: confidence → provider priority → completeness (text length>0) → timestamp quality (word count>0). Store alternatives. Per-segment completion + barrier.

## Starting State

Segments + speakers exist. No TranscriptionWorker/Service. Provider transcription + resolver + barrier + dispatcher exist.

## Scope

Must implement: TranscriptionService/Worker, batch dispatch wiring, version selection, fallback/review. Must not implement: context/translation and later.

## Instructions

1. Create `src/DubbingPlatform.Application/Services/TranscriptionService.cs`: `TranscribeSegmentAsync(tenant,project,run,segment,attempt,ct)`: resolve ITranscriptionProvider via Resolver (capability Transcription, language=source lang), enforce cost reservation via ICostGate (stub allow; real Task 36 — call interface), invoke provider with segment audio slice (extract via FFmpeg slice `[-ss start -t duration]` to temp wav 16kHz mono for provider), persist TranscriptVersion(s) (one per attempt/provider), record ProviderExecution, apply confidence policy: if <threshold and attempts remain → throw retryable (stage RetryPending); elif fallback provider available → try once; else create ReviewItem (reason LOW_CONFIDENCE, status Open) + mark version NeedsReview + stage unit ManualReviewRequired.
2. Selection: `SelectBestAsync(segmentId)`: order by Confidence desc, provider priority index, Text.Length>0, Words.Count>0; set IsSelected=true on winner.
3. Create `src/DubbingPlatform.Workers/Consumers/TranscriptionWorker.cs` : BaseConsumer<StageWorkRequested> (Transcription, scope Segment, queue ai.provider): claim segment execution, call service, on success Complete + BarrierService.RecordUnitCompletion(Completed); on review → MarkReviewRequired + barrier ReviewUnits; publish StageCompleted per segment. Dispatcher (Task 12) publishes batches; worker respects idempotency (duplicate segment message returns existing execution).
4. Word timestamps stored as Artifact type Transcript (JSON words + schema v1) linked via WordTimestampsArtifactId.
5. Config: `Transcription: { ConfidenceThreshold=0.70, MaxAttempts=3, BatchSize=50 }`.

## Requirements

- R1: Clear speech → selected transcript.
- R2: Low confidence → fallback then review, not project fail.
- R3: Alternatives stored, winner deterministic.
- R4: Provider executions recorded.
- R5: Barrier counts correct.

## Edge Cases and Error Handling

- Empty audio slice → validation fail fast, no retry.
- Provider rate-limit → delayed retry (StageLeaseTimeout delay, not immediate).
- Provider invalid response → fallback, not transport retry.
- Lease lost mid-call → discard, reconcile via request hash.
- Cancelled run → abort before persist.

## Security and Safety Requirements

- Tenant-scoped audio slices deleted after use; no transcript PII beyond tenant boundary; no secrets logged.

## Testing

Create `tests/DubbingPlatform.IntegrationTests/Pipeline/TranscriptionTests.cs` (mocks): `Clear_Speech_Selected`, `Low_Confidence_Fallback`, `Persistent_Low_Creates_Review`, `Barrier_Correct`, `Execution_Recorded`.

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~TranscriptionTests
```

## Completion Criteria

- Transcription versioned/selective/review-aware; tests pass.

## Traceability

- Plan Section 13; Functional checklist transcripts versioned; Error checklist low-confidence fallback/review.
