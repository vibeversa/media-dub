# Task 32 — Quality Control

## Goal

Run segment and project QC checks with signal assertions, structured reports, render blocking, and review creation.

## Context

Binding: segment checks (missing segment, empty translation, missing voice, missing audio, sync failure, checksum mismatch, stale provider metadata, unresolved review); project checks (invalid overlaps, overflow, gaps, drift, clipping, silence, corrupt artifacts, rate/channel mismatch, voice instability, terminology where configured); signal assertions (channel routing, placement, attenuation, peak/RMS/loudness, silence boundaries, crossfades). Persist QualityResult (scope/status/code/severity/message/details/artifact). Classify PASS/PASS_WITH_WARNINGS/RETRY_REQUIRED/MANUAL_REVIEW_REQUIRED/BLOCKED. Block render on blocking. Store QC report artifact. Publish render only when allowed. Create reviews for review-required.

## Starting State

Mixed audio + timeline + all segment artifacts exist. No QualityControlWorker/Service.

## Scope

Must implement: QualityControlService/Worker, all checks, classification, blocking, report artifact, review creation. Must not implement: render/exports.

## Instructions

1. Create `src/DubbingPlatform.Application/Services/QualityControlService.cs`: `RunAsync(tenant,project,run,ct)` executing in order: segment loop (for each segment: check selected transcript non-empty, voice assigned, generated audio present + SHA matches ContentObject, SyncResult not ManualReviewRequired unless review resolved, provider metadata fresh (ProviderExecution within run attempt), no open required ReviewItem) → project checks (overlap validity via SegmentOverlaps, overflow vs source, gaps >5s unexpected flag warning, duration drift |mixed-source|>500ms warning, clipping via peak measure, silence >10s warning, corrupt (FFprobe fail) BLOCKED, sampleRate≠48000 BLOCKED, channels mismatch BLOCKED, speaker→voice 1:1 stability BLOCKED if violated, glossary violations BLOCKED if `settings.terminologyStrict=true`) → signal assertions via FFmpeg ebur128/astats (routing, placement ±50ms, attenuation -12±3dB during dialogue, loudness ±1LU, silence boundaries, crossfade presence).
2. Persist one QualityResult per check failure/warning (Status mapped: pass→PASS, warnings→PASS_WITH_WARNINGS, retryable→RETRY_REQUIRED, review→MANUAL_REVIEW_REQUIRED, blocking→BLOCKED; Severity Info/Warning/Error/Blocking; Code e.g., QC_MISSING_AUDIO, QC_CLIPPING, QC_CHECKSUM_MISMATCH). Store report artifact type QcReport (JSON array + schema v1).
3. Blocking rule: any BLOCKED → stage Failed + no render publish + Run stays Running (await manual fix) or Failed per `Qc: { BlockFailsRun=false }` default false (document: block render, create review, run→ManualReviewRequired).
4. Create `QualityControlWorker : BaseConsumer<StageWorkRequested>` (QualityControl, scope Run, queue media.render): claim, call service, if allowed publish StageCompleted for Render else publish StageReviewRequired + create ReviewItems.
5. Config: `Qc: { BlockFailsRun=false, TerminologyStrict=false }`.

## Requirements

- R1: Corrupt artifact blocks render.
- R2: Clean fixture passes/warns.
- R3: Report persisted.
- R4: Reviews created when required.
- R5: Results queryable (via API later + direct DB).

## Edge Cases and Error Handling

- Checksum mismatch → BLOCKED + ARTIFACT_CHECKSUM_MISMATCH.
- Stale metadata (execution from prior attempt) → RETRY_REQUIRED.
- Unresolved required review → BLOCKED until resolved.
- Missing segments (expected>actual) → BLOCKED.

## Security and Safety Requirements

- Tenant-scoped checks; no secrets; FFmpeg safe; checksum revalidate optional `Qc:VerifyChecksums=true` default true.

## Testing

Create `tests/DubbingPlatform.IntegrationTests/Pipeline/QcTests.cs`: `Corrupt_Blocks_Render`, `Clean_Passes`, `Report_Persisted`, `Review_Created_When_Required`, `Checksum_Mismatch_Blocked`.

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~QcTests
```

## Completion Criteria

- QC blocks invalid, reports + reviews work; tests pass.

## Traceability

- Plan Section 21; Functional checklist QC blocks; Error checklist missing/clipping/checksum/invalid-overlap.
