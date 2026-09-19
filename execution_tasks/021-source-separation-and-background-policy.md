# Task 21 — Source Separation and Background Policy

## Goal

Implement optional source separation with normalized confidence, safe fallback, and auditable decisions.

## Context

Binding: policy SourceSeparationPolicy Disabled/Enabled/Auto per project settings (`settings.sourceSeparation: disabled|enabled|auto`, default disabled). If disabled → skip (unit Skipped), select canonical audio, record skip reason. If enabled → invoke ISourceSeparationProvider, store dialogue (+background if available), record provider confidence. Normalize confidence via policy layer (never compare raw cross-provider); acceptance threshold normalized 0.70 default (config `Media:SeparationThreshold=0.70`). Below → fallback canonical + warning + reason; above → separated dialogue for speech + background for mixing. Record provider + stage executions with artifact refs; fallback explicit queryable.

## Starting State

Canonical audio artifact exists; AudioPreparation completes. No SourceSeparationWorker/Service. Provider interfaces + mocks + resolvers exist.

## Scope

Must implement: SourceSeparationService + Worker, policy handling, normalization, fallback, artifacts. Must not implement: VAD and later.

## Instructions

1. Create `src/DubbingPlatform.Application/Services/SourceSeparationService.cs`: `Task<SeparationDecision> DecideAsync(tenant,project,run,canonicalArtifactId,ct)` reading `ProcessingPolicy` + project SettingsJson `sourceSeparation` + `separationThreshold`; if Disabled → return Skipped; else resolve provider via ProviderResolver(SourceSeparation) + compatibility check; call `SeparateAsync` with canonical bytes ref; normalize: `normalized = (raw - providerMin)/(providerMax-providerMin)` using descriptor ConfidenceSemantics (parse `min-max`, default 0-1); compare to threshold.
2. Artifacts: on success above threshold → publish DialogueStem (+BackgroundStem if present) via ArtifactService with parents=[canonical], provider/model hashes; on fallback → no new artifacts, record `FallbackReason` in StageExecution ErrorMessage + QualityResult warning (severity Warning, code SEPARATION_FALLBACK).
3. Create `src/DubbingPlatform.Workers/Consumers/SourceSeparationWorker.cs` : BaseConsumer<StageWorkRequested> (stage SourceSeparation, scope Project/Run, queue media.preparation): claim, call service, Complete with unit state Completed (used separated) or Skipped (disabled or fallback? Decision: fallback due to low confidence = Completed with fallback flag + warning, not Skipped; disabled = Skipped — document), publish StageCompleted with selected audio artifact id in payload.
4. Record ProviderExecution for every call (even fallback) with outcome + confidence; config `Media:SeparationThreshold` default 0.70.
5. Downstream contract: later stages read `SelectedDialogueArtifactId` from StageOutput (store in StageExecution OutputArtifactIdsJson first entry = selected).

## Requirements

- R1: Disabled → Skipped + canonical selected + reason.
- R2: Low normalized (<0.70) → fallback canonical + warning + reason.
- R3: High (≥0.70) → separated dialogue + background stored.
- R4: Provider execution recorded always.
- R5: Fallback queryable (stage metadata + QC warning).

## Edge Cases and Error Handling

- Provider transient → retry within logical budget; permanent → fallback canonical + warning (do not fail run unless policy `failOnSeparationError=true` default false).
- Missing background stem → proceed with dialogue only, background=null for mixer (mixer mutes/ducks accordingly).
- Lease lost → discard provider output, no artifact commit.
- Cancelled run → abort before commit.

## Security and Safety Requirements

- Tenant-scoped artifacts; no secrets in logs; provider bytes validated via FFprobe before selection (must be decodable audio).

## Testing

Create `tests/DubbingPlatform.UnitTests/Media/SeparationTests.cs` + integration `SeparationWorkerTests.cs`: `Disabled_Skips`, `Low_Confidence_Fallback`, `High_Confidence_Selects`, `Warning_Recorded`, `Execution_Recorded` (mock provider with configurable confidence 0.5/0.9).

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~SeparationTests
dotnet test --filter FullyQualifiedName~SeparationWorkerTests
```

## Completion Criteria

- Separation optional/safe/auditable; tests pass.

## Traceability

- Plan Section 10 all actions; Functional checklist separation fallback; Error checklist separation fallback explicit.
