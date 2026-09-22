# Task 39 — Test Fixtures and Test Tiers

## Goal

Deliver deterministic media fixtures, fixture generation, and the full test-tier matrix covering correctness, recovery, isolation, and media quality.

## Context

Binding: tiers unit/integration/provider-contract/workflow/E2E-smoke/recovery-chaos/load-soak. Fixtures via FFmpeg small: single-speaker, multi-speaker, overlap, long-silence, music+dialogue, noisy, single-video, multi-video, low-quality, non-English marker. Testcontainers PG/Rabbit/Redis/MinIO; WireMock for adapters; mocks for deterministic CI. Must cover state machines, idempotency, duplicates, barriers, leases, stale commits, cancel races, retry invalidation, failover, async contracts, cross-tenant (API/storage/consumers/Redis/RLS), cost races, quotas/rate, resource exhaustion, disk pressure, orphans, reviews, partial exports, loudness/signal, golden refs where practical, backup/restore, migration compat (old/new schema), fan-out/in load, soak long jobs. Prior tasks already created many tier tests; this task fills gaps + fixtures + E2E smoke.

## Starting State

Many unit/integration/contract tests exist per task. No centralized fixtures, no generate script, no E2E full-pipeline test, no chaos/load/soak/backup/compat tests.

## Scope

Must implement: fixture script + fixtures, E2E smoke, recovery/chaos, load/soak, backup/restore, compat, signal/golden tests. Must not implement: CI wiring (next task), production hardening docs.

## Instructions

1. Create `scripts/generate-fixtures.sh` (bash, LF, uses ffmpeg): generate 10 fixtures into `fixtures/` (all <5MB, <15s): `single-speaker.wav` (sine 440Hz + dialogue), 44.1kHz? Decision: generate 48kHz to match canonical (document); `multi-speaker.wav` (two tones sequenced), `overlap.wav` (mixed tones overlapping), `silence.wav` (10s silence + 2s tone), `music-dialogue.wav` (tone + noise bg), `noisy.wav` (tone + white noise), `single-video.mp4` (testsrc + sine), `multi-video.mp4`, `low-quality.wav` (8kHz upsampled), `non-english.wav` (different tone marker + metadata language=es). Each deterministic (fixed seeds, no randomness). Script idempotent.
2. Wire Testcontainers uniformly: create `tests/DubbingPlatform.IntegrationTests/Fixtures/TestFixtureBase.cs` (PG+Rabbit+Redis+MinIO containers, WireMock server, mock providers default).
3. Create `tests/DubbingPlatform.E2ETests/FullPipelineTests.cs`: `E2E_Full_Pipeline_Mocks` (upload single-video → start → poll progress → assert Completed + MP4 downloadable + duration tolerance + voice stable + translation context-aware + timing constraints + background preserved flag), `E2E_LowConfidence_Fallback`, `E2E_Separation_Fallback`, `E2E_Overlap_Fixture`, `E2E_Video_Tolerance`, `E2E_Audio_Only`, `E2E_Exports`, `E2E_Review_Resolution` — all with mocks, timeout 5min each, using fixtures.
4. Gaps: `RecoveryTests.cs` (worker crash → lease recovery, network partition simulated via container pause, broker outage), `LoadTests.cs` (200 segments fan-out/in completes, concurrent 5 projects), `SoakTests.cs` (marked `[Trait("Category","Soak")]` skipped in CI by default, 30min long job), `MediaBombTests.cs` (100MB declared vs 1GB actual → rejected; zip-bomb-like oversized → RESOURCE_EXHAUSTED), `BackupRestoreTests.cs` (pg_dump + restore + verify counts), `MigrationCompatTests.cs` (old code/new schema additive check via information_schema), `SignalTests.cs` (loudness/ducking golden thresholds on fixtures).
5. Keep fixtures small; document `fixtures/README.md` with durations + expected segments/speakers per fixture for assertions.

## Requirements

- R1: All tiers pass (except Soak skipped in CI).
- R2: E2E full pipeline with mocks passes.
- R3: Recovery/isolation/quota/media-bomb covered.
- R4: Fixtures deterministic + small.
- R5: Golden/signal thresholds documented.

## Edge Cases and Error Handling

- FFmpeg missing → fixture tests skip with message (not fail).
- Container unavailable → skip with message in local fast (document).
- Flaky timing → use tolerances (±100ms, ±1LU), never exact float equality.

## Security and Safety Requirements

- Fixtures contain no PII/secrets; synthetic tones only; Testcontainers isolated networks.

## Testing

This task IS testing: create files above; run `dotnet test` full suite (excluding Soak).

## Validation

```bash
bash scripts/generate-fixtures.sh
dotnet build
dotnet test --filter Category!=Soak
dotnet test --filter FullyQualifiedName~FullPipelineTests.E2E_Full_Pipeline_Mocks
```

## Completion Criteria

- Fixtures + all tiers present and passing (Soak excluded from gate).

## Traceability

- Plan Section 28 all actions; Tests checklist (all 32 items); Functional/Error checklists E2E fixtures.
