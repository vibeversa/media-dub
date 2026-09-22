# Task 27 — Voice Assignment and Consent

## Goal

Assign stable policy-aware voices per speaker with consent enforcement and auditable assignments.

## Context

Binding: speaker-scoped executions. Resolve per speaker via target lang + style + overrides (`settings.voiceOverrides: {speakerKey: voiceId}`) + inventory + tenant policy. Persist SpeakerVoiceAssignment; same speaker same voice across segments; stable across retries unless overridden. Metadata provider/voiceId/version/reason/policyHash. Cloning disabled by default (`Voices: { CloningEnabled=false }`); if enabled require ConsentRecord (subject/evidence/scope/jurisdiction/timestamp/revocation/voice relation) validated + audit. Consent lifecycle grant/revoke/audit enforced at assignment/cloning. Privacy participates in routing.

## Starting State

Speakers + diarization exist. No VoiceAssignmentWorker/Service, no VoiceProfile/Consent logic. Provider voice inventory via descriptors exists.

## Scope

Must implement: VoiceAssignmentService/Worker, consent validation, stability, audit. Must not implement: TTS generation.

## Instructions

1. Create `src/DubbingPlatform.Application/Services/VoiceAssignmentService.cs`: `AssignAsync(tenant,project,run,speakerId,ct)`: load speaker + target lang + style + overrides + tenant ProcessingPolicy + provider voice inventories (from DescriptorStore VoiceInventory); if override present validate inventory contains it else throw PROVIDER_CONFIGURATION_ERROR; else deterministic pick: hash(speakerKey) mod inventory filtered by language (sorted voiceIds for determinism); check cloning: if chosen Type==Cloned and !CloningEnabled → throw CONSENT_REQUIRED; if cloning enabled require ConsentRecord Status==Granted, scope covers project, not revoked/expired, jurisdiction matches policy else CONSENT_REQUIRED/POLICY_DENIED; persist SpeakerVoiceAssignment (Reason=`override|deterministic|cloned-consented`, PolicyHash=hash of policy+inventory version); record AuditEvent for cloned assignments.
2. Stability: `AssignAsync` idempotent — if assignment exists for (Run? Decision: per Project stable, per Run copy — document: first run creates Project-level assignment reused; retry returns existing unless override changed) → return existing.
3. Create `VoiceAssignmentWorker : BaseConsumer<StageWorkRequested>` (VoiceAssignment, scope Speaker, queue ai.provider): claim per speaker, call service, Complete per speaker + project-level barrier (ExpectedUnits=speaker count).
4. ConsentRecord CRUD: `ConsentService.GrantAsync/RevokeAsync` (called from Admin API later; implement service here): Revoke sets Status Revoked + RevokedAt + audit; assignment checks revocation live (revoked blocks new uses, existing artifacts remain immutable — document).
5. Config: `Voices: { CloningEnabled=false, DefaultProvider=mock }`.

## Requirements

- R1: Same speaker → same voice stable.
- R2: Cloning without consent rejected (CONSENT_REQUIRED).
- R3: Revocation blocks new cloning use.
- R4: Incompatible voice route fails.
- R5: Audit on cloned/consent changes.

## Edge Cases and Error Handling

- Unknown override voice → PROVIDER_CONFIGURATION_ERROR fail-fast.
- Revoked mid-run → in-flight TTS finishes, new segments blocked to review.
- No inventory for language → fail stage with clear message.
- Duplicate assign message → return existing (idempotent).

## Security and Safety Requirements

- Consent evidence required; cloning default-off; audit append-only; tenant isolation; no biometric storage beyond voiceId.

## Testing

Create `tests/DubbingPlatform.UnitTests/Pipeline/VoiceAssignmentTests.cs`: `Stable_Mapping`, `Cloning_Without_Consent_Rejected`, `Revocation_Blocks`, `Incompatibility_Fails`, `Override_Respected`.

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~VoiceAssignmentTests
```

## Completion Criteria

- Voice assignment stable/policy-auditable; tests pass.

## Traceability

- Plan Section 16; Assumptions 76–77 consent/privacy; Security checklist cloning/consent/privacy-routing.
