# 003 — Domain Enums and Value Objects Report

## Status
COMPLETED

## Summary
Implemented all 25 domain enums in `src/DubbingPlatform.Domain/Enums/` and all 10 value objects in `src/DubbingPlatform.Domain/ValueObjects/` with exact member/property names, validation, and loudness/timing defaults. Added `tests/DubbingPlatform.UnitTests/Domain/EnumAndValueObjectTests.cs` with the 6 required tests. `dotnet build` succeeds with 0 warnings 0 errors and the filtered test run passes 6/6.

## Files Created/Modified
- `src/DubbingPlatform.Domain/Enums/ProjectStatus.cs` — Enum Created/Uploading/MediaReady/MediaRejected/Processing/Cancelling/Cancelled/Completed/Failed/ManualReviewRequired.
- `src/DubbingPlatform.Domain/Enums/ProcessingRunStatus.cs` — Enum Pending/Running/Completed/Failed/Cancelling/Cancelled/ManualReviewRequired.
- `src/DubbingPlatform.Domain/Enums/StageStatus.cs` — Enum Pending/Scheduled/Running/Completed/Failed/RetryPending/Cancelled/ManualReviewRequired/Skipped.
- `src/DubbingPlatform.Domain/Enums/StageType.cs` — Enum with 17 pipeline stages MediaValidation..Render.
- `src/DubbingPlatform.Domain/Enums/ScopeType.cs` — Enum Run/Project/Speaker/Window/Segment.
- `src/DubbingPlatform.Domain/Enums/OutcomeClass.cs` — Enum Success + 10 failure/policy outcomes.
- `src/DubbingPlatform.Domain/Enums/FailureCategory.cs` — Enum with 19 internal categories; XML doc notes distinct from public error codes.
- `src/DubbingPlatform.Domain/Enums/AssetType.cs` — Enum with 19 asset kinds.
- `src/DubbingPlatform.Domain/Enums/ArtifactType.cs` — Enum mirroring AssetType 1:1; XML doc notes asset→artifact mapping.
- `src/DubbingPlatform.Domain/Enums/ArtifactStatus.cs` — Enum Pending/Committed/Deleted.
- `src/DubbingPlatform.Domain/Enums/ContentObjectStatus.cs` — Enum Pending/Committed/Orphaned/Deleted.
- `src/DubbingPlatform.Domain/Enums/UploadStatus.cs` — Enum Created/InProgress/Completed/Aborted/Expired/Duplicate.
- `src/DubbingPlatform.Domain/Enums/MediaAssetStatus.cs` — Enum Pending/Valid/Invalid.
- `src/DubbingPlatform.Domain/Enums/QualityStatus.cs` — Enum Pass/PassWithWarnings/RetryRequired/ManualReviewRequired/Blocked.
- `src/DubbingPlatform.Domain/Enums/SyncStatus.cs` — Enum SyncAcceptable/SyncAcceptableWithWarning/SyncRetryable/ManualReviewRequired.
- `src/DubbingPlatform.Domain/Enums/ProviderType.cs` — Enum Mock/Azure/OpenAI/Google/LocalInference.
- `src/DubbingPlatform.Domain/Enums/ProviderCapability.cs` — Enum Vad/Diarization/Transcription/Translation/Tts/SourceSeparation/VideoIntelligence/LocalInference.
- `src/DubbingPlatform.Domain/Enums/VoiceType.cs` — Enum Stock/Cloned/Synthetic.
- `src/DubbingPlatform.Domain/Enums/ExportFormat.cs` — Enum Srt/WebVtt/JsonTimeline/SpeakerMetadataJson/TranscriptJson/TranslationJson/QualityReportJson.
- `src/DubbingPlatform.Domain/Enums/ExportJobStatus.cs` — Enum Pending/Running/Completed/Failed/Cancelled.
- `src/DubbingPlatform.Domain/Enums/ReviewStatus.cs` — Enum Open/Approved/Rejected/Requeued/ResolvedWithEdit; XML doc notes terminal states.
- `src/DubbingPlatform.Domain/Enums/ReviewDecisionType.cs` — Enum Approve/Reject/Requeue/ResolveWithEdit.
- `src/DubbingPlatform.Domain/Enums/AudioMixPolicy.cs` — Enum DuckBackground/KeepBackground/MuteBackground.
- `src/DubbingPlatform.Domain/Enums/SourceSeparationPolicy.cs` — Enum Disabled/Enabled/Auto.
- `src/DubbingPlatform.Domain/Enums/ConsentStatus.cs` — Enum Granted/Revoked/Expired/Pending.
- `src/DubbingPlatform.Domain/ValueObjects/TimeRange.cs` — Readonly record struct `TimeRange(int StartMs, int EndMs)` with `DurationMs` property + `GetDurationMs()` method, invariant `ToString()`.
- `src/DubbingPlatform.Domain/ValueObjects/ContentHash.cs` — Sealed record with `Sha256Hex`, 64-lowercase-hex validation, shared `internal static IsValidHex(string?)`.
- `src/DubbingPlatform.Domain/ValueObjects/ConfigurationHash.cs` — Sealed record, same shape, validates via `ContentHash.IsValidHex`.
- `src/DubbingPlatform.Domain/ValueObjects/ExecutionSnapshotHash.cs` — Sealed record, same shape, validates via `ContentHash.IsValidHex`.
- `src/DubbingPlatform.Domain/ValueObjects/ProviderRouteHash.cs` — Sealed record, same shape, validates via `ContentHash.IsValidHex`.
- `src/DubbingPlatform.Domain/ValueObjects/PromptHash.cs` — Sealed record, same shape, validates via `ContentHash.IsValidHex`.
- `src/DubbingPlatform.Domain/ValueObjects/Money.cs` — Sealed record `Money(decimal Amount, string? Currency = "USD")`, null→USD, ISO-4217 normalization.
- `src/DubbingPlatform.Domain/ValueObjects/ProviderModelReference.cs` — Sealed record with Provider/Model/ModelVersion/Deployment/Region/ApiVersion.
- `src/DubbingPlatform.Domain/ValueObjects/LoudnessTarget.cs` — Sealed record with `WebDefault` (-16/-1) and `Broadcast` (-23/-1) statics.
- `src/DubbingPlatform.Domain/ValueObjects/TimingWindow.cs` — Sealed record with 8 properties and 50/100/15.0/1.15 defaults.
- `tests/DubbingPlatform.UnitTests/Domain/EnumAndValueObjectTests.cs` — 6 tests with exact required names; enum test is one `[Fact]` looping 25 enums.

## Decisions Made
- Scope says "20 enums" but lists 25 and R1 requires 25: implemented all 25 with exact member names/order as listed.
- `TimingWindow` has 8 properties (`TargetOnsetMs`, `TargetDurationMs`, `AllowableLeadMs=50`, `AllowableLagMs=50`, `MaxRateChangePercent=15.0`, `MaxStretchFactor=1.15`, `PreferredToleranceMs=50`, `MaxToleranceMs=100`): the spec lists 6 named properties then says "store both PreferredToleranceMs=50, MaxToleranceMs=100", so both pairs are stored; defaults satisfy preferred-50/max-100.
- `TimeRange.DurationMs` implemented as read-only property plus `GetDurationMs()` method: spec calls it a "method" but property is the idiomatic C# shape; providing both covers either consumer (`DurationMs` and `GetDurationMs()` return `EndMs - StartMs`).
- Hash validation uses a manual lowercase-hex loop equivalent to regex `^[0-9a-f]{64}$` (documented in XML doc) instead of `Regex`, shared via `internal static ContentHash.IsValidHex(string?)`: deterministic, culture-invariant, allocation-light; all 4 sibling hash types reuse it so behavior is identical while types stay distinct.
- `Money` normalizes currency with `ToUpperInvariant()` and requires exactly 3 A-Z letters; only `null` defaults to `"USD"` (empty/whitespace throws `ArgumentException`): matches "null defaults, do not throw" without silently accepting garbage.
- `LoudnessTarget` is intentionally permissive (no range throw): task specifies no error case for it and future tasks (028/029/031) may use other targets; validation would risk breaking them.
- `TimingWindow` validation is lenient by design (`TargetDurationMs >= 0`, `MaxStretchFactor >= 1.0`, `MaxToleranceMs >= PreferredToleranceMs`): the parameterless default `new TimingWindow()` (0,0,50,50,15.0,1.15,50,100) must be valid or the defaults test fails.
- Enum test is a single `[Fact]` (`All_Enums_Contain_Expected_Members`) asserting all 25 enums including exact count (`Assert.Equal(25, ...)` and per-enum exact member count), not a `[Theory]`: keeps the filtered run at exactly 6 tests as the Validation section expects (a `MemberData` theory expands to 30 cases).
- Shell: `default.shell` is broken (`The system cannot find the path specified`); all commands ran via `default.execute` → `tools["claude-code"].PowerShell`, per 002 report.

## Build/Test Results
- `dotnet build` (last 10 lines):
```
  DubbingPlatform.ContractTests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.ContractTests\bin\Debug\net10.0\DubbingPlatform.ContractTests.dll
  DubbingPlatform.IntegrationTests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.IntegrationTests\bin\Debug\net10.0\DubbingPlatform.IntegrationTests.dll
  DubbingPlatform.E2ETests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.E2ETests\bin\Debug\net10.0\DubbingPlatform.E2ETests.dll
  DubbingPlatform.UnitTests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.UnitTests\bin\Debug\net10.0\DubbingPlatform.UnitTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:09.84
```
- `dotnet test --filter FullyQualifiedName~EnumAndValueObjectTests` (last 10 lines):
```
No test matches the given testcase filter `FullyQualifiedName~EnumAndValueObjectTests` in C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.ContractTests\bin\Debug\net10.0\DubbingPlatform.ContractTests.dll
No test matches the given testcase filter `FullyQualifiedName~EnumAndValueObjectTests` in C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.E2ETests\bin\Debug\net10.0\DubbingPlatform.E2ETests.dll
No test matches the given testcase filter `FullyQualifiedName~EnumAndValueObjectTests` in C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.IntegrationTests\bin\Debug\net10.0\DubbingPlatform.IntegrationTests.dll
  DubbingPlatform.UnitTests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.UnitTests\bin\Debug\net10.0\DubbingPlatform.UnitTests.dll
Test run for C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.UnitTests\bin\Debug\net10.0\DubbingPlatform.UnitTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:     6, Skipped:     0, Total:     6, Duration: 38 ms - DubbingPlatform.UnitTests.dll (net10.0)
```
- `dotnet build --no-incremental /p:EnforceCodeStyleInBuild=true` (= `make lint` body; `make` binary absent) (last 10 lines):
```
  DubbingPlatform.IntegrationTests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.IntegrationTests\bin\Debug\net10.0\DubbingPlatform.IntegrationTests.dll
  DubbingPlatform.E2ETests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.E2ETests\bin\Debug\net10.0\DubbingPlatform.E2ETests.dll
  DubbingPlatform.UnitTests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.UnitTests\bin\Debug\net10.0\DubbingPlatform.UnitTests.dll
  DubbingPlatform.ContractTests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.ContractTests\bin\Debug\net10.0\DubbingPlatform.ContractTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:21.33
```

## Recommendations for Next Agent (004)
- Repo state: `DubbingPlatform.sln` (10 projects, `net10.0`, `TreatWarningsAsErrors true`) builds clean with 0 warnings; Domain now has `src/DubbingPlatform.Domain/Placeholder.cs` (untouched `public static class Placeholder`), `src/DubbingPlatform.Domain/Enums/*.cs` (25 enums in namespace `DubbingPlatform.Domain.Enums`), `src/DubbingPlatform.Domain/ValueObjects/*.cs` (10 records in namespace `DubbingPlatform.Domain.ValueObjects`). Do not rename enum members or VO properties — 004 entities will reference them.
- Shell gotcha: `default.shell` fails (`The system cannot find the path specified`); use `default.execute` with `tools["claude-code"].PowerShell({command, description})`. Working dir is already `C:\Users\fazeli\source\hobby\media-dub`. `make`, `docker`, `trivy` binaries are absent — validate with `dotnet build`, `dotnet build --no-incremental /p:EnforceCodeStyleInBuild=true`, `dotnet test --filter FullyQualifiedName~<Name>`.
- Naming/conventions: enums are one-file-per-type (`<Name>.cs` = `public enum <Name>`); VOs are sealed records except `TimeRange` which is `public readonly record struct TimeRange`; hash VOs expose `string Sha256Hex { get; init; }`; `Money(decimal Amount, string? Currency = "USD")`; `ProviderModelReference(string Provider, string Model, string? ModelVersion = null, string? Deployment = null, string? Region = null, string? ApiVersion = null)`; `LoudnessTarget(double IntegratedLufs = -16.0, double TruePeakDbtp = -1.0)` with statics `LoudnessTarget.WebDefault` and `LoudnessTarget.Broadcast`; `TimingWindow(int TargetOnsetMs = 0, int TargetDurationMs = 0, int AllowableLeadMs = 50, int AllowableLagMs = 50, double MaxRateChangePercent = 15.0, double MaxStretchFactor = 1.15, int PreferredToleranceMs = 50, int MaxToleranceMs = 100)`.
- Key APIs: `new TimeRange(int startMs, int endMs)` throws `ArgumentOutOfRangeException` on `startMs < 0` or `endMs <= startMs`; `int DurationMs { get; }` / `int GetDurationMs()`; `new ContentHash/ConfigurationHash/ExecutionSnapshotHash/ProviderRouteHash/PromptHash(string sha256Hex)` throws `ArgumentException` (param `sha256Hex`) on non-`^[0-9a-f]{64}$`; shared checker `internal static bool ContentHash.IsValidHex(string? value)`; `new Money(decimal amount, string? currency)` throws `ArgumentOutOfRangeException` (param `amount`) on negative, `ArgumentException` (param `currency`) on non-3-letter; `Money.NormalizeCurrency(string?)` is `internal static`; all VOs override `ToString()` with `CultureInfo.InvariantCulture` and are `IEquatable<>` via records.
- Incomplete integration: no entities/`DbContext`/state machines yet (004–006); `ArtifactType`↔`AssetType` 1:1 mapping is documented only, no converter exists; `ReviewStatus` terminal-state rules are documented only, no transition guard exists; `FailureCategory` vs public error codes mapping does not exist yet.
- Test helpers: `tests/DubbingPlatform.UnitTests/Domain/EnumAndValueObjectTests.cs` (`DubbingPlatform.UnitTests.Domain.EnumAndValueObjectTests`) has the 6 tests; keep their exact names and keep the enum test as a single `[Fact]` so the filtered count stays 6. Existing `SmokeTests.Smoke_Passes()` in each of `tests/DubbingPlatform.{UnitTests,IntegrationTests,ContractTests,E2ETests}/SmokeTests.cs` still passes. UnitTests reference Domain directly (`ProjectReference` to `src/DubbingPlatform.Domain/DubbingPlatform.Domain.csproj`).
- Warnings: `EnforceCodeStyleInBuild true` + `TreatWarningsAsErrors true`; keep file-scoped namespaces, 4-space indent, LF-only, `insert_final_newline true`, `System` usings first in a single group; no `DateTime.Now`/randomness in Domain; no secrets/logging in Domain.
- Config: untouched — appsettings still hold 16 empty `{}` sections; Docker/compose/Makefile untouched; do not add packages in 004 unless entities require them (Domain currently has zero package refs).
