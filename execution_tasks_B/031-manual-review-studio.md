# Task 031 — Manual Review Studio

## Goal
Implement the review queue with single-screen item context and idempotent, audited dispositions.

## Context
Human gate before release; review endpoints from Task 011 (queue, approve/reject/requeue/resolve-with-edit with idempotency + expected-version + reason + audit), segment context from Tasks 027–029, mounted in the Task 018 project tabs. Dispositions are anti-duplicate: retries never double-apply.

## Starting State
Tasks 011 (reviews API), 027 (transcript context), 028 (translation context), 029 (voice context) done. No `features/review`. Depends on Tasks 011, 027, 028, 029.

## Scope
Included: `ReviewQueue` + filters, `ReviewCard` single-screen context, approve/reject/requeue/resolve-with-edit with idempotency + expected-version + reason + audit display, conflict→refresh anti-duplicate.
Excluded: transcript/translation/voice editing outside review context (Tasks 027–029 own those), QC scoring (Task 032).

## Instructions
1. Create `frontend/src/features/review/ReviewQueue.tsx`: queue fed by `useReviewQueue(projectId, filters)` on `queryKeys.reviews`; filters — project, severity, status, type, speaker, language, age; filter state in URL search params (shareable); virtualized list; empty-filter result shows `EmptyState` with clear-filters action.
2. Create `frontend/src/features/review/ReviewCard.tsx`: single-screen context per item — media excerpt (Task 030 compact player at issue timestamp), transcript segment, translation + candidates, voice assignment, audio excerpt, sync/QC summary, provider/model/versions, available actions gated by permission, history/audit trail.
3. Implement dispositions (`frontend/src/features/review/useDisposition.ts`): approve / reject / requeue / resolve-with-edit; every call sends `Idempotency-Key` (client-generated per intent, reused on retry) + `expectedVersion` + reason (required for reject/requeue, optional note otherwise); resolve-with-edit embeds the Task 027/028 manual-version payload inline.
4. Implement conflict→refresh: 409/412 → stale banner + refetch + preserved reason text; retry reuses the same idempotency key (test asserts key stability across retries, no duplicate audit entries).
5. Gate actions by the aggregate `actions[]`/permissions: disallowed actions hidden (not disabled) with the reason available in history; optimistic status update with rollback on failure.
6. History panel per card: chronological audit entries (actor, action, reason, version) from the Task 011 history endpoint; newest first; pending-disposition spinner row while mutate in flight.

## Requirements
- R1: All filters (project/severity/status/type/speaker/lang/age) narrow the queue server-side (test asserts query params).
- R2: Every disposition carries idempotency key + expectedVersion + reason per contract; retries reuse the key.
- R3: 409/412 always refetches + banners; no duplicate dispositions from retries (audit-count assertion).
- R4: Review card shows full context single-screen without navigating away (media/transcript/translation/voice/audio/sync/QC/provider/versions/history).
- R5: Disallowed actions hidden by permission, with rationale discoverable in history.

## Edge Cases and Error Handling
- Double-click approve → single mutation (button disabled in flight + idempotency key reuse).
- Item resolved by another reviewer → 409 → banner + card marked resolved by `<actor>` + removed from active filter on refresh.
- Reason exceeding max length → 400 with inline field error, disposition not sent.
- Queue empty → `EmptyState` distinguishing "no items" from "filters exclude everything".

## Security and Safety Requirements
- Reasons rendered as plain text (no HTML); audit actor shown from backend only, never client-injected.
- Permission gating enforced from backend `actions[]`; client hiding is UX only, never a security boundary claim.

## Testing
- Create `frontend/src/features/review/__tests__/review.test.tsx`: filter param mapping, per-action payload (key/version/reason), idempotent retry (stable key, single audit), 409 refresh, permission hiding, resolve-with-edit payload.
- Playwright `@review`: queue filters, card context renders, approve/reject/requeue flows, conflict banner on stale item.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/review`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/review
npx playwright test --grep="@review"
```

## Completion Criteria
- Review queue filters and disposes items with idempotent, versioned, reasoned actions and full single-screen context; review tests + `@review` E2E pass.

## Traceability
- Plan B §12.13. Depends on Tasks 011, 027, 028, 029.
