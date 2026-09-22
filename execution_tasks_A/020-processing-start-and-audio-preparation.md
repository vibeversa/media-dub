# Task 20 — Processing Start and Audio Preparation

## Goal

Implement explicit processing start with run creation plus media analysis and canonical archival audio production with hardened FFmpeg execution.

## Context

Binding: POST /projects/{projectId}/processing validates media-ready, no active non-terminal run, quotas, cost preflight, privacy. Creates ProcessingRun Pending + attempt + pipeline version + config/route/snapshot hashes, starts saga, publishes RunStarted. MediaAnalyzer claims stage, validates source artifact + hash, produces analysis artifact + plan. AudioPreparation extracts canonical 48kHz/24-bit PCM-or-FLAC preserving layout (+32-bit float working only when needed), verifies via FFprobe, uploads artifact, records exact FFmpeg args. Resource limits (CPU/memory/tmp/timeout/concurrency), disk check, temp cleanup, stage executions, next-stage publish.

## Starting State

Upload ingestion + MediaReady state exist. No ProcessingController start logic, no MediaAnalyzerWorker, no AudioPreparationWorker, no FFmpegService. StageExecution/Artifact/Quota/Cost/Policy interfaces exist.

## Scope

Must implement: processing-start endpoint logic, run creation, MediaAnalyzerWorker, AudioPreparationWorker, FFmpegService, resource guards. Must not implement: separation and later stages.

## Instructions

1. Implement `POST /api/v1/projects/{projectId}/processing` in ProcessingController (route already exists): validate project MediaReady else 409; check no active run via partial-unique query else 409 CONFLICT; quota (active projects, cost estimate via ICostGate.EstimateAsync) + privacy (PolicyChecker) else 403 POLICY_DENIED / 402? Decision: quota → 429 QUOTA_EXCEEDED (document); create ProcessingRun Attempt=(max+1), PipelineVersion="1.0.0", ConfigurationHash (from project settings), ProviderRouteHash, ExecutionSnapshotHash basis; insert + RunStageSummary rows for all DAG stages (ExpectedUnits: run-scoped=1, segment/speaker/window set later by dispatcher); publish RunStarted; return 202 `{runId: run_..., attempt}`. Idempotency 7d.
2. Create `src/DubbingPlatform.Infrastructure/Media/FFmpegService.cs`: `Task<FfmpegResult> RunAsync(IReadOnlyList<string> args, string workDir, CancellationToken)` wrapping ProcessRunner; `ExtractCanonicalAudioAsync(sourcePath, destFlacPath, ct)` args `["-y","-i",source,"-vn","-ar","48000","-sample_fmt","s32","-c:a","flac",dest]` (24-bit via s32→flac; document) preserving layout (no -ac unless provider downmix needed); optional float working `["-ar","48000","-sample_fmt","flt",...]` only when `needsFloatWork=true`. Always log exact args (no secrets), verify output via FFprobeService.
3. Create `MediaAnalyzerWorker : BaseConsumer<StageWorkRequested>` (filter StageType==MediaAnalysis): claim, verify ContentObject hash (re-hash streaming if configured `Media:VerifyOnUse=true` default true), produce analysis JSON artifact (duration, streams, plan {needsSeparation, estimatedSegments}) via ArtifactService, Complete + publish StageCompleted.
4. Create `AudioPreparationWorker : BaseConsumer<StageWorkRequested>` (AudioPreparation): claim, download source to /tmp, check disk free >2x source size else RESOURCE_EXHAUSTED fail-fast, run FFmpeg with timeout MediaOptions.FfmpegTimeoutSec, concurrency semaphore MaxConcurrentMediaJobs, verify sampleRate==48000 + layout preserved + duration within 100ms of source, upload canonical artifact (type CanonicalAudio, FLAC), record args in StageExecution OutputArtifactIdsJson metadata + ProviderExecution? (no provider, record as stage metadata), Complete.
5. Guards: `DiskSpaceChecker.EnsureFree(path, requiredBytes)`; temp cleanup in finally (`Directory.Delete(workDir,true)`); CPU threads `-threads {CpuThreads}` appended.

## Requirements

- R1: Start creates one active run; second start 409.
- R2: Canonical audio 48kHz, layout preserved, FLAC/PCM.
- R3: FFmpeg args recorded.
- R4: Resource/disk checks fail fast.
- R5: Stage executions + next-stage publish.

## Edge Cases and Error Handling

- Media not ready → 409.
- Active run exists → 409 CONFLICT.
- Quota/cost fail → QUOTA_EXCEEDED.
- FFmpeg non-zero → retry once if transient (timeout) else Failed.
- Disk full → RESOURCE_EXHAUSTED, no partial artifact committed.

## Security and Safety Requirements

- FFmpeg ArgumentList only, no shell; temp isolated; no secrets in args/logs; resource limits enforced.

## Testing

Create `tests/DubbingPlatform.IntegrationTests/Media/AudioPrepTests.cs`: `Start_Creates_One_Run`, `Second_Start_409`, `Canonical_Audio_SampleRate_Layout`, `Duration_Within_Tolerance`, `Args_Recorded`, `Disk_Full_Fails_Fast` (mock checker).

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~AudioPrepTests
```

Requires ffmpeg/ffprobe in media image or local.

## Completion Criteria

- Processing start + analysis + canonical audio work; tests pass.

## Traceability

- Plan Section 9 all actions; Assumptions 18–20,27–29,48–54; Functional checklist explicit start/canonical lossless/source-unchanged.
