# Task 030 — Media Player, Waveform, and Timeline

## Goal
Implement the shared media player, canvas waveform, and multi-lane timeline with read-only timing.

## Context
Media review surface reused by transcript (Task 027 compact mode), translation, review, and QC; media artifact + waveform-peak endpoints from Task 004, export/output reads from Task 012. Visualization peaks never derive from archival originals. Timing is display-only — no manual timing edits anywhere.

## Starting State
Tasks 004 (media artifacts, `WaveformPeaks`, signed URLs), 012 (output/export reads) done. No shared player. Depends on Tasks 004, 012.

## Scope
Included: `MediaPlayer` controls, canvas `Waveform` from peaks, `Timeline` lanes + zoom/pan/select/seek/play-region/issue-jump, timing markers, read-only timing rule, memoization/virtualization, debounced interactions.
Excluded: transcript/translation text editing (Tasks 027–028), review actions (Task 031), upload UX (Task 023).

## Instructions
1. Create `frontend/src/features/timeline/MediaPlayer.tsx`: controls play/pause, seek (±5s/±1-frame buttons + scrubber), volume, playback speed, fullscreen, Picture-in-Picture, prev/next segment navigation; keyboard shortcuts (space/K, arrows/J-L); error state for expired signed URL (single refetch).
2. Create `frontend/src/features/timeline/Waveform.tsx`: canvas-rendered waveform from `WaveformPeaks` (Task 004) only — assert at the data layer that peak source is never the archival original (test asserts peak endpoint used, archival URL never requested for visualization); memoized canvas draw, resize-observer redraw, debounced scrub.
3. Create `frontend/src/features/timeline/Timeline.tsx`: lanes — video, source-audio, dialogue (transcript segments), generated-audio, markers; interactions zoom (wheel/ctrl+wheel + buttons), pan (drag), click-to-select segment, click/drag seek, play-region loop, issue-jump (Task 032 issue → timeline position).
4. Render markers: segment start/end, overlap, silence, warning, missing-audio; marker tooltips explain cause + link to owning surface (transcript/translation/QC); missing markers render with pattern + label, never color alone.
5. Enforce read-only timing: no drag-to-retime, no trim handles, no duration inputs anywhere in player/timeline (grep-gate test for retime/trim/duration-edit affordances); segment boundaries come from the aggregate only.
6. Performance: memoized lane components, virtualized marker rendering for long media, debounced (≥100ms) zoom/pan/seek handlers; shared player instance via `frontend/src/features/timeline/playerStore.ts` so Task 027 embeds compact mode without a second element.

## Requirements
- R1: Waveform renders from `WaveformPeaks` only; archival media never fetched for visualization.
- R2: No manual timing edits exist in player or timeline (grep-gate + interaction test).
- R3: Timeline supports zoom/pan/select/seek/play-region/issue-jump across all five lanes.
- R4: Markers cover start/end/overlap/silence/warning/missing with text+pattern encoding (no color-only signaling).
- R5: Scrub/zoom/pan handlers debounced; lanes memoized; 60fps scrub on a 2h fixture (perf smoke).

## Edge Cases and Error Handling
- Peaks missing (still processing) → skeleton waveform + progress link, player still functional.
- Signed media URL expired → single refetch + resume position; double-expiry → error state with retry.
- Zero-duration/gap regions → rendered as explicit gap blocks, never collapsed silently.
- PiP/fullscreen unsupported → controls hidden gracefully with tooltip, no crash.

## Security and Safety Requirements
- Signed media URLs used in `<video>`/`<audio>` only; never logged, never embedded in error reports.
- Peak data treated as untrusted floats: clamped/validated before canvas draw (no NaN propagation).

## Testing
- Create `frontend/src/features/timeline/__tests__/timeline.test.tsx`: player controls + shortcuts, peaks-only assertion, marker rendering per type, read-only timing grep-gate, debounce/zoom/pan behavior, issue-jump wiring.
- Playwright `@timeline`: player plays/seeks, waveform renders, zoom/pan/select/seek work, issue click jumps playhead.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/timeline`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/timeline
npx playwright test --grep="@timeline"
```

## Completion Criteria
- Shared player + peaks waveform + five-lane timeline navigate media with full marker coverage and strictly read-only timing; timeline tests + `@timeline` E2E pass.

## Traceability
- Plan B §10.7, §12.12. Depends on Tasks 004, 012.

## Review Fix — Dependency Correction
- **Re-timed earlier:** depends on 004 (preview artifacts), 008 (run/progress contracts), 009 (segments/timing metadata). The final output API (012A) is OPTIONAL for artifact references only — do not wait for 012A to build player/waveform/timeline.
