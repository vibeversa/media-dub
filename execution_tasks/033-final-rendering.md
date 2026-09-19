# Task 33 — Final Rendering

## Goal

Mux final audio with video or produce audio-only output, validate, register OutputAsset, and complete the run.

## Context

Binding: video → copy stream when safe else re-encode H.264 high preset + record decision; audio-only → WAV/MP3/AAC as requested (`settings.outputFormat: mp4|wav|mp3|aac`, default mp4 if source video else wav). Pad/trim within tolerance: video within one frame (1/fps, default 40ms for 25fps if fps unknown), audio-only within 100ms. FFprobe validate. Register OutputAsset. Mark run Completed if valid + no blocking review, publish RunCompleted; else Failed + RunFailed. Record stage execution.

## Starting State

Mixed audio + QC pass (or pass-with-warnings) exist. No RenderWorker/RenderService. OutputAsset entity exists.

## Scope

Must implement: RenderService/Worker, mux/encode, tolerance validation, run completion. Must not implement: exports, progress APIs.

## Instructions

1. Create `src/DubbingPlatform.Infrastructure/Media/RenderService.cs`: `RenderAsync(sourceVideoPath?, mixedAudioPath, outputPath, outputFormat, fps, ct)`: if video and codecs compatible (video h264 + audio aac/flac→aac): args `["-y","-i",video,"-i",audio,"-c:v","copy","-c:a","aac","-b:a","192k","-shortest",output]`; else re-encode `["-y","-i",video,"-i",audio,"-c:v","libx264","-preset","slow","-crf","18","-c:a","aac","-b:a","192k","-shortest",output]` + set ReencodeReason (e.g., `video-codec-incompatible`); audio-only: wav `[-c:a pcm_s24le -ar 48000]`, mp3 `[-c:a libmp3lame -b:a 192k]`, aac similar. Pad/trim: `apad`/`atrim` to source duration within tolerance.
2. Tolerances: video `1/fps*1000` ms (fps from FFprobe, fallback 40ms); audio-only 100ms; assert `abs(output-source)≤tolerance` else fail.
3. Create `RenderWorker : BaseConsumer<StageWorkRequested>` (Render, scope Run, queue media.render): claim, call RenderService to /tmp then upload artifact type RenderedOutput, create OutputAsset (MediaKind Video/Audio, Duration, Container), FFprobe validate, if QC blocking review open → do not complete (ManualReviewRequired); elif valid → RunStateMachine→Completed, Project→Completed, publish RunCompleted; else →Failed + RunFailed. Record exact FFmpeg args + re-encode decision in stage metadata.
4. Download URL: OutputController `GET /projects/{id}/output/download` returns presigned 15min URL after ownership check (implement here).

## Requirements

- R1: Full pipeline produces final MP4/audio.
- R2: Duration within tolerance.
- R3: Copy vs re-encode recorded.
- R4: Download URL works + expires.
- R5: Run terminal state correct.

## Edge Cases and Error Handling

- QC blocked → no render (fail fast with QC_BLOCKED).
- Render FFmpeg fail → run Failed, retryable via manual retry (Task 35).
- Duration out-of-tolerance → Failed, no OutputAsset.
- Missing mixed audio → ARTIFACT_UNAVAILABLE.

## Security and Safety Requirements

- ArgumentList only; temp cleaned; ownership before URL; 15min expiry.

## Testing

Create `tests/DubbingPlatform.IntegrationTests/Media/RenderTests.cs` (ffmpeg): `Produces_Mp4_Within_Tolerance`, `Copy_When_Safe`, `Reencode_Recorded_When_Unsafe`, `Audio_Only_Within_100ms`, `Download_Url_Works`.

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~RenderTests
```

## Completion Criteria

- Render + validation + run completion + download work; tests pass.

## Traceability

- Plan Section 22; Functional checklist render validates; Completion criteria final media downloadable/timeline tolerance.
