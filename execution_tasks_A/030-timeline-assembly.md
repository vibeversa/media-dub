# Task 30 — Timeline Assembly

## Goal

Place generated audio on the source timeline preserving silence and intentional overlaps with integrity checks.

## Context

Binding: loads segments + selected audio + source timeline + background + overlap relations. Each generated segment at assigned start. Preserve valid silence + intentional overlaps via relations. Detect invalid overlaps + overflow. Persist JSON timeline artifact v1. Record stage execution. Run/project scope.

## Starting State

Timing-optimized selected audio per segment + overlap relations + source duration exist. No TimelineAssemblerWorker/Service.

## Scope

Must implement: TimelineAssemblyService/Worker, placement, validation, artifact. Must not implement: mixing/render.

## Instructions

1. Create `src/DubbingPlatform.Application/Services/TimelineAssemblyService.cs`: `AssembleAsync(tenant,project,run,ct)`: load segments ordered Sequence + selected GeneratedAudioArtifact durations + source DurationMs + background artifact id + SegmentOverlaps; build `Timeline { string SchemaVersion="1"; string RunId; List<Entry> Entries; } Entry { string SegmentId; int StartMs; int DurationMs; string AudioArtifactId; string? OverlapGroupId; }`; place at segment StartMs; preserve silence (gaps remain silent); preserve intentional overlaps (overlapping entries kept where OverlapGroup exists); detect invalid overlaps (overlap without group → PIPELINE_INVARIANT_VIOLATION fail); detect overflow (any End>sourceDuration+100ms tolerance → fail); persist artifact type Timeline (JSON + schema v1, parents=selected audios) via ArtifactService.
2. Create `TimelineAssemblerWorker : BaseConsumer<StageWorkRequested>` (TimelineAssembly, scope Run, queue control.orchestration): claim, call service, Complete + publish StageCompleted.
3. Determinism: entries sorted by StartMs then Sequence; JSON canonical (sorted keys).

## Requirements

- R1: Placement matches artifact.
- R2: Intentional overlaps preserved.
- R3: Invalid overlaps blocked.
- R4: Overflow detected.

## Edge Cases and Error Handling

- Missing selected audio for segment → fail with ARTIFACT_UNAVAILABLE unless segment Skipped (then omit + mark skipped in timeline metadata).
- Overflow → BLOCKED, no artifact commit.
- Duplicate assembly (retry) → idempotent (overwrite Pending, keep Committed history via new Artifact row).

## Security and Safety Requirements

- Tenant-scoped; timeline ints only; no secrets.

## Testing

Create `tests/DubbingPlatform.UnitTests/Pipeline/TimelineTests.cs`: `Placement_Matches`, `Intentional_Preserved`, `Invalid_Blocked`, `Overflow_Detected`.

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~TimelineTests
```

## Completion Criteria

- Deterministic timeline artifact + checks; tests pass.

## Traceability

- Plan Section 19; Functional checklist timeline preserves timing; Error checklist overlaps/overflow.
