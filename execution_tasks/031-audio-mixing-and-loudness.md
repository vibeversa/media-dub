# Task 31 — Audio Mixing and Loudness

## Goal

Mix dubbed dialogue with background via FFmpeg filter graphs meeting loudness, peak, ducking, and consistency requirements.

## Context

Binding: FFmpeg complex_filter only (no custom C# sample mixer for final). Apply loudness normalization + true-peak limiting + clipping prevention + ducking + crossfades + 48kHz + layout handling. Defaults integrated -16 LUFS / peak -1 dBTP; broadcast -23 LUFS / -1 dBTP (profile `settings.loudnessProfile: web|broadcast`, default web). Ducking -12dB, 150ms fade in/out (config `Mixing: { DuckDb=-12, FadeMs=150 }`). Two-pass loudnorm where determinism required (default true). Final 48kHz. Persist final audio artifact + record exact filter graph. Publish QC request.

## Starting State

Timeline artifact + dialogue audios + background stem (or null) exist. No AudioMixerWorker/FFmpegMixer.

## Scope

Must implement: FFmpegMixer, AudioMixerWorker, loudness verification. Must not implement: QC/render.

## Instructions

1. Create `src/DubbingPlatform.Infrastructure/Media/FFmpegMixer.cs`: `MixAsync(timelineJson, dialogueFiles[], backgroundFile?, outputWav, profile, ct)`: build complex_filter: `concat dialogue per timeline (adelay+amix or amerge per entry) → [dialog]; background [bg] sidechaincompress + volume duck → [bgduck]; [dialog][bgduck] amix=inputs=2 → loudnorm=I={-16|-23}:TP=-1:LRA=11:measured_* (two-pass: first pass JSON, second pass linear=true) → aformat=sample_rates=48000:channel_layouts=stereo?` Decision: preserve source layout if known else stereo — document; record exact `-filter_complex` string in stage metadata + artifact metadata. Use ProcessRunner + FFmpegService; timeout 600s; concurrency limit shared media semaphore.
2. Ducking: `sidechaincompress=threshold=0.01:ratio=8:attack=150:release=150` + `volume=-12dB` on background when dialogue present (if no background, skip duck).
3. Verify: FFprobe duration/sampleRate/channels; loudness via `ffmpeg -filter ebur128` parse integrated + true-peak (`loudnorm` JSON); assert integrated ±1 LU tolerance, peak ≤-1 dBTP, no clipping (peak <0dBFS); ducking spot-check (background RMS lower during dialogue windows — implement `VerifyDucking` comparing RMS in/out dialogue).
4. Create `AudioMixerWorker : BaseConsumer<StageWorkRequested>` (AudioMixing, scope Run, queue media.render): claim, stage inputs, call mixer, upload artifact type MixedAudio (WAV/FLAC 48kHz), Complete + publish QC request.
5. Config: `Mixing: { Profile=web, DuckDb=-12, FadeMs=150, TwoPass=true }`; `LoudnessTarget` value object reused.

## Requirements

- R1: FFprobe validates duration/rate/channels.
- R2: Loudness within ±1 LU of target.
- R3: Peak within limit, no clipping.
- R4: Ducking applied in dialogue regions.
- R5: Filter graph recorded.

## Edge Cases and Error Handling

- Missing background → mix dialogue only + warning.
- Loudness measure fail → retry once, then review (MIX_QUALITY).
- Clipping detected → apply alimiter + re-measure once, else fail.
- Disk/timeout → RESOURCE_EXHAUSTED, no commit.

## Security and Safety Requirements

- ArgumentList only; temp cleaned; no secrets; resource limits.

## Testing

Create `tests/DubbingPlatform.IntegrationTests/Media/MixingTests.cs` (requires ffmpeg): `Duration_Rate_Channels`, `Loudness_Within_Target`, `Peak_Within_Limit`, `No_Clipping`, `Ducking_Applied` (sine fixtures).

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~MixingTests
```

## Completion Criteria

- Mixed audio meets targets + verified; tests pass.

## Traceability

- Plan Section 20; Assumptions 68–71; Functional checklist mixing loudness; Tests checklist loudness/signal.
