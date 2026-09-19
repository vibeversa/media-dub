# 010 — Messaging Contracts Report

## Status
COMPLETED

## Summary
Defined all 15 durable message contracts in `DubbingPlatform.Contracts.Messages`, each extending the abstract `IntegrationMessage` envelope with its exact payload fields, plus `MessageVersionPolicy` (CurrentVersion=1, additive-only + `_skipped` + `messaging.schema_mismatch_total` rule), frozen `QueueNames` constants for the 7 workload queues plus `_skipped`/`_error`, and shared camelCase tolerant `MessagingJson.Options`. Added `MessageContractTests` (common-fields reflection, no-JobId, version detection, round-trips with unknown-prop tolerance); filter run passes 4/4 and the full ContractTests suite passes 5/5.

## Files Created/Modified
- `src/DubbingPlatform.Contracts/Messages/IntegrationMessage.cs` — abstract base record with the 16 envelope fields from the spec code block (no `JobId`).
- `src/DubbingPlatform.Contracts/Messages/RunStarted.cs` — `+ string PipelineVersion, string ProviderRouteHash`.
- `src/DubbingPlatform.Contracts/Messages/RunCancelledRequested.cs` — `+ string Reason`.
- `src/DubbingPlatform.Contracts/Messages/StageWorkRequested.cs` — `+ string StageTypeRequired, string ScopeTypeRequired, string ScopeIdRequired, string? PayloadJson`.
- `src/DubbingPlatform.Contracts/Messages/StageCompleted.cs` — `+ string CompletedStageType, string[] OutputArtifactIds`.
- `src/DubbingPlatform.Contracts/Messages/StageFailed.cs` — `+ string FailedStageType, string ErrorCode, string ErrorMessage, bool IsRetryable`.
- `src/DubbingPlatform.Contracts/Messages/StageCancelled.cs` — `+ string CancelledStageType`.
- `src/DubbingPlatform.Contracts/Messages/StageReviewRequired.cs` — `+ string BlockedStageType, string ReviewItemId`.
- `src/DubbingPlatform.Contracts/Messages/ReviewResolved.cs` — `+ string ReviewItemId, string Decision`.
- `src/DubbingPlatform.Contracts/Messages/RunCompleted.cs` — `+ string OutputAssetId`.
- `src/DubbingPlatform.Contracts/Messages/RunFailed.cs` — `+ string ErrorCode, string ErrorMessage`.
- `src/DubbingPlatform.Contracts/Messages/RunCancelled.cs` — `+ string Reason`.
- `src/DubbingPlatform.Contracts/Messages/MediaUploaded.cs` — `+ string UploadSessionId, string StorageKey, string? ClientSha256`.
- `src/DubbingPlatform.Contracts/Messages/MediaValidated.cs` — `+ string MediaAssetId, bool IsValid`.
- `src/DubbingPlatform.Contracts/Messages/ExportJobRequested.cs` — `+ string ExportJobId, string Format`.
- `src/DubbingPlatform.Contracts/Messages/StageLeaseTimeout.cs` — `+ string LeaseToken, DateTimeOffset LeaseExpiresAt`.
- `src/DubbingPlatform.Contracts/Messages/MessageVersionPolicy.cs` — `CurrentVersion=1`, `IsSupported(v)=>v==1`, `SchemaMismatchMetricName="messaging.schema_mismatch_total"`, additive-only + `_skipped` rule docs.
- `src/DubbingPlatform.Contracts/Messages/QueueNames.cs` — frozen constants for `control.orchestration`, `media.preparation`, `media.render`, `ai.provider`, `ai.gpu`, `export`, `maintenance`, `_skipped`, `_error`.
- `src/DubbingPlatform.Contracts/Messages/MessagingJson.cs` — shared `JsonSerializerOptions` (`JsonSerializerDefaults.Web` + explicit `PropertyNameCaseInsensitive=true`): camelCase, tolerant reader.
- `tests/DubbingPlatform.ContractTests/Messaging/MessageContractTests.cs` — the 4 required tests (common-fields reflection incl. 15-type count, no-JobId, version detection, round-trips for RunStarted + StageWorkRequested incl. camelCase asserts and unknown-prop tolerance).

## Decisions Made
- Spec code block lists 16 envelope parameters while R2 says "17 common fields": implemented exactly the 16 from the authoritative code block and the reflection test asserts that exact 16-name set (no invented 17th field).
- R3 "No JobId anywhere" means no member literally named `JobId`: the only `JobId` substring match in the codebase is the explicitly required `ExportJobRequested.ExportJobId` payload field, which the spec mandates; the test asserts `DeclaredOnly` property/field names never equal `JobId` (Ordinal).
- Added `MessagingJson` helper (beyond the 3 named files) because instruction 5 mandates camelCase + tolerant JSON in production code, not just tests; it uses `JsonSerializerDefaults.Web` with `PropertyNameCaseInsensitive=true` set explicitly to match the spec text. Added `SchemaMismatchMetricName` const to `MessageVersionPolicy` so the Task 38 metric name lives in code, not just docs.
- Derived records carry full base parameter lists and forward to `base(...)` positionally, preserving exact names/types/order; no default values were added (spec shows none, producers pass `MessageVersionPolicy.CurrentVersion`).
- `Contracts` stays dependency-free (no new package refs; `System.Text.Json` is framework-only), per 001 decision. No bus/consumers/saga/outbox wiring added (Task 11 scope).

## Build/Test Results
- `dotnet build --nologo -v q` (last 5):
```
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:03.05
```
- `dotnet test --filter FullyQualifiedName~MessageContractTests --nologo -v q` (last 6):
```
A total of 1 test files matched the specified pattern.
A total of 1 test files matched the specified pattern.
A total of 1 test files matched the specified pattern.


Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 582 ms - DubbingPlatform.ContractTests.dll (net10.0)
```
- `dotnet build --no-incremental /p:EnforceCodeStyleInBuild=true --nologo -v q` (last 5):
```
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:12.89
```
- `dotnet test tests/DubbingPlatform.ContractTests/DubbingPlatform.ContractTests.csproj --nologo -v q` (last 3):
```
Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 431 ms - DubbingPlatform.ContractTests.dll (net10.0)
```

## Recommendations for Next Agent (011)
- Repo state: builds 0/0 incl. lint (`TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`, `AnalysisLevel latest`). Contracts now has 19 files under `src/DubbingPlatform.Contracts/Messages/` (base + 15 messages + `MessageVersionPolicy` + `QueueNames` + `MessagingJson`); `Placeholder.cs` retained. No bus/consumers/saga/outbox wiring exists yet — that is 011 scope. `AppDbContext` already scaffolds MassTransit outbox/inbox tables (`OutboxMessage`/`OutboxState`/`InboxState` via `AddTransactionalOutboxEntities()` + `AddInboxStateEntity()`).
- Key APIs for 011: `IntegrationMessage` (abstract record, 16 envelope fields, `SchemaVersion` must equal `MessageVersionPolicy.CurrentVersion`); `MessageVersionPolicy.IsSupported(int)`, `MessageVersionPolicy.SchemaMismatchMetricName` (`"messaging.schema_mismatch_total"`); `QueueNames.*` (frozen strings incl. `Skipped="_skipped"`, `Error="_error"`); `MessagingJson.Options` (use for all message serde). Correlation ID is a message-header concern at bus level — envelope already carries `CorrelationId`.
- Conventions: file-scoped namespaces, 4-space/LF, `StringComparison.Ordinal(IgnoreCase)` everywhere, `ConfigureAwait(false)` in library code; test project uses `Xunit` via ImplicitUsings (`Using Include="Xunit"` in csproj) so no `using Xunit;` needed; `CultureInfo` needs `using System.Globalization` (ImplicitUsings does not cover it — first build failed on this, fixed).
- Gotchas: shell is PowerShell (`Select-Object`, no `||`, no `tail`/`grep` — use `Select-Object -Last/-First`); `Contracts` must stay dependency-free (no external packages); R2/R3 counting notes above (16 vs "17", `ExportJobId` substring); 011 must implement `_skipped` routing + `messaging.schema_mismatch_total` emission on `IsSupported==false` (contract side only detects, does not throw at deserialization — tolerant reader already proven by test).
- Config keys: `Transport:Provider` (`InMemory` fast / `RabbitMq` full, `Messaging:Transport` legacy fallback per `HealthRegistration.IsFullProfile`); queue names map to workload classes (`control.orchestration`, `media.preparation`, `media.render`, `ai.provider`, `ai.gpu`, `export`, `maintenance`), not per-micro-stage.
