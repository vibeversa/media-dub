# Task 044 — Optional Enrichment / Local AI UX

**Required/Optional:** Optional
**Complexity:** S

## Goal
Expose flag-gated optional enrichment (video intelligence, lip-sync score with separate asset, local-GPU operator health) so failures never block core flows.

## Context
Core product (Tasks 001–043) completes without this task. Enrichment surfaces from Plan B §19.1–§19.3 ride behind feature flags (Task 036 `FlagsPanel`): video-intel artifacts separate from core outputs, lip-sync as score + separate transformed asset, local-GPU provider visible only as operator health with a privacy-policy routing note. Skipping this task leaves a complete product.

## Starting State
Tasks 036 and 043 done. Depends on Tasks 036 and 043. May be deferred or skipped without blocking release.

## Scope
Included: flag-gated video-intel UI, lip-sync score + separate asset download, operator-only local-GPU health panel.
Excluded: enrichment pipeline execution (backend/AI), core flows (untouched), ordinary-user GPU internals (never shown).

## Instructions
1. Create `frontend/src/features/enrichment/EnrichmentGate.tsx`: central flag check (`flags.videoIntel`, `flags.lipSync`, `flags.localGpu`) — when off, zero UI chrome for enrichment appears anywhere (no upsell banners, no disabled buttons); when on, mount the panels below without altering core layouts.
2. Create `frontend/src/features/enrichment/VideoIntelPanel.tsx`: display video-intel results as separate artifacts (scene cuts, detected overlays) linked from — never merged into — the transcript/timeline; enrichment failure renders a dismissible `UnavailableState` while transcript/translation/voice/review continue normally.
3. Create `frontend/src/features/enrichment/LipSyncPanel.tsx`: show lip-sync score per segment + separate transformed asset download (distinct from core outputs in Task 033); score shown with method note (`heuristic vX`); asset download reuses the signed-URL click-time pattern, never a new mechanism.
4. Create `frontend/src/features/admin/LocalGpuPanel.tsx` (operator-only, inside Task 036 Admin): provider health/model/version/device summary for the local GPU; ordinary users never see GPU internals; include the privacy-policy routing note (local processing path declared, data never leaves the operator boundary without explicit policy).
5. Prove non-blocking in `frontend/src/features/enrichment/__tests__/enrichment.test.tsx`: with flags off, core suites unaffected (import-gate test — enrichment modules not in core bundles); with enrichment APIs 500/timeout, workspace/review/export tests still pass (failure-isolation test).

## Requirements
- R1: All enrichment UI hidden entirely when flags are off (no chrome, no upsell, no disabled buttons).
- R2: Video-intel results are separate artifacts, never merged into core transcript/timeline data.
- R3: Lip-sync ships as score + separate asset; score carries its method note.
- R4: Local-GPU health visible to operators only with the privacy-policy routing note; no GPU internals for ordinary users.
- R5: Enrichment failure (flag on, backend 500/timeout) never blocks transcript/translation/voice/review/export flows.
- R6: Core bundles exclude enrichment modules when flags are off (import-gate test).

## Edge Cases and Error Handling
- Flag on but backend unimplemented (501) → panels show `NotAvailableState` with "operator feature in setup" copy, core unaffected.
- Lip-sync asset missing while score present → score stays, download hidden with reason.
- GPU health endpoint unreachable → operator panel shows `UnknownState`, never blocks admin login or other panels.
- Enrichment artifact references deleted segment → link degrades to `GoneState`, no crash.

## Security and Safety Requirements
- Enrichment downloads reuse Task 037 signed-URL rules (15-min, tenant-bound, never logged).
- GPU/device details restricted to elevated Admin role; API 403s for all others (extend `AdminAuthzTests` pattern).
- Privacy-policy routing note reviewed against §19.3 before display copy freezes.

## Testing
- Create `frontend/src/features/enrichment/__tests__/enrichment.test.tsx`: flag-off invisibility, separate-artifact rule, failure-isolation (500/timeout → core green), import-gate (core bundles clean).
- Playwright `@optional-enrichment`: flags on → panels render with fixtures; flags off → zero enrichment chrome; backend failure → core journey still completes.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/admin` + `@optional-enrichment`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/admin
npx playwright test --grep="@optional-enrichment"
```

## Completion Criteria
- Flag-gated enrichment renders separately, fails without blocking core, hides operator GPU detail from ordinary users; enrichment tests + `@optional-enrichment` pass — or the task is explicitly deferred with no core impact.

## Traceability
- Plan B §19.1–§19.3. Depends on Tasks 036 and 043.
