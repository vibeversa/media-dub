# Task 26 — Translation Worker

## Goal

Produce versioned context-aware translations with glossary/style/timing awareness, bounded budgets, and deterministic selection.

## Context

Binding: segment-scoped; provider invoked with source text + context ref + glossary + speaker + target duration + style + target lang. TranslationVersion: primary + alternatives + semantic/naturalness/timing scores + provider/model + prompt metadata + selected. Budgets per segment: max candidates 3, max tokens 4000, max cost $2.00, max wall-clock 60s (`Translation: { MaxCandidates=3, MaxTokens=4000, MaxCost=2.0, MaxWallClockSec=60 }`). Deterministic heuristics default, no AI judge. Alternatives kept for timing opt. Provider executions recorded. Quality below threshold (`Translation:QualityThreshold=0.70`) + fallback exhausted → review.

## Starting State

Context windows + selected transcripts exist. No TranslationWorker/Service.

## Scope

Must implement: TranslationService/Worker, budget enforcement, scoring, review routing. Must not implement: voice/TTS and later.

## Instructions

1. Create `src/DubbingPlatform.Application/Services/TranslationService.cs`: `TranslateSegmentAsync(tenant,project,run,segment,ct)`: load selected transcript + assigned window + glossary/style/targetDuration (segment DurationMs) + target lang; resolve ITranslationProvider; enforce budgets via counters (candidates≤3, tokens tracked via usage, cost via ICostGate reservation, wall-clock via CancellationTokenSource 60s); call provider (up to MaxCandidates alternatives in one call where supported, else loop); score each candidate deterministically: `score = 0.5*semantic + 0.3*naturalness + 0.2*timing` where semantic=naturalness=provider scores (or 0.8 default if missing), timing=`1 - min(1, abs(lenRatio-1))` with lenRatio=targetChars/sourceChars ideal per language pair (en→es 1.0, en→de 1.1, default 1.0 — document table); select max score; persist TranslationVersion (Primary=max, Alternatives=rest, scores, prompt TemplateId/Hash); record ProviderExecution(s).
2. Glossary enforcement: verify primary contains glossary target terms where source contains source terms (case-insensitive); if violated and alternatives contain it, prefer alternative (document rule).
3. Budgets: exceeding candidates/tokens/cost/wall-clock → stop loop, select best-so-far or review if none; log + metric.
4. Review: if best semantic<QualityThreshold and no fallback provider succeeds → ReviewItem (TRANSLATION_QUALITY) + unit ManualReviewRequired.
5. Create `TranslationWorker : BaseConsumer<StageWorkRequested>` (Translation, scope Segment, queue ai.provider): claim, call service, Complete/barrier per segment.

## Requirements

- R1: Glossary terms used when present.
- R2: Context bounded (window ref recorded).
- R3: Candidate budget enforced (≤3).
- R4: Selected marked deterministically.
- R5: Quality fail → review when configured.

## Edge Cases and Error Handling

- Missing context (skipped) → translate without context + warning, not fail.
- Provider rate-limit → delayed retry.
- Cost reservation fail → QUOTA_EXCEEDED, no provider call.
- Cancelled → abort before persist.
- Empty source text → fail fast validation.

## Security and Safety Requirements

- Tenant-scoped; no secrets in prompts stored (hashes exclude secrets); glossary PII stays in tenant.

## Testing

Create `tests/DubbingPlatform.IntegrationTests/Pipeline/TranslationTests.cs` (mocks): `Glossary_Used`, `Budget_Enforced`, `Selected_Marked`, `Quality_Routes_Review`, `Context_Bounded`.

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~TranslationTests
```

## Completion Criteria

- Translation bounded/traceable/context-aware; tests pass.

## Traceability

- Plan Section 15; Functional checklist translations versioned/bounded; Error checklist translation retry dependents (partial — full invalidation Task 35).
