#!/usr/bin/env bash
# Task 39: deterministic media fixtures for test tiers (E2E smoke, recovery,
# signal/golden, media-bomb negative controls).
#
# Generates 10 small (<5MB, <15s) synthetic fixtures into ./fixtures plus the
# two legacy Task 19 fixtures (valid-2s.mp4, invalid-text.mp4).
#
# Canonical audio is 48kHz stereo s16 to match the pipeline canonical form.
# Every source is a fixed lavfi generator with fixed parameters (pink/white
# noise uses seed=42); no randomness, no network, no PII/secrets.
#
# Idempotent: existing non-empty outputs are skipped unless FIXTURES_FORCE=1.
# Usage: ./scripts/generate-fixtures.sh [out-dir]
set -euo pipefail

OUT_DIR="${1:-./fixtures}"
FORCE="${FIXTURES_FORCE:-}"
mkdir -p "$OUT_DIR"

if ! command -v ffmpeg >/dev/null 2>&1; then
  echo "ffmpeg is missing; install ffmpeg to generate fixtures." >&2
  exit 1
fi

# needs <name>: true when the fixture must be (re)generated.
needs() {
  if [ -n "$FORCE" ] || [ ! -s "$OUT_DIR/$1" ]; then
    return 0
  fi
  echo "exists, skipping: $1"
  return 1
}

# 1. single-speaker.wav: 4s 440Hz dialogue tone, 48kHz stereo.
if needs "single-speaker.wav"; then
  ffmpeg -y -v error \
    -f lavfi -i "sine=frequency=440:duration=4:sample_rate=48000" \
    -c:a pcm_s16le -ac 2 "$OUT_DIR/single-speaker.wav"
  echo "Wrote $OUT_DIR/single-speaker.wav"
fi

# 2. multi-speaker.wav: 6s, 3s 440Hz then 3s 660Hz (two speakers sequenced).
if needs "multi-speaker.wav"; then
  ffmpeg -y -v error \
    -f lavfi -i "sine=frequency=440:duration=3:sample_rate=48000" \
    -f lavfi -i "sine=frequency=660:duration=3:sample_rate=48000" \
    -filter_complex "[0:a][1:a]concat=n=2:v=0:a=1" \
    -c:a pcm_s16le -ac 2 "$OUT_DIR/multi-speaker.wav"
  echo "Wrote $OUT_DIR/multi-speaker.wav"
fi

# 3. overlap.wav: 4s, 440Hz + 660Hz mixed simultaneously (overlapping speech).
if needs "overlap.wav"; then
  ffmpeg -y -v error \
    -f lavfi -i "sine=frequency=440:duration=4:sample_rate=48000" \
    -f lavfi -i "sine=frequency=660:duration=4:sample_rate=48000" \
    -filter_complex "[0:a][1:a]amix=inputs=2:duration=longest:normalize=0" \
    -c:a pcm_s16le -ac 2 "$OUT_DIR/overlap.wav"
  echo "Wrote $OUT_DIR/overlap.wav"
fi

# 4. silence.wav: 12s, 10s digital silence then 2s 440Hz tone.
if needs "silence.wav"; then
  ffmpeg -y -v error \
    -f lavfi -i "anullsrc=sample_rate=48000:channel_layout=stereo:duration=10" \
    -f lavfi -i "sine=frequency=440:duration=2:sample_rate=48000" \
    -filter_complex "[0:a][1:a]concat=n=2:v=0:a=1" \
    -c:a pcm_s16le -ac 2 "$OUT_DIR/silence.wav"
  echo "Wrote $OUT_DIR/silence.wav"
fi

# 5. music-dialogue.wav: 5s, 440Hz dialogue over a quiet pink-noise music bed.
if needs "music-dialogue.wav"; then
  ffmpeg -y -v error \
    -f lavfi -i "sine=frequency=440:duration=5:sample_rate=48000" \
    -f lavfi -i "anoisesrc=color=pink:duration=5:sample_rate=48000:seed=42" \
    -filter_complex "[1:a]volume=0.15[bg];[0:a][bg]amix=inputs=2:duration=longest:normalize=0" \
    -c:a pcm_s16le -ac 2 "$OUT_DIR/music-dialogue.wav"
  echo "Wrote $OUT_DIR/music-dialogue.wav"
fi

# 6. noisy.wav: 4s, 440Hz dialogue mixed with white noise (seed=42).
if needs "noisy.wav"; then
  ffmpeg -y -v error \
    -f lavfi -i "sine=frequency=440:duration=4:sample_rate=48000" \
    -f lavfi -i "anoisesrc=color=white:duration=4:sample_rate=48000:seed=42" \
    -filter_complex "[1:a]volume=0.4[bg];[0:a][bg]amix=inputs=2:duration=longest:normalize=0" \
    -c:a pcm_s16le -ac 2 "$OUT_DIR/noisy.wav"
  echo "Wrote $OUT_DIR/noisy.wav"
fi

# 7. single-video.mp4: 3s testsrc 320x240@10fps + 440Hz sine (h264/aac).
if needs "single-video.mp4"; then
  ffmpeg -y -v error \
    -f lavfi -i "testsrc=duration=3:size=320x240:rate=10" \
    -f lavfi -i "sine=frequency=440:duration=3:sample_rate=48000" \
    -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest "$OUT_DIR/single-video.mp4"
  echo "Wrote $OUT_DIR/single-video.mp4"
fi

# 8. multi-video.mp4: 6s, 3s testsrc/440Hz then 3s smptebars/660Hz.
if needs "multi-video.mp4"; then
  ffmpeg -y -v error \
    -f lavfi -i "testsrc=duration=3:size=320x240:rate=10" \
    -f lavfi -i "smptebars=duration=3:size=320x240:rate=10" \
    -f lavfi -i "sine=frequency=440:duration=3:sample_rate=48000" \
    -f lavfi -i "sine=frequency=660:duration=3:sample_rate=48000" \
    -filter_complex "[0:v][1:v]concat=n=2:v=1:a=0[v];[2:a][3:a]concat=n=2:v=0:a=1[a]" \
    -map "[v]" -map "[a]" -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest "$OUT_DIR/multi-video.mp4"
  echo "Wrote $OUT_DIR/multi-video.mp4"
fi

# 9. low-quality.wav: 4s 440Hz mastered at 8kHz mono, then upsampled to 48kHz.
if needs "low-quality.wav"; then
  tmp_lo="$OUT_DIR/.low-quality-8k.tmp.wav"
  ffmpeg -y -v error \
    -f lavfi -i "sine=frequency=440:duration=4:sample_rate=8000" \
    -c:a pcm_s16le -ar 8000 -ac 1 "$tmp_lo"
  ffmpeg -y -v error -i "$tmp_lo" -c:a pcm_s16le -ar 48000 -ac 2 "$OUT_DIR/low-quality.wav"
  rm -f "$tmp_lo"
  echo "Wrote $OUT_DIR/low-quality.wav"
fi

# 10. non-english.wav: 4s 520Hz marker tone, tagged language=es.
if needs "non-english.wav"; then
  ffmpeg -y -v error \
    -f lavfi -i "sine=frequency=520:duration=4:sample_rate=48000" \
    -c:a pcm_s16le -ac 2 -metadata language=es "$OUT_DIR/non-english.wav"
  echo "Wrote $OUT_DIR/non-english.wav"
fi

# Legacy Task 19 fixtures (kept for ingestion-test compat).
if needs "valid-2s.mp4"; then
  ffmpeg -y -v error \
    -f lavfi -i "testsrc=duration=2:size=320x240:rate=10" \
    -f lavfi -i "sine=frequency=440:duration=2" \
    -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest "$OUT_DIR/valid-2s.mp4"
  echo "Wrote $OUT_DIR/valid-2s.mp4"
fi

if [ -n "$FORCE" ] || [ ! -s "$OUT_DIR/invalid-text.mp4" ]; then
  printf 'this is not a video, just text renamed to mp4' > "$OUT_DIR/invalid-text.mp4"
  echo "Wrote $OUT_DIR/invalid-text.mp4"
else
  echo "exists, skipping: invalid-text.mp4"
fi

# Size gate: every fixture must stay under 5MB.
fail=0
for f in single-speaker.wav multi-speaker.wav overlap.wav silence.wav music-dialogue.wav noisy.wav single-video.mp4 multi-video.mp4 low-quality.wav non-english.wav valid-2s.mp4 invalid-text.mp4; do
  size=$(wc -c < "$OUT_DIR/$f")
  if [ "$size" -ge 5242880 ]; then
    echo "SIZE GATE FAILED: $f is $size bytes (>= 5MB)." >&2
    fail=1
  else
    echo "ok: $f ($size bytes)"
  fi
done

if [ "$fail" -ne 0 ]; then
  exit 1
fi

echo "All fixtures ready in $OUT_DIR."
