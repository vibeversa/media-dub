# Task 25 — Context Build Worker

## Goal

Build reusable deterministic conversation context windows at window granularity for translation.

## Context

Binding: windows from adjacent segments + speaker + transcript + glossary + style + target lang + max token budget (default 2000 tokens, `Context: { MaxTokens=2000, MaxSegmentsPerWindow=5 }`). ContextWindow entities + SegmentContextAssignment. Deterministic for given transcript version + config. One artifact per window reusable by multiple segments (never one-per-segment unless required). Window-scoped executions. Translation waits for required windows. Prompt template + hash recorded if LLM summarization used.

## Starting State

Selected transcripts exist per segment. No ContextBuilderWorker/Service. Translation not yet implemented.

## Scope

Must implement: ContextBuilderService/Worker, windowing, determinism, artifacts, gating. Must not implement: translation logic.

## Instructions

1. Create `src/DubbingPlatform.Application/Services/ContextBuilderService.cs`: `BuildWindowsAsync(tenant,project,run,ct)`: load segments ordered by Sequence with selected transcripts + speakers; sliding window size up to MaxSegmentsPerWindow or MaxTokens (estimate tokens=chars/4); include glossary (`settings.glossary: {term: translation}`) + style (`settings.style`) + target lang; produce ContextWindow rows (Sequence, ContextText = concatenated `[seq speaker: text]` + glossary/style header, ContextHash=SHA256 hex of canonical text, TokenCount estimate); create SegmentContextAssignment per segment→window (many-to-one; overlapping windows allowed but default non-overlapping partitions for determinism — document decision); persist artifact per window type ContextWindow (JSON + schema v1, parent=transcript artifacts).
2. Determinism: sort by Sequence, invariant join, no timestamps in hash input; same input → same ContextHash.
3. Create `src/DubbingPlatform.Workers/Consumers/ContextBuilderWorker.cs` : BaseConsumer<StageWorkRequested> (ContextBuild, scope Window, queue control.orchestration): claim per window (ScopeId=window sequence), call service partition, Complete per window + barrier; saga gates Translation dispatch until all windows Completed (check via BarrierService summary in saga — reuse Task 12 gate).
4. If LLM summarization enabled (`Context:UseLlmSummary=false` default false): call translation-provider LLM with PromptTemplate (record TemplateId/Version + PromptHash + SystemHash in ProviderExecution); default path is deterministic concatenation (no AI judge/cost).
5. Glossary/style from project SettingsJson keys `glossary`, `style`, `targetLanguage`.

## Requirements

- R1: Windows bounded (≤MaxSegments, ≤MaxTokens).
- R2: Same input → same hash.
- R3: Artifacts reusable (multiple segments → one window).
- R4: Prompt metadata recorded when LLM used.
- R5: Translation gated on windows.

## Edge Cases and Error Handling

- No transcripts (all failed) → zero windows, translation Skipped with reason.
- Glossary term missing → proceed without, no fail.
- Token overflow → split window, never truncate silently (record split).
- Duplicate build → idempotent via (Run,Sequence) unique.

## Security and Safety Requirements

- No secrets in context text; tenant-scoped; prompt hashes exclude secrets.

## Testing

Create `tests/DubbingPlatform.UnitTests/Pipeline/ContextBuildTests.cs`: `Windows_Bounded`, `Deterministic_Hash`, `Reusable_Across_Segments`, `Prompt_Recorded_When_Llm`, `Empty_Transcripts_Skips`.

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~ContextBuildTests
```

## Completion Criteria

- Context windows deterministic/reusable/gated; tests pass.

## Traceability

- Plan Section 14; Functional checklist context reusable.
