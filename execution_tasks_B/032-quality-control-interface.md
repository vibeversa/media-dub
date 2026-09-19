# Task 032 — Quality Control Interface

## Goal
Implement the QC summary and issue list with evidence-backed, accessibility-safe status signaling.

## Context
Quality gate for dubs; QC read endpoints from Task 008 (processing workspace progress/QC), issue deep-links into the Task 030 timeline and Task 031 review queue. Blocking issues must be unmissable without relying on color alone.

## Starting State
Task 008 (workspace aggregate with QC summary/issues) done. No `features/quality`. Depends on Task 008.

## Scope
Included: `QualitySummary` (passed/warning/retry/review/blocked), `QualityIssue` cards (code/severity/scope/segment/description/evidence/action/status) with evidence rendering, blocking prominence, no-color-only signaling.
Excluded: timeline rendering (Task 030), review dispositions (Task 031), export gating (Task 033).

## Instructions
1. Create `frontend/src/features/quality/QualitySummary.tsx`: aggregate counts by state — passed, warning, retry, review, blocked — fed by `useQuality(projectId)` on `queryKeys.quality` (or the Task 025 workspace aggregate slice); each count links to the filtered issue list; blocked state announced with icon + text + pattern, never color alone.
2. Create `frontend/src/features/quality/QualityIssue.tsx`: card shows code, severity (with icon + label), scope (segment/project/run), linked segment id, description, suggested action, status; evidence block per issue type — waveform excerpt (Task 030 mini canvas), timestamp (click → Task 030 issue-jump), audio excerpt player, QC metric readout, artifact link (signed URL, Task 004/012).
3. Implement status signaling rule: every status/severity conveyed by icon + text label + shape/pattern in addition to color (test asserts each badge contains non-color text); blocked issues pinned to top with a prominent banner + count.
4. Implement issue actions: jump-to-timeline (Task 030 `issue-jump`), open-in-review (deep link to Task 031 card where applicable), retry-link where backend advertises the action in `actions[]`; unavailable actions omitted with reason tooltip.
5. Filters + grouping: filter by severity/status/scope; group toggle (by segment / by code); filter state in URL search params; empty states distinguish "no issues" (pass celebration) from "filters exclude everything".

## Requirements
- R1: Summary covers all five states (passed/warning/retry/review/blocked) with counts that match the issue list.
- R2: Every issue shows code/severity/scope/segment/description/evidence/action/status — none omitted (fixture completeness test).
- R3: No color-only signaling anywhere (icon + text + pattern assertion per badge).
- R4: Blocking issues pinned + bannered above all other content.
- R5: Evidence appropriate to issue type renders inline (waveform/timestamp/audio/metric/artifact).

## Edge Cases and Error Handling
- QC pending (run active) → summary shows in-progress skeleton + live updates via Task 026 invalidation, not zeros.
- Evidence artifact expired → single signed-URL refetch, then graceful "evidence unavailable" note.
- Unknown issue code → generic card with raw code + description, never dropped or crashed.
- Zero issues → explicit passed state, not an empty list.

## Security and Safety Requirements
- Artifact/evidence URLs are signed and short-lived; never logged or copied into shareable filter URLs.
- QC metric values displayed as provided; client computes no scores or thresholds.

## Testing
- Create `frontend/src/features/quality/__tests__/quality.test.tsx`: summary counts vs fixture, issue field completeness, non-color signaling assertion, blocking prominence order, filter/group behavior, unknown-code tolerance.
- Playwright `@quality`: summary renders, issue evidence plays/jumps, blocked banner visible, filters narrow list.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/quality`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/quality
npx playwright test --grep="@quality"
```

## Completion Criteria
- QC summary and evidence-backed issues render with blocking prominence and redundant (non-color) signaling; quality tests + `@quality` E2E pass.

## Traceability
- Plan B §12.14. Depends on Task 008.
