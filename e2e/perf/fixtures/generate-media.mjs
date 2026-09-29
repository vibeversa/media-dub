// Regenerates the synthetic preview media fixture used by the media-seek budget
// (Task 041D).
//
//   node e2e/perf/fixtures/generate-media.mjs
//
// Why a generator and not a checked-in binary: the fixture has to be
// reproducible and reviewable. The command below is the whole definition of the
// asset, and anyone can re-run it and diff the result.
//
// Why FFmpeg at all: the media element needs a *decodable* file, and the
// container matters more than the codec here. Two things were found by running
// this, not by reading about it:
//
//   1. Chromium's `seekable` was `[0, 0]` for a WebM written by this pipeline.
//      The `seeked` event still fired, on a no-op, so a seek assertion passed
//      while measuring nothing. The fix is on the *serving* side - the fixture
//      layer answers `Range` with a 206 - but it is why the spec asserts
//      `seekable` before it times anything.
//   2. The published bundle is a Matroska file with a real `Duration` element
//      and cues, which is what makes the demuxer willing to report a seekable
//      range at all. `testsrc` + libvpx at 6fps with a keyframe every 2 seconds
//      is a preview-shaped profile: cheap to encode, cheap to decode, and
//      realistically sparse.
//
// Argument arrays only, never a shell string: an input description is not
// allowed to become syntax. No secrets, no network, no shell.

import { spawnSync } from 'node:child_process';
import { mkdirSync, statSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const OUTPUT = join(HERE, 'preview-60s.mkv');

/** 60 seconds at 6fps, 128x96, 10 kbit/s, a keyframe every 2 seconds. */
const ARGS = [
  '-hide_banner',
  '-loglevel', 'error',
  '-y',
  '-f', 'lavfi',
  '-i', 'testsrc=size=128x96:rate=6:duration=60',
  '-c:v', 'libvpx',
  '-b:v', '10k',
  '-g', '12',
  '-deadline', 'realtime',
  '-cpu-used', '8',
  '-an',
  '-f', 'matroska',
  OUTPUT,
];

const ffmpeg = process.env['FFMPEG_PATH'] ?? 'ffmpeg';
const result = spawnSync(ffmpeg, ARGS, { shell: false, stdio: 'inherit', windowsHide: true });

if (result.error !== undefined) {
  process.stderr.write(
    `Could not run FFmpeg (${ffmpeg}): ${result.error.message}\n` +
      'Install FFmpeg and put it on PATH, or set FFMPEG_PATH to the executable.\n',
  );
  process.exitCode = 1;
} else if (result.status !== 0) {
  process.stderr.write(`FFmpeg exited with ${String(result.status)}.\n`);
  process.exitCode = 1;
} else {
  mkdirSync(HERE, { recursive: true });
  const size = statSync(OUTPUT).size;
  process.stdout.write(
    `wrote ${OUTPUT} (${String(size)} bytes, 60.000s, VP8/Matroska)\n` +
      'The media-seek budget records this size in `e2e/perf/budgets.ts`; re-run this and commit the\n' +
      'new file together with any size change there.\n',
  );
}
