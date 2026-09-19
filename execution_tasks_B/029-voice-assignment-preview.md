# Task 029 — Voice Assignment and Preview

## Goal
Implement speaker-to-voice assignment with compatible-only selection, consent gating, and preview playback.

## Context
Voice casting for dub speakers; speaker/voice endpoints from Task 010 (compatible voices, assign/replace/reset, consent states, signed-URL preview), surfaced in translation (Task 028 speaker chips) and review context (Task 031). Backend compatibility is authoritative — the client never invents eligibility.

## Starting State
Task 010 (speakers/voices API: compatibility, assignment, consent, preview URLs) done. No `features/voices`. Depends on Task 010.

## Scope
Included: speaker list with voice state, `VoiceSelector` (preview/select/replace/reset), compatible-only display, impact pre-confirm, consent-state gating, signed-URL preview playback with quota/consent errors.
Excluded: voice model provisioning (backend), translation text editing (Task 028), timeline audio rendering (Task 030).

## Instructions
1. Create `frontend/src/features/voices/SpeakerList.tsx`: per-speaker row — segment count, appearance count/time, current voice + provider/type chips, consent badge; fed by `useSpeakers(projectId)` on `queryKeys.speakers`.
2. Create `frontend/src/features/voices/VoiceSelector.tsx`: compatible-voices dropdown/list from the backend compatibility response only — incompatible voices never rendered (not disabled-rendered, not rendered); select/replace/reset actions call Task 010 endpoints; reset restores backend default with confirm.
3. Implement impact pre-confirm (`frontend/src/features/voices/ImpactDialog.tsx`): before assign/replace, show affected segment count, downstream invalidation note (translations/audio re-render), and cost note where backend provides it; confirm → mutate, cancel → no-op.
4. Implement consent gating: states `unavailable` / `consent-required` / `valid` / `revoked` per voice; non-`valid` voices blocked from assignment with the tenant-policy message from the API shown verbatim; revoked mid-session → assignment button disabled + banner on refetch.
5. Implement preview playback (`frontend/src/features/voices/PreviewPlayer.tsx`): plays the signed preview URL in an `<audio>` element; handles expiry (refetch URL once), quota errors (429 → friendly retry-later message), consent errors (403 → tenant-policy message); never persists the URL.
6. Wire success/error to cache: assignment success invalidates `queryKeys.speakers` (+ translations/audio where Task 026 events indicate); all errors surface via the Task 017 structured-error path (no ad-hoc toasts).

## Requirements
- R1: Only backend-reported compatible voices displayed (test asserts incompatible voice absent from DOM).
- R2: Impact dialog (segments/invalidation/cost) precedes every assign/replace; cancel performs no mutation.
- R3: Consent states enforced client-side with the backend tenant-policy message; non-valid voices unassignable.
- R4: Preview uses short-lived signed URLs only; expired URLs refetched, never cached or logged.
- R5: Quota (429) and consent (403) preview failures show distinct, actionable messages.

## Edge Cases and Error Handling
- Preview URL expired mid-play → single refetch + resume prompt, not a silent failure.
- Voice revoked between list and assign → 403/409 → banner + refetch, selection cleared.
- Speaker with zero segments → row shows `0 segments`, assignment allowed but impact dialog notes no affected segments.
- Compatibility endpoint 404 (project without diarization) → `EmptyState` with pipeline link.

## Security and Safety Requirements
- Signed preview URLs never logged, never stored, never placed in query params beyond the provided URL; `Referrer-Policy` respected.
- Consent/policy messages rendered from backend verbatim; client adds no legal interpretation.

## Testing
- Create `frontend/src/features/voices/__tests__/voices.test.tsx`: compatible-only rendering, impact dialog confirm/cancel paths, consent-gate blocking per state, preview error mapping (429/403/expiry), reset flow.
- Playwright `@voices`: speaker list renders, preview plays, assign shows impact dialog then updates voice, revoked voice blocked.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/voices`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/voices
npx playwright test --grep="@voices"
```

## Completion Criteria
- Speakers assigned compatible, consent-valid voices behind impact confirmation, with signed-URL previews and distinct quota/consent errors; voices tests + `@voices` E2E pass.

## Traceability
- Plan B §12.11. Depends on Task 010.
