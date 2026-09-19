# Task 027 — Transcript Workspace

## Goal
Implement the transcript editing workspace with version-aware segment editing and player-synced navigation.

## Context
Source-of-truth transcript review for a project; segment read/edit/version endpoints from Task 009, workspace shell from Task 025, live invalidation from Task 026, mounted in the Task 018 project tabs. Candidates are immutable snapshots — edits always create manual versions, never mutate history.

## Starting State
Tasks 009 (segment editing API: select-version, manual-version with expected-version + 409), 025 (workspace shell), 026 (`useProgressStream` invalidation) done. No `features/transcript`. Depends on Tasks 009, 026.

## Scope
Included: `TranscriptEditor` + `SegmentRow`, three-pane player+waveform/list/inspector layout, seek-on-select, active highlight + autoscroll, select-version vs create-manual-version flows, original/selected/manual display, virtualized list.
Excluded: translation editing (Task 028), media player internals + waveform rendering (Task 030), review actions (Task 031).

## Instructions
1. Create `frontend/src/features/transcript/TranscriptEditor.tsx`: three-pane layout — left player+waveform slot (embeds Task 030 `MediaPlayer` in compact mode), center virtualized segment list, right inspector. Fed by `useTranscript(projectId)` on `queryKeys.transcript`; invalidated by Task 026 progress events.
2. Create `frontend/src/features/transcript/SegmentRow.tsx`: row shows timestamp, speaker label, editable text, confidence badge, review flag, inline playback button; selected/active states distinct; keyboard-navigable (up/down moves selection, Enter seeks).
3. Implement seek-on-select: selecting a row seeks the shared player to segment start (via Task 030 player ref/store); active segment highlights during playback with autoscroll (toggleable, pauses on manual scroll, resumes on re-enable).
4. Create `frontend/src/features/transcript/Inspector.tsx`: shows selected segment detail — original text, currently-selected version text, manual draft editor, advanced disclosure (provider/model/version per version); version history list with select-version action.
5. Implement select-version (`POST .../select-version` with `expectedVersion`): on 409 conflict → stale banner + refetch + discard pending selection (no silent overwrite). Implement create-manual-version (`POST .../manual-version` with `expectedVersion` + edited text): optimistic draft state, error rollback, success invalidates `queryKeys.transcript`.
6. Virtualize the list (`frontend/src/features/transcript/VirtualizedSegmentList.tsx`): windowing for 1000+ segments; memoized rows; debounced search/filter by speaker/text/review-flag; never fetch per-row (single transcript query, slices via props).
7. Display rule: every segment shows original vs selected vs manual lineage explicitly (badges: `original`, `selected vN`, `manual`); unreviewed/low-confidence segments carry a review flag that links to Task 031.

## Requirements
- R1: Edits never mutate history — manual versions are new versions only (test asserts no PUT/patch on version endpoints).
- R2: Every mutating call sends `expectedVersion`; 409 always triggers refetch + banner, never silent overwrite.
- R3: Selecting a row seeks the player to segment start (ms-accurate within one frame interval).
- R4: Active-segment highlight tracks playback with autoscroll that yields to manual scrolling.
- R5: Original/selected/manual lineage visible per segment at all times.
- R6: List renders 1000+ segments without frame drops (virtualized;Memoized rows).

## Edge Cases and Error Handling
- 409 on select/manual → stale banner + refresh action; pending edit preserved as draft, never auto-resubmitted.
- Transcript empty (pre-run) → `EmptyState` with link to processing tab, not an empty list.
- Segment deleted server-side → row removed on refetch + toast; selection moves to nearest surviving segment.
- Playback position beyond transcript range → highlight cleared, no crash.

## Security and Safety Requirements
- Version history shows provider/model metadata only — never secrets, tokens, or internal paths.
- Manual text sanitized for display (no raw HTML injection); audit attribution uses session user only.

## Testing
- Create `frontend/src/features/transcript/__tests__/transcript.test.tsx`: select-version success + 409 refresh, manual-version create + rollback, seek-on-select wiring, lineage badges, virtualization smoke (1000-row fixture), filter behavior.
- Playwright `@transcript`: editor renders three panes, row select seeks player, highlight follows playback, 409 shows stale banner.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/transcript`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/transcript
npx playwright test --grep="@transcript"
```

## Completion Criteria
- Transcript workspace edits via versioned flows only, with seek-synced player, highlight + autoscroll, lineage display, and virtualized list; transcript tests + `@transcript` E2E pass.

## Traceability
- Plan B §12.9. Depends on Tasks 009, 026.
