# 042 — Optional Video Intelligence LipSync Report

## Status
COMPLETED

## Summary
Implemented feature-flagged video-intelligence and lip-sync enrichment as isolated post-render jobs on `ai.gpu`. Added `EnrichmentRequested` message, `ArtifactType.Enrichment`, `EnrichmentGate` dual-gate, `EnrichmentPayload` builders/validators, `EnrichmentMetrics.enrichment.failed`, `ILipSyncProvider` + `MockLipSyncProvider` (0.85), `VideoIntelligenceWorker` and `LipSyncWorker` (`BaseConsumer<EnrichmentRequested>`), render fan-out, and core-download isolation. Added hermetic `EnrichmentTests` (3/3 pass); full Unit (310) and Contract (39) suites green with `dotnet build` 0/0.

## Files Created/Modified
- `src/DubbingPlatform.Contracts/Messages/EnrichmentRequested.cs` — new: `IntegrationMessage + string Kind`, documents `ai.gpu` routing and new-run-only flag semantics.
- `src/DubbingPlatform.Contracts/Messages/EnrichmentKinds.cs` — new: frozen `VideoIntelligence|LipSync` constants + `IsKnown`/`All`.
- `src/DubbingPlatform.Domain/Enums/ArtifactType.cs` — modified: added `Enrichment` member (string conversion, additive, intermediate 30d retention via existing `RetentionService`).
- `src/DubbingPlatform.Application/Abstractions/Providers/Dtos/LipSyncDtos.cs` — new: `LipSyncRequest`/`LipSyncResponse` (score 0.85 success).
- `src/DubbingPlatform.Application/Abstractions/Providers/ILipSyncProvider.cs` — new: `AnalyzeAsync` interface.
- `src/DubbingPlatform.Application/Enrichment/EnrichmentGate.cs` — new: tolerant `settings.enrichment` parsing + dual-gate `ShouldRequest*`/`RequestedKinds`/`BuildRequests`.
- `src/DubbingPlatform.Application/Enrichment/EnrichmentPayload.cs` — new: v1 JSON builders + `IsLipSyncOutputValid` + `PreviewToleranceMs` + `EnrichmentOutcome`.
- `src/DubbingPlatform.Infrastructure/Observability/EnrichmentMetrics.cs` — new: `enrichment.failed{kind}` counter on `dubbing-platform` meter.
- `src/DubbingPlatform.Infrastructure/Providers/Mock/MockLipSyncProvider.cs` — new: deterministic mock honoring `MockBehaviorEvaluator` scenarios.
- `src/DubbingPlatform.Infrastructure/Providers/Mock/README.md` — modified: 9 capabilities, added `LipSync` to capability list + score doc.
- `src/DubbingPlatform.Infrastructure/Providers/ProviderRegistration.cs` — modified: registered `ILipSyncProvider` mock singleton.
- `src/DubbingPlatform.Workers/Consumers/VideoIntelligenceWorker.cs` — new: `BaseConsumer<EnrichmentRequested>` Kind filter, double-gate, provider call, `Enrichment` artifact + separate `Video/mp4` `OutputAsset`, `ProviderExecution` record, isolated catch.
- `src/DubbingPlatform.Workers/Consumers/LipSyncWorker.cs` — new: same base, `ILipSyncProvider` + `IFFprobeService` post-validation, `lipSyncScore` metadata, isolated catch.
- `src/DubbingPlatform.Workers/Consumers/RenderWorker.cs` — modified: optional `IOptions<FeatureOptions>` ctor param (backward compat), `MaybePublishEnrichmentAsync` after `RunCompleted` (best-effort, dual-gate, publishes to bus for `ai.gpu`).
- `src/DubbingPlatform.Workers/Program.cs` — modified: registered both enrichment workers via `configureExtra`.
- `src/DubbingPlatform.Api/Controllers/OutputController.cs` — modified: download now joins `Artifact` and filters `ArtifactType.RenderedOutput` so enrichment `OutputAsset`s never shadow core download.
- `tests/DubbingPlatform.IntegrationTests/Enrichment/EnrichmentTests.cs` — new: `Disabled_Core_Completes_Identically`, `Enabled_Mock_Produces_Artifacts`, `Failure_Does_Not_Fail_Core` (hermetic, mocks only).
- `tests/DubbingPlatform.ContractTests/Messaging/MessageContractTests.cs` — modified: 16→17 message count with Task 042 comment.
- `tests/DubbingPlatform.UnitTests/Domain/EnumAndValueObjectTests.cs` — modified: added `Enrichment` to `ArtifactType` expectation.

## Decisions Made
- **ArtifactType.Enrichment via code-first string conversion, StageType.Render for lineage**: avoids new `StageType` (would perturb `StageGraph` DAG) and keeps core code tolerant; enrichment artifacts use `StageType.Render` as the out-of-band parent stage, documented in workers.
- **No new ProviderCapability for LipSync**: `CostService.Estimate` has a catch-all but routing/descriptors assume known capabilities; lip-sync records metadata in `artifacts.metadata_json` (`lipSyncScore`) instead of a `ProviderExecution` row, while video records full `ProviderExecution` (`Capability=VideoIntelligence`). Keeps change minimal and satisfies R5.
- **Workers as BaseConsumer<EnrichmentRequested> with null execution**: `BaseConsumer.ResolveStageIdentity` returns null for enrichment (no stage identity), so no lease claiming; `ShouldProcess` filters `Kind` pre-claim for shared `ai.gpu` endpoint, mirroring `ai.provider` sharing semantics.
- **Isolation swallows all except cancellation**: `catch (Exception)` → `EnrichmentMetrics.Failed(kind)` + warning log (ids only) + return; `OperationCanceledException` rethrown to respect shutdown. Never publishes `RunFailed`, never touches run status or core asset.
- **Mock scores pinned to existing behavior**: video mock stays 0.88 (existing `MockVideoIntelligenceProvider`), lip-sync mock 0.85/0.35; tests assert `>=0.8` and `==0.85` respectively rather than forcing video to 0.9 to avoid breaking `MockDeterminismTests`.
- **Download filtering is required isolation**: `OutputController.Download` previously picked latest `OutputAsset`; without filtering, a later enrichment preview would shadow core. Filter to `ArtifactType.RenderedOutput` preserves identical behavior when disabled and keeps core download stable when enabled.
- **No migration**: `ArtifactType` persists as string (`HasConversion<string>`), so the new enum value needs no schema migration; verified `AppDbContextModelSnapshot` untouched.

## Build/Test Results
- `dotnet build --nologo -v q` (last 4):
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:17.34
```
- `dotnet test --filter "FullyQualifiedName~EnrichmentTests" --nologo -v q` (last 1):
```
Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3, Duration: 604 ms - DubbingPlatform.IntegrationTests.dll (net10.0)
```
- With flags true (`$env:Features__VideoIntelligenceEnabled="true"; $env:Features__LipSyncEnabled="true"; dotnet test --filter "FullyQualifiedName~EnrichmentTests"`):
```
Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3, Duration: 461 ms - DubbingPlatform.IntegrationTests.dll (net10.0)
```
- `dotnet test --filter "FullyQualifiedName~MessageContractTests"`:
```
Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 338 ms - DubbingPlatform.ContractTests.dll (net10.0)
```
- `dotnet test --filter "FullyQualifiedName~EnumAndValueObjectTests"`:
```
Passed!  - Failed:     0, Passed:     6, Skipped:     0, Total:     6, Duration: 122 ms - DubbingPlatform.UnitTests.dll (net10.0)
```
- Extra: `dotnet test tests/DubbingPlatform.UnitTests` 310/310 passed (~4s); `dotnet test tests/DubbingPlatform.ContractTests` 39/39 passed (~25s).

## Recommendations for Next Agent (043)
- Repo state: builds 0/0 (`TreatWarningsAsErrors`). Task 042 added 2 workers + 1 message + 1 enum member + gate/payload/metrics + lip-sync provider/mock + render fan-out + download filter. Not a git repo. No Docker/kubectl on host; hermetic tests pass, Docker-gated tests skip per convention.
- Key files for 043 (optional local/GPU sidecar, disabled by default): `src/DubbingPlatform.Application/Options/LocalInferenceOptions.cs` (check `Device=cuda` default on gpu worker), `src/DubbingPlatform.Infrastructure/Providers/LocalInference/LocalInferenceProvider.cs` (`HttpClientName` + mTLS handler in `ProviderRegistration.cs:81-114`), `src/DubbingPlatform.Application/Abstractions/Providers/ILocalInferenceProvider.cs`, `deploy/k8s/workers-gpu.yaml` (0-base GPU pool), `src/DubbingPlatform.Workers/Program.cs:192-213` (worker registration pattern — add sidecar consumer via `configureExtra`), `src/DubbingPlatform.Infrastructure/Messaging/MassTransitConfig.cs:200-207` (`ai.gpu` endpoint sharing via `ShouldProcess`).
- Gotchas: NEVER run two `dotnet test` concurrently (MSBuild/testhost contention). Keep `Features__LocalInferenceEnabled=false` default; do not rename frozen queues in `src/DubbingPlatform.Contracts/Messages/QueueNames.cs` or instruments in `PlatformMetrics.cs`/`EnrichmentMetrics.cs`. New messages must bump `MessageContractTests.cs:48` count (now 17) and new enum members must update `EnumAndValueObjectTests.cs:24`. `ArtifactType` is string-converted (no migration); `StageType` additions would perturb `StageGraph` — prefer `StageType.Render` lineage for out-of-band work.
- Conventions: error codes via `src/DubbingPlatform.Application/Errors/ErrorCodes.cs` + `ErrorCodeException` (never raw HTTP in services); options with `SectionName` const + `IValidateOptions<>`; `ArgumentList` (list<string>) for all FFmpeg/ffprobe invocations via `ProcessRunner.RunAsync`, never shell concat; pod security `runAsUser:1654` + readOnlyRootFilesystem; secrets via config/`CHANGE_ME`, never hardcoded.
- Config keys for 043: `Features__LocalInferenceEnabled` (default false), `LocalInference__Device`, `LocalInference__ClientCertificatePath` (mTLS), `Media__MaxConcurrentMediaJobs=2` (enrichment/sidecar must not bypass), `Providers__Mock:Behaviors:LipSync:Scenario` (new mock capability name `LipSync`, case-insensitive).
- Test helpers: hermetic mocks via `TestFixtureBase.MockSuccessOptions()` / `MockOptions(scenario)`; `MockBehaviorOptions.Success|RateLimited|Timeout` for isolation tests; `EnrichmentGate.RequestedKinds(features, settingsJson)` + `EnrichmentPayload.IsLipSyncOutputValid` are pure and reusable; `ConfigurationHashCalculator.Compute(object)` for request/response hashes; `ProviderExecutionRecorder.BuildIdempotencyKey(runId, stage, scope, attempt)` format `{run:N}:{stage}:{scope}:{attempt}`.
