# Task 22 — VAD and Segment Builder

## Goal

Detect speech regions and build canonical segments with stable IDs, sequence ordering, overlap relations, and timeline validation.

## Context

Binding: VadWorker invokes IVadProvider, persists VAD regions artifact. SegmentBuilder merges short pauses (<300ms default), splits long speech (>30s default), respects speaker boundaries when available, preserves intentional overlaps, aligns to word timestamps when available. Stable segment IDs (deterministic Guid from run+sequence), sequence by start time. Segments fields start/end/duration/sequence/status/speaker-placeholder. Overlap via OverlapGroup + SegmentOverlap (segment/group/relation/order/overlap start-end); never single OverlapGroupId field only. Timeline validation: no negative start, end>start, duration within media, no invalid overlaps, no overflow. Persist VAD+segment artifacts. Enforce MaxSegmentCount quota (default 2000). Config `Segment: { MergePauseMs=300, MaxSegmentMs=30000, MaxSegments=2000 }`.

## Starting State

Selected dialogue audio (canonical or separated) exists. No VadWorker/SegmentBuilder. SpeechSegment/Overlap entities + DbContext exist.

## Scope

Must implement: VadWorker, SegmentBuilderWorker/Service, overlap persistence, validation, quota. Must not implement: diarization and later.

## Instructions

1. Create `src/DubbingPlatform.Workers/Consumers/VadWorker.cs` : BaseConsumer<StageWorkRequested> (Vad, scope Run, queue ai.provider): resolve IVadProvider, call DetectAsync(dialogue artifact, language), persist artifact type VadRegions (JSON regions + schema v1) with parent=dialogue, Complete.
2. Create `src/DubbingPlatform.Application/Services/SegmentBuilderService.cs`: `BuildAsync(run, vadRegions, mediaDurationMs, existingSpeakerBounds?, wordTimestamps?)`: merge gaps <MergePauseMs, split >MaxSegmentMs at silence midpoint, keep overlaps where regions overlap >100ms (create OverlapGroup per overlapping cluster), assign Sequence ordered by StartMs, Id deterministic `GuidUtility.From(runId+sequence)` (implement SHA1-based deterministic Guid, document), Duration=End-Start. Validate: Start>=0, End>Start, End<=mediaDuration, no overflow (End int range), invalid overlap (negative overlap window) rejected with PIPELINE_INVARIANT_VIOLATION.
3. Create `SegmentBuilderWorker : BaseConsumer<StageWorkRequested>` (SegmentBuild, scope Run, queue control.orchestration): load VAD artifact + media duration, call service, insert SpeechSegments + OverlapGroups + SegmentOverlaps in transaction, persist segments artifact (JSON), set RunStageSummary ExpectedUnits for segment-scoped stages = segment count, Complete.
4. Quota: if count>MaxSegments → fail with QUOTA_EXCEEDED, no partial insert.
5. Segment Status initial `Pending`; SpeakerId null placeholder until diarization.

## Requirements

- R1: Fixture produces expected segments (sequence ordered).
- R2: Overlap creates relational rows.
- R3: Invalid timeline rejected.
- R4: Count quota enforced.
- R5: Deterministic IDs/sequence.

## Edge Cases and Error Handling

- Empty VAD (silence) → zero segments, downstream stages Skipped (publish completion with zero units).
- VAD provider transient → retry; permanent → fail stage (no fallback).
- Overlapping beyond media → overflow error BLOCKED.
- Duplicate build (retry) → idempotent via unique (Run,Sequence) handling (delete+reinsert in same tx only if prior attempt same run+stage attempt).

## Security and Safety Requirements

- Tenant validation; no secrets; timeline ints prevent float drift.

## Testing

Create `tests/DubbingPlatform.UnitTests/Media/SegmentBuilderTests.cs`: `Merges_Short_Pauses`, `Splits_Long_Speech`, `Overlap_Creates_Relations`, `Invalid_Timeline_Rejected`, `Quota_Enforced`, `Sequence_Ordered_Deterministic`.

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~SegmentBuilderTests
```

## Completion Criteria

- VAD + segments + overlaps + validation work; tests pass.

## Traceability

- Plan Section 11; Functional checklist VAD/segments/overlap-relational; Error checklist invalid overlaps preserved/blocked.
