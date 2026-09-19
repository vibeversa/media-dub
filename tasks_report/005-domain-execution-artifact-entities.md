# 005 — Domain Execution Artifact Entities Report

## Status
COMPLETED

## Summary
Implemented all 30 execution/artifact/provider/QC/review/export/audit/cost entities listed in task 005 scope in `src/DubbingPlatform.Domain/Entities/` (scope lists 30 names despite "29" wording) plus a `ProjectStatusProjection` static documenting the run-to-project projection. Added `tests/DubbingPlatform.UnitTests/Domain/ExecutionEntitiesTests.cs` with the 4 required tests and updated the 004 entity-count assertion from 15 to 45. `dotnet build` and the lint build succeed with 0 warnings 0 errors; the filtered test run passes 4/4 and the 004 suite still passes 5/5.

## Files Created/Modified
- `src/DubbingPlatform.Domain/Entities/TranscriptVersion.cs` — `Id/TenantId/ProjectId/RunId/SegmentId/Provider/Model/Language/Text/Confidence/WordTimestampsArtifactId?/IsSelected/NeedsReview/CreatedAt`; Confidence 0..1.
- `src/DubbingPlatform.Domain/Entities/TranslationVersion.cs` — Full shape with `PrimaryText`, `AlternativeTexts` (not null), `SemanticScore/NaturalnessScore/TimingScore` 0..1, `PromptTemplateId?/PromptHash?/IsSelected`.
- `src/DubbingPlatform.Domain/Entities/GeneratedAudioArtifact.cs` — Full shape with `VoiceProfileId/ContentObjectId`, `DurationMs>=0`, `Attempt>=0`, `IsPreview`.
- `src/DubbingPlatform.Domain/Entities/SyncResult.cs` — Full shape with `SyncStatus Status`, `SyncScore` 0..1, `TargetWindowMs/ActualDurationMs>=0`, `RateDelta` not-NaN, `StretchFactor>0`.
- `src/DubbingPlatform.Domain/Entities/StageExecution.cs` — Full shape with `StageType/ScopeType/ScopeId/SegmentId?/Attempt`, `StageStatus`, lease fields `LeaseOwner/LeaseToken/LeaseTokenVersion>=0/LeaseExpiresAt`, `StartedAt?/CompletedAt?`, `InputHash?/ConfigurationHash/ExecutionSnapshotHash/OutputArtifactIdsJson?/ErrorCode?/ErrorMessage?`, `CreatedAt/UpdatedAt`.
- `src/DubbingPlatform.Domain/Entities/RunStageSummary.cs` — `ProcessingRunId/StageType`, six unit counters all `>=0`, `CreatedAt/UpdatedAt`.
- `src/DubbingPlatform.Domain/Entities/StageUnitCompletion.cs` — `ProcessingRunId/StageType/ScopeType/ScopeId/StageExecutionId/UnitState/CreatedAt`; `UnitState` restricted to Completed/Skipped/Failed/ManualReviewRequired/Cancelled with XML doc of allowed set.
- `src/DubbingPlatform.Domain/Entities/ContentObject.cs` — `ContentHash/Sha256Hex` (both required non-empty strings), `SizeBytes>=0`, `MediaFormat/StorageKey`, `ContentObjectStatus`, `CreatedAt/LastReferencedAt`.
- `src/DubbingPlatform.Domain/Entities/Artifact.cs` — `ProjectId/ProcessingRunId/ProducedByStage?/ArtifactType Type/SchemaVersion?/ContentObjectId/Provider?/Model?/ConfigurationHash?/ExecutionSnapshotHash?/ArtifactStatus/MetadataJson?/CreatedAt` (immutable, no UpdatedAt).
- `src/DubbingPlatform.Domain/Entities/ArtifactParent.cs` — Relational lineage edge `ChildArtifactId/ParentArtifactId` (must differ) with XML doc that lineage is relational, never JSON-only.
- `src/DubbingPlatform.Domain/Entities/StageInputArtifact.cs` — `StageExecutionId/ArtifactId/CreatedAt` link table.
- `src/DubbingPlatform.Domain/Entities/StageOutputArtifact.cs` — `StageExecutionId/ArtifactId/CreatedAt` link table.
- `src/DubbingPlatform.Domain/Entities/ProviderExecution.cs` — Full 30-field trace with `ProviderType/ProviderCapability`, `RequestHash` required, `LatencyMs>=0`, nullable `TokensIn/Out>=0`, `AudioSeconds/EstimatedCost/ActualCost>=0`, `OutcomeClass`, all optional strings rejected when whitespace-only.
- `src/DubbingPlatform.Domain/Entities/ProviderCapabilityDescriptor.cs` — Full descriptor with `SupportedLanguages/SupportedFormats/VoiceInventory/TimingControls` arrays (not null), `MaxInputBytes/MaxDurationMs>=0`, capability flags, `ConfidenceSemantics/RateLimitDimsJson/CostDimsJson/PrivacyClass/Region` required, `Version>=1`.
- `src/DubbingPlatform.Domain/Entities/ProviderRouteSnapshot.cs` — `ProjectId/ProcessingRunId/RouteConfigHash/CapabilityHash/PrivacyHash/CreatedAt`.
- `src/DubbingPlatform.Domain/Entities/PromptTemplate.cs` — `Name/Description/CreatedAt`.
- `src/DubbingPlatform.Domain/Entities/PromptTemplateVersion.cs` — `PromptTemplateId/Version>=1/SystemInstruction/TemplateBody/SafetySettingsJson/PromptHash/CreatedAt`.
- `src/DubbingPlatform.Domain/Entities/QualityResult.cs` — `ProjectId/ProcessingRunId/ScopeType/ScopeId/SegmentId?/QualityStatus/Code/Severity/Message/DetailsJson?/ArtifactId?/CreatedAt`.
- `src/DubbingPlatform.Domain/Entities/ReviewItem.cs` — `ProjectId/ProcessingRunId/ScopeType/ScopeId/SegmentId?/ReviewStatus/Reason/PayloadJson?/CreatedAt/UpdatedAt/ResolvedAt?` with `ResolvedAt>=CreatedAt`.
- `src/DubbingPlatform.Domain/Entities/ReviewDecision.cs` — `ReviewItemId/ReviewDecisionType/Reviewer/Reason/MetadataJson?/CreatedAt`.
- `src/DubbingPlatform.Domain/Entities/OutputAsset.cs` — `ProjectId/ProcessingRunId/ArtifactId/MediaKind/DurationMs>=0/Container/CreatedAt`; `MediaKind` restricted to Video/Audio with XML doc.
- `src/DubbingPlatform.Domain/Entities/ExportJob.cs` — `ProjectId/ProcessingRunId/ExportFormat/ExportJobStatus/ArtifactIdRef?/CompletenessJson?/IsPartial/CreatedAt/UpdatedAt`.
- `src/DubbingPlatform.Domain/Entities/ExportArtifact.cs` — `ExportJobId/ArtifactId/CreatedAt`.
- `src/DubbingPlatform.Domain/Entities/AuditEvent.cs` — Append-only `ProjectId?/Actor/Action/ResourceType/ResourceId/DetailsJson?/CreatedAt`, no UpdatedAt, XML doc noting immutability convention.
- `src/DubbingPlatform.Domain/Entities/IdempotencyRecord.cs` — `Endpoint/IdempotencyKey/RequestHash/State/ResponseStatus?/ResponseBody?/CreatedAt/ExpiresAt>=CreatedAt`; `State` restricted to Started/Succeeded/Failed with XML doc.
- `src/DubbingPlatform.Domain/Entities/CostReservation.cs` — `ProjectId/ProcessingRunId/SegmentId?/ProviderCapability/ReservedAmount>=0/ActualAmount>=0/Currency` (3-letter check)`/PriceTableVersion/State/CreatedAt`; `State` restricted to Reserved/Reconciled/Released with XML doc.
- `src/DubbingPlatform.Domain/Entities/QuotaUsage.cs` — `Dimension/Used>=0/Limit>=0/WindowStart/WindowEnd>=WindowStart` (no CreatedAt per spec).
- `src/DubbingPlatform.Domain/Entities/ProcessingPolicy.cs` — `ExternalProvidersAllowed/AllowedProviders` (not null)`/ResidencyConstraint?/SensitivePolicy/VoicePolicy/LocalInferenceAllowed/RetentionOverride?/CreatedAt/UpdatedAt`.
- `src/DubbingPlatform.Domain/Entities/RetentionHold.cs` — `ProjectId?/ArtifactId?/Reason/PlacedBy/PlacedAt/ReleasedAt?/IsActive` with `ReleasedAt>=PlacedAt` (no CreatedAt per spec).
- `src/DubbingPlatform.Domain/Entities/DeletionJob.cs` — `ProjectId?/Scope/Status/CreatedAt/CompletedAt?` with `Status` restricted to Pending/Running/Completed/Failed/Cancelled and XML doc.
- `src/DubbingPlatform.Domain/Entities/ProjectStatusProjection.cs` — Static `Project(ProcessingRunStatus, bool)` documenting projection (Running→Processing, Completed→Completed unless open reviews→ManualReviewRequired, Failed→Failed, Cancelling/Cancelled→Cancelling/Cancelled, ManualReviewRequired→ManualReviewRequired, Pending→Processing); unknown enum throws DomainException; enforcement deferred to Task 6 per spec.
- `tests/DubbingPlatform.UnitTests/Domain/ExecutionEntitiesTests.cs` — 4 tests with exact required names.
- `tests/DubbingPlatform.UnitTests/Domain/CoreEntitiesTests.cs` — Updated `All_Scoped_Entities_Have_TenantId` expected count 15→45 (15 core + 30 new); no test deleted.

## Decisions Made
- Scope lists 30 entity names but R1/R2/testing say "29": implemented all 30 listed names (under-implementing to hit "29" would violate R1's "exact fields" for the omitted type). `All_Execution_Entities_Have_TenantId` asserts an explicit 30-entry `Type[]` and `Assert.Equal(30, ...)` so the discrepancy is visible and intentional; documented here.
- `ProjectStatusProjection` placed in `Entities/` as `public static class` (excluded from entity counts since static classes are abstract+sealed; the `All_Scoped_Entities_Have_TenantId` reflection filter `IsClass && !IsAbstract` skips it, keeping the count at 45 = 15 + 30).
- `Pending → Processing` mapping chosen for the undocumented run status: a run that exists but has not started still means the project is in Processing; alternatives (Created/MediaReady) would incorrectly move the project backwards. `hasOpenRequiredReviews` only affects `Completed`, so a `ManualReviewRequired` run stays `ManualReviewRequired` even with no open reviews (explicit signal preserved).
- `ContentObject.ContentHash` + `Sha256Hex` implemented as two required non-empty strings: spec lists both fields verbatim, so both are kept even though they look duplicative; strict 64-hex is NOT enforced (per 004 precedent, hash fields require non-empty only; `ValueObjects/ContentHash.IsValidHex` remains the strict checker for future persistence validation in 006).
- String-union states (`UnitState`, `MediaKind`, `IdempotencyRecord.State`, `CostReservation.State`, `DeletionJob.Status`) are enforced in `Validate()` with `DomainException` plus XML-doc allowed sets: spec says "consumers must reject", and entity-level rejection is the earliest production-safe enforcement; valid values used in tests are unaffected.
- `ArtifactParent` additionally rejects `ChildArtifactId == ParentArtifactId` (self-lineage is never valid); no other cross-field rules added to avoid over-constraining future seeds.
- Entities without `CreatedAt` in spec (`QuotaUsage` uses `WindowStart/WindowEnd`; `RetentionHold` uses `PlacedAt/ReleasedAt?`) keep exactly the specified fields — no synthetic `CreatedAt` added, since R1 demands exact fields. All other entities follow the `Guid Id, Guid TenantId, DateTimeOffset CreatedAt (+UpdatedAt where mutable)` convention from instruction 1.
- Conventions match 004 exactly: `namespace DubbingPlatform.Domain.Entities`, `public sealed class`, `{ get; private set; }`, private parameterless ctor defaulting strings to `string.Empty` and arrays to `Array.Empty<...>()`, public full-arg ctor in task property order calling `public void Validate()` throwing `DubbingPlatform.Domain.Exceptions.DomainException`. No EF references added to Domain; no secrets; file-scoped namespaces, 4-space indent, LF.
- Shell: `default.shell` fails (`The system cannot find the path specified`); all commands ran via `default.execute` → `tools["claude-code"].PowerShell`, per 003/004 reports.

## Build/Test Results
- `dotnet build` (last 10 lines):
```
  DubbingPlatform.E2ETests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.E2ETests\bin\Debug\net10.0\DubbingPlatform.E2ETests.dll
  DubbingPlatform.UnitTests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.UnitTests\bin\Debug\net10.0\DubbingPlatform.UnitTests.dll
  DubbingPlatform.ContractTests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.ContractTests\bin\Debug\net10.0\DubbingPlatform.ContractTests.dll
  DubbingPlatform.IntegrationTests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.IntegrationTests\bin\Debug\net10.0\DubbingPlatform.IntegrationTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:18.74
```
- `dotnet test --filter FullyQualifiedName~ExecutionEntitiesTests` (last 10 lines):
```
A total of 1 test files matched the specified pattern.
A total of 1 test files matched the specified pattern.
No test matches the given testcase filter `FullyQualifiedName~ExecutionEntitiesTests` in C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.ContractTests\bin\Debug\net10.0\DubbingPlatform.ContractTests.dll
No test matches the given testcase filter `FullyQualifiedName~ExecutionEntitiesTests` in C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.IntegrationTests\bin\Debug\net10.0\DubbingPlatform.IntegrationTests.dll
No test matches the given testcase filter `FullyQualifiedName~ExecutionEntitiesTests` in C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.E2ETests\bin\Debug\net10.0\DubbingPlatform.E2ETests.dll



Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 379 ms - DubbingPlatform.UnitTests.dll (net10.0)
```
- Extra verification `dotnet test --filter FullyQualifiedName~CoreEntitiesTests` (last 3 lines):
```
Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 56 ms - DubbingPlatform.UnitTests.dll (net10.0)
```
- Extra verification `dotnet build --no-incremental /p:EnforceCodeStyleInBuild=true` (last 6 lines):
```
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:54.51
```

## Recommendations for Next Agent (006)
- Repo state: `DubbingPlatform.sln` (10 projects, `net10.0`, `TreatWarningsAsErrors true`, `EnforceCodeStyleInBuild true`, `AnalysisLevel latest`) builds clean with 0/0. Domain now has `Placeholder.cs` (untouched), `Enums/` (25 enums), `ValueObjects/` (10 records), `Entities/` (15 core + 30 execution = 45 sealed classes + `ProjectStatusProjection` static), `Exceptions/DomainException.cs`, `Identity/PublicIdMapper.cs`. `src/DubbingPlatform.Domain/DubbingPlatform.Domain.csproj` still has zero `PackageReference`/`ProjectReference` — Domain must stay dependency-free; 006 DbContext lives in Infrastructure, never in Domain.
- Shell gotcha: `default.shell` fails (`The system cannot find the path specified`); use `default.execute` with `tools["claude-code"].PowerShell({command, description})` (discover via `search({query:"PowerShell", namespace:"claude-code"})` if needed). CWD is already `C:\Users\fazeli\source\hobby\media-dub`. `make`/`docker`/`trivy` absent — validate with `dotnet build`, `dotnet build --no-incremental /p:EnforceCodeStyleInBuild=true`, `dotnet test --filter FullyQualifiedName~<Name>`.
- Naming/conventions: entities are `namespace DubbingPlatform.Domain.Entities`, `public sealed class <Name>` with `{ get; private set; }`, private parameterless ctor (`string.Empty` / `Array.Empty<...>()` defaults), public full-arg ctor in task-spec property order calling `public void Validate()` throwing `DubbingPlatform.Domain.Exceptions.DomainException`. Only usings are `DubbingPlatform.Domain.Enums`/`Exceptions` (System via ImplicitUsings). File-scoped namespaces, 4-space indent, LF, `insert_final_newline true`.
- Key APIs new in 005: `new StageExecution(id,tenantId,projectId,processingRunId,stageType,scopeType,scopeId,segmentId,attempt,status,leaseOwner,leaseToken,leaseTokenVersion,leaseExpiresAt,startedAt,completedAt,inputHash,configurationHash,executionSnapshotHash,outputArtifactIdsJson,errorCode,errorMessage,createdAt,updatedAt)`; `new ProviderExecution(...)` (30 args, `RequestHash` required); `new ArtifactParent(id,tenantId,childArtifactId,parentArtifactId,createdAt)` (rejects self-link); `ProjectStatusProjection.Project(ProcessingRunStatus,bool)`; `PublicIdMapper.PrefixFor<T>()` already maps `Artifact→art_`, `ContentObject→cnt_`, `StageExecution→exe_`, `ProviderExecution→prov_`, `QualityResult→qc_`, `ReviewItem→rev_`, `ExportJob→exp_` — new class names match those literals exactly, do not rename.
- Incomplete integration (006 scope): no `DbContext`/configs/migrations yet; stage identity unique constraint on `(ProcessingRunId,StageType,ScopeType,ScopeId,Attempt)` must be configured in 006 from `StageExecution` fields; lease fencing (`LeaseToken/LeaseTokenVersion/LeaseExpiresAt`) and conditional commit, RLS on `TenantId`, `ProjectStatusProjection` enforcement state machines, and SHA-256 hash strictness (`ContentObject.Sha256Hex` currently non-empty only) are all 006 work. `AuditEvent` is append-only by convention (no UpdatedAt/mutators) — 006 must not add update paths for it.
- Test helpers: `tests/DubbingPlatform.UnitTests/Domain/ExecutionEntitiesTests.cs` (`DubbingPlatform.UnitTests.Domain.ExecutionEntitiesTests`, fixed `Now = 2026-01-01`) has the 4 required tests — keep exact names; `All_Execution_Entities_Have_TenantId` uses an explicit 30-entry `Type[]` (not assembly scan) so it is immune to future entity additions. `CoreEntitiesTests.All_Scoped_Entities_Have_TenantId` now asserts 45 total entity types via assembly scan (excludes static `ProjectStatusProjection`); 006 must update that count only if it adds Domain entity types (it should not — DbContext goes in Infrastructure). Total UnitTests now 1 smoke + 6 + 5 + 4 = 16.
- Warnings: `AnalysisLevel latest`; no serialization ctors on exceptions; use `StringComparison.Ordinal` explicitly; validate `double.IsNaN` for all double inputs; no `DateTime.Now` in tests; no secrets/logging in Domain.
- Config: appsettings still 16 empty `{}` sections; Docker/compose/Makefile untouched; no new packages added.
