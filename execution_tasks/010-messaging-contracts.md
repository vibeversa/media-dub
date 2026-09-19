# Task 10 — Messaging Contracts

## Goal

Define all durable message contracts with exact fields, versioning policy, and queue taxonomy so producers and consumers share stable schemas.

## Context

Binding: at-least-once delivery, no exactly-once; idempotent consumers + durable execution records mandatory; external provider work at-least-once + reconciliation; schema additive-only, tolerant readers, unsupported versions → `_skipped` + metric. Queues by workload class (not per micro-stage): control.orchestration, media.preparation, media.render, ai.provider, ai.gpu, export, maintenance. Correlation ID propagated via message headers. Stage scopes run/project/speaker/window/segment. Common fields on every message.

## Starting State

Contracts classlib exists empty. No message types. MassTransit not yet configured. DbContext has outbox/inbox tables scaffolded.

## Scope

Must implement: 15 message contracts + base envelope + version policy + queue constants. Must not implement: MassTransit bus config, consumers, saga, outbox wiring.

## Instructions

1. Create `src/DubbingPlatform.Contracts/Messages/` namespace `DubbingPlatform.Contracts.Messages`. Base record:
   ```csharp
   public abstract record IntegrationMessage(
     Guid MessageId, string CorrelationId, Guid TenantId, Guid ProjectId,
     Guid ProcessingRunId, Guid? StageExecutionId, string? StageType, string? ScopeType,
     string? ScopeId, Guid? SegmentId, int SchemaVersion, DateTimeOffset CreatedAt,
     int Attempt, string? InputHash, string? ConfigurationHash, string? ExecutionSnapshotHash);
   ```
   SchemaVersion current = 1. No `JobId` field (explicitly removed).
2. Exact messages (each extends IntegrationMessage with payload):
   - `RunStarted : IntegrationMessage` + `string PipelineVersion, string ProviderRouteHash`
   - `RunCancelledRequested` + `string Reason`
   - `StageWorkRequested` + `string StageTypeRequired, string ScopeTypeRequired, string ScopeIdRequired, string? PayloadJson`
   - `StageCompleted` + `string CompletedStageType, string[] OutputArtifactIds`
   - `StageFailed` + `string FailedStageType, string ErrorCode, string ErrorMessage, bool IsRetryable`
   - `StageCancelled` + `string CancelledStageType`
   - `StageReviewRequired` + `string BlockedStageType, string ReviewItemId`
   - `ReviewResolved` + `string ReviewItemId, string Decision`
   - `RunCompleted` + `string OutputAssetId`
   - `RunFailed` + `string ErrorCode, string ErrorMessage`
   - `RunCancelled` + `string Reason`
   - `MediaUploaded` + `string UploadSessionId, string StorageKey, string? ClientSha256`
   - `MediaValidated` + `string MediaAssetId, bool IsValid`
   - `ExportJobRequested` + `string ExportJobId, string Format`
   - `StageLeaseTimeout` + `string LeaseToken, DateTimeOffset LeaseExpiresAt`
3. Create `MessageVersionPolicy.cs`: `const int CurrentVersion=1; static bool IsSupported(int v)=>v==1;` tolerant reader: unknown JSON props ignored (System.Text.Json default); unsupported → consumer moves to `_skipped` + emits `messaging.schema_mismatch_total` (metric name documented for Task 38).
4. Create `QueueNames.cs`: constants `ControlOrchestration="control.orchestration", MediaPreparation="media.preparation", MediaRender="media.render", AiProvider="ai.provider", AiGpu="ai.gpu", Export="export", Maintenance="maintenance", Skipped="_skipped", Error="_error"`.
5. JSON: camelCase, tolerant (`JsonSerializerOptions { PropertyNameCaseInsensitive=true }`).

## Requirements

- R1: All 15 messages exist with exact extra fields.
- R2: Every message carries the 17 common fields.
- R3: No JobId anywhere.
- R4: Queue constants exact.
- R5: Version policy additive-only + _skipped rule documented.

## Edge Cases and Error Handling

- Missing TenantId/ProjectId/RunId: consumer must reject to _skipped (defined in Task 11, contract supports null-check).
- Unknown SchemaVersion: must not throw deserialization; handled as skip.
- Null StageType where required: validation error, not silent default.

## Security and Safety Requirements

- No secrets in messages. TenantId required; cross-tenant validation in consumers. Message size <256KB (payload JSON refs via artifacts, not inline bytes).

## Testing

Create `tests/DubbingPlatform.ContractTests/Messaging/MessageContractTests.cs`:
- `All_Messages_Have_Common_Fields` (reflection)
- `No_JobId_Field_Exists`
- `Unsupported_Version_Is_Detected` (IsSupported(999)==false)
- `Serialization_RoundTrips` for RunStarted + StageWorkRequested.

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~MessageContractTests
```

## Completion Criteria

- Contracts compile, tests pass, queue names frozen.

## Traceability

- Plan Section 4 actions 1–7; Assumptions 30–31,62; Tests checklist outbox/inbox (contract part).
