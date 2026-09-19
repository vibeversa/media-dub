# Task 42 — [OPTIONAL] Video Intelligence and Lip Sync

## Goal

[OPTIONAL POST-MVP] Provide feature-flagged video-intelligence and lip-sync enrichment that never blocks or corrupts core dubbing.

## Context

Binding (optional, disabled by default): VideoIntelligence (face detect/track, active-speaker, face-to-speaker assoc, confidences) + LipSync (lip analysis, score, optional mouth transform + post-validation) as enrichment jobs after core render when requested. Flags `Features:VideoIntelligenceEnabled=false`, `Features:LipSyncEnabled=false` default false. Failures never fail core; outputs separate OutputAssets; core completion independent; provider/model metadata recorded; isolated-failure tests.

## Starting State

Core pipeline Completed + render + observability + production hardening exist. Provider IVideoIntelligenceProvider interface exists; no VideoIntelligenceWorker/LipSyncWorker. FeatureOptions has both flags false.

## Scope

Must implement (only when flags true): VideoIntelligenceWorker, LipSyncWorker, enrichment artifacts/assets, flag gating, isolation. Must not modify mandatory completion criteria; must not change core DAG ordering (enrichment after render, out-of-band).

## Instructions

1. Gate: in saga/render completion, if flags false → skip entirely (no jobs, no artifacts, core completes identically to Task 33). If true and requested per project (`settings.enrichment: {videoIntelligence: true, lipSync: true}`) → after RunCompleted publish `EnrichmentRequested` (new message defined here: `EnrichmentRequested : IntegrationMessage + string Kind` Kind in VideoIntelligence|LipSync) to queue ai.gpu (video) — document addition.
2. Create `VideoIntelligenceWorker : BaseConsumer<EnrichmentRequested>` (Kind==VideoIntelligence): call IVideoIntelligenceProvider (mock default returns 1 face + active speaker + assoc confidence 0.9), persist artifact type VideoIntelligence? Decision: use ArtifactType `Segments`? No — add `EnrichmentJson` stored as Artifact with Type=Segments? Better: reuse `ArtifactType.Transcript`? Decision documented: store as Artifact Type=`Segments` is wrong; create new ArtifactType member `Enrichment` via code-first addition (allowed for optional; core code ignores unknown types tolerantly) + separate OutputAsset (MediaKind Video, Container mp4) for annotated preview; record ProviderExecution.
3. Create `LipSyncWorker : BaseConsumer<EnrichmentRequested>` (Kind==LipSync): analyze lip movement (mock score 0.85), optional transform (FFmpeg crop/scale mouth region — mock copies video + metadata `lipSyncScore`), post-validate via FFprobe (decodable + duration within tolerance) else mark enrichment Failed without touching core Run status; separate OutputAsset; record metadata.
4. Isolation: try/catch all enrichment errors → log + metric `enrichment.failed{kind}` + mark enrichment job Failed; never publish RunFailed, never modify core OutputAsset or run status; core download unaffected.
5. Config: flags default false; per-project opt-in requires flag true AND settings true (both gates).
6. Tests: `EnrichmentTests.cs`: `Disabled_Core_Completes_Identically` (flags false → no enrichment artifacts), `Enabled_Mock_Produces_Artifacts`, `Failure_Does_Not_Fail_Core`.

## Requirements

- R1: Disabled → core identical, zero enrichment side effects.
- R2: Enabled mock → artifacts + separate assets.
- R3: Failure isolated (core stays Completed).
- R4: Flags default false.
- R5: Metadata recorded.

## Edge Cases and Error Handling

- Enrichment provider timeout → enrichment Failed, core Completed.
- Invalid video for face detect → enrichment Failed + warning, core unaffected.
- Flag toggled mid-run → takes effect only for new runs (document).

## Security and Safety Requirements

- Face data tenant-scoped, retained per intermediate 30d; no cross-tenant; no biometric export without consent; flags prevent accidental PII processing.

## Testing

Create `tests/DubbingPlatform.IntegrationTests/Enrichment/EnrichmentTests.cs` as above (mocks).

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~EnrichmentTests
```

With flags false then true (`Features__VideoIntelligenceEnabled=true Features__LipSyncEnabled=true`).

## Completion Criteria

- Optional enrichment gated/isolated; tests pass; core criteria unchanged.

## Traceability

- Plan Section 31; Feature flags; Completion criteria extensibility (enrichment part).
