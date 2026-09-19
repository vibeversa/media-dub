# Task 23 — Diarization and Speaker Identity

## Goal

Map segments to stable project-scoped speakers with provenance and retry-safe fallback.

## Context

Binding: DiarizationWorker invokes provider, stores artifact, maps provider labels → internal Speaker IDs (stable key, display name, first/last appearance, method/version, confidence, label provenance). Mapping is derived data with history preserved for reruns. Permanent failure → single-speaker fallback if policy `diarization.fallbackToSingleSpeaker=true` (default true) + warning + reason; else fail stage. Publish next stage.

## Starting State

Segments exist with null SpeakerId. No DiarizationWorker/Service. Speaker entities + provider interfaces exist.

## Scope

Must implement: DiarizationWorker/Service, speaker persistence, mapping history, fallback. Must not implement: transcription and later.

## Instructions

1. Create `src/DubbingPlatform.Application/Services/DiarizationService.cs`: `MapAsync(tenant,project,run,segments,providerLabels)` where provider returns `List<DiarLabel { SegmentId, Label, Confidence }>`; for each distinct Label create/find Speaker by SpeakerKey=`$"{projectId}:{label}"` hash → Speaker record (DisplayName=`Speaker {n}`, First/LastAppearance from segment times, MappingMethod=`provider:{provider}`, MappingVersion=`1`, Confidence avg, ProviderLabel stored). Update SpeechSegment.SpeakerId. Preserve history: never delete Speakers on rerun; new run creates new mappings but old rows remain (query by RunId via segments).
2. Create `src/DubbingPlatform.Workers/Consumers/DiarizationWorker.cs` : BaseConsumer<StageWorkRequested> (Diarization, scope Run, queue ai.provider): resolve IDiarizationProvider, call, store artifact type DiarizationMap (JSON label map + schema v1, parent=segments artifact), call MapAsync, Complete. On ProviderPermanentFailure and fallback allowed → create single Speaker (key `single`, DisplayName `Single Speaker`) assign all segments, store warning QualityResult (severity Warning, code DIARIZATION_FALLBACK) + FallbackReason in stage metadata, Complete (not Fail). If fallback disallowed → Fail with PROVIDER_FAILED.
3. Config: `Diarization: { FallbackToSingleSpeaker=true }` in MediaOptions? Decision: new `DiarizationOptions { bool FallbackToSingleSpeaker=true; }` bound from `Media:Diarization` — document.

## Requirements

- R1: Same provider label → same SpeakerId consistently.
- R2: Distinct labels → distinct IDs.
- R3: Provenance stored (method/version/label/confidence).
- R4: Fallback warning + reason when used.
- R5: History preserved.

## Edge Cases and Error Handling

- Empty segments → skip (no speakers, Complete).
- Provider timeout/transient → retry within budget.
- Conflicting labels across retries → new mapping version, old preserved.
- Cancelled run → abort before commit.

## Security and Safety Requirements

- Tenant-scoped speakers; no voice biometrics logged; no secrets.

## Testing

Create `tests/DubbingPlatform.UnitTests/Media/DiarizationTests.cs`: `Same_Label_Same_Speaker`, `Distinct_Labels_Distinct`, `Fallback_Warning`, `Provenance_Stored`, `History_Preserved_On_Rerun`.

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~DiarizationTests
```

## Completion Criteria

- Diarization + speakers + fallback work; tests pass.

## Traceability

- Plan Section 12; Functional checklist diarization stable IDs.
