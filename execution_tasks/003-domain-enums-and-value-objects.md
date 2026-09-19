# Task 3 — Domain Enums and Value Objects

## Goal

Implement all domain enums and value objects with exact names, members, and deterministic behavior so later entities and services compile against stable types.

## Context

Binding decisions: timeline values integer milliseconds; canonical audio 48kHz/24-bit PCM-or-FLAC default, 32-bit float temp DSP files; loudness defaults -16 LUFS integrated / -1 dBTP true peak, broadcast -23 LUFS; timing preferred ±50ms, max ±100ms, rate ±15%, stretch 1.15x; internal IDs ULID-compatible UUIDs as `uuid`; public prefixed IDs. Hashes deterministic, culture-invariant, secrets excluded.

## Starting State

Solution with Domain classlib exists and builds with placeholder. No enums or value objects exist. `Directory.Build.props` enforces nullable + warnings-as-errors.

## Scope

Must implement: 20 enums and 10 value objects listed below in `DubbingPlatform.Domain`. Must not implement: entities, DbContext, state machines, services, persistence.

## Instructions

1. Create `src/DubbingPlatform.Domain/Enums/` with one file per enum, namespace `DubbingPlatform.Domain.Enums`. Exact enums and members:
   - `ProjectStatus`: Created, Uploading, MediaReady, MediaRejected, Processing, Cancelling, Cancelled, Completed, Failed, ManualReviewRequired
   - `ProcessingRunStatus`: Pending, Running, Completed, Failed, Cancelling, Cancelled, ManualReviewRequired
   - `StageStatus`: Pending, Scheduled, Running, Completed, Failed, RetryPending, Cancelled, ManualReviewRequired, Skipped
   - `StageType`: MediaValidation, MediaAnalysis, AudioPreparation, SourceSeparation, Vad, SegmentBuild, Diarization, Transcription, ContextBuild, Translation, VoiceAssignment, VoiceGeneration, TimingOptimization, TimelineAssembly, AudioMixing, QualityControl, Render
   - `ScopeType`: Run, Project, Speaker, Window, Segment
   - `OutcomeClass`: Success, ProviderUnavailable, ProviderRateLimited, ProviderTransientFailure, ProviderPermanentFailure, ProviderInvalidResponse, ProviderTimeout, QualityBelowThreshold, PolicyRejected, UnsupportedCapability, Cancelled
   - `FailureCategory`: Validation, MediaUnsupported, MediaCorrupt, ProviderTransient, ProviderPermanent, ProviderRateLimited, ProviderTimeout, ProviderInvalidResponse, QuotaExceeded, RateLimited, LeaseLost, Cancelled, PolicyDenied, ConsentRequired, ConfigurationError, StorageUnavailable, ChecksumMismatch, InvariantViolation, Unknown (document: internal category distinct from public error codes)
   - `AssetType`: SourceOriginal, CanonicalAudio, WorkingAudio, DialogueStem, BackgroundStem, VadRegions, Segments, DiarizationMap, Transcript, Translation, GeneratedAudioPreview, GeneratedAudioFinal, Timeline, MixedAudio, QcReport, RenderedOutput, Export, FfprobeAnalysis, ContextWindow
   - `ArtifactType`: same members as AssetType (keep 1:1 for simplicity; document mapping)
   - `ArtifactStatus`: Pending, Committed, Deleted
   - `ContentObjectStatus`: Pending, Committed, Orphaned, Deleted
   - `UploadStatus`: Created, InProgress, Completed, Aborted, Expired, Duplicate
   - `MediaAssetStatus`: Pending, Valid, Invalid
   - `QualityStatus`: Pass, PassWithWarnings, RetryRequired, ManualReviewRequired, Blocked
   - `SyncStatus`: SyncAcceptable, SyncAcceptableWithWarning, SyncRetryable, ManualReviewRequired
   - `ProviderType`: Mock, Azure, OpenAI, Google, LocalInference
   - `ProviderCapability`: Vad, Diarization, Transcription, Translation, Tts, SourceSeparation, VideoIntelligence, LocalInference
   - `VoiceType`: Stock, Cloned, Synthetic
   - `ExportFormat`: Srt, WebVtt, JsonTimeline, SpeakerMetadataJson, TranscriptJson, TranslationJson, QualityReportJson
   - `ExportJobStatus`: Pending, Running, Completed, Failed, Cancelled
   - `ReviewStatus`: Open, Approved, Rejected, Requeued, ResolvedWithEdit (note: Approved/Rejected/Requeued/ResolvedWithEdit are terminal)
   - `ReviewDecisionType`: Approve, Reject, Requeue, ResolveWithEdit
   - `AudioMixPolicy`: DuckBackground, KeepBackground, MuteBackground
   - `SourceSeparationPolicy`: Disabled, Enabled, Auto
   - `ConsentStatus`: Granted, Revoked, Expired, Pending
2. Create `src/DubbingPlatform.Domain/ValueObjects/` namespace `DubbingPlatform.Domain.ValueObjects`:
   - `TimeRange` record struct: `int StartMs`, `int EndMs`; validates StartMs>=0, EndMs>StartMs; method `DurationMs`.
   - `ContentHash` record: `string Sha256Hex` (64 lowercase hex); validate regex `^[0-9a-f]{64}$`.
   - `ConfigurationHash`, `ExecutionSnapshotHash`, `ProviderRouteHash`, `PromptHash` — same shape as ContentHash (64 hex) with distinct types.
   - `Money` record: `decimal Amount`, `string Currency` (ISO 4217, default `USD`); Amount>=0.
   - `ProviderModelReference` record: `string Provider`, `string Model`, `string? ModelVersion`, `string? Deployment`, `string? Region`, `string? ApiVersion`.
   - `LoudnessTarget` record: `double IntegratedLufs`, `double TruePeakDbtp`; defaults `(-16.0, -1.0)`; broadcast `(-23.0, -1.0)`; static `WebDefault`, `Broadcast`.
   - `TimingWindow` record: `int TargetOnsetMs`, `int TargetDurationMs`, `int AllowableLeadMs`, `int AllowableLagMs`, `double MaxRateChangePercent` (default 15.0), `double MaxStretchFactor` (default 1.15); defaults lead/lag 50 preferred, 100 max — store both `PreferredToleranceMs=50`, `MaxToleranceMs=100`.
3. All value objects immutable, implement `IEquatable`, use invariant culture in ToString.
4. Add unit-test-ready determinism: no DateTime.Now, no randomness.

## Requirements

- R1: All 25 enums exist with exact member names.
- R2: All 10 value objects exist with exact properties/types/validation.
- R3: Loudness defaults match -16/-1 and -23/-1.
- R4: Timing defaults 50/100ms, 15%, 1.15x encoded.
- R5: Hash types validate 64-char lowercase hex.

## Edge Cases and Error Handling

- Invalid TimeRange (negative start, end<=start): throw `ArgumentOutOfRangeException`.
- Invalid hash hex: throw `ArgumentException` with param name.
- Negative Money: throw `ArgumentOutOfRangeException`.
- Null currency: default to USD, do not throw.

## Security and Safety Requirements

- No secrets in enums/value objects. No logging. Input validation as above.

## Testing

Create `tests/DubbingPlatform.UnitTests/Domain/EnumAndValueObjectTests.cs`:
- `All_Enums_Contain_Expected_Members` — reflect each enum, assert listed members exist.
- `TimeRange_Rejects_Negative_Start`
- `ContentHash_Rejects_Invalid_Hex`
- `LoudnessTarget_Defaults_Are_Correct` (Web -16/-1, Broadcast -23/-1)
- `TimingWindow_Defaults_Are_Correct` (50/100/15/1.15)
- `Money_Rejects_Negative`

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~EnumAndValueObjectTests
```

Expected: build 0 warnings; 6 tests pass.

## Completion Criteria

- All enums/value objects compile; tests pass; no entities yet required.

## Traceability

- Plan Section 2 actions 2–3; Assumptions 14, 18–19, 68–73; Tests checklist unit domain.
