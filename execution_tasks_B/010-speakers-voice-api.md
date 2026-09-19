# Task 010 — Speakers, Voice Assignment, Voice Preview API

## Goal
Expose speaker list/detail, compatible voices, stable voice assignment, and durable preview endpoints.

## Context
Each speaker keeps one stable voice; changing it invalidates dependent dubbing work, cloning voices requires recorded consent, and incompatible voices must be blocked server-side (the frontend compatible-only display is not a security boundary). Previews are the durable Task 004 jobs surfaced here.

## Starting State
Task 004 done (VoicePreviewJob lifecycle, consent/quota gates, VoicePreviewAudio artifacts). Plan A speaker diarization + voice catalog exist. No speaker/voice HTTP endpoints.

## Scope
Included: `GET .../speakers[/{id}|/{id}/available-voices]`, `PUT .../voice-assignment`, `POST|GET .../voice-previews[/{id}]`, consent + compatibility enforcement, change-invalidation, audit.
Excluded: preview job internals (Task 004, reuse as-is), frontend voice UX (Task 029), final TTS render (Plan A).

## Instructions
1. Create `src/DubbingPlatform.Api/Controllers/SpeakersController.cs` under `/api/v1/projects/{projectId}`: `GET .../speakers` (list with `speakerId, voiceId?, segmentCount, assignedVoice`), `GET .../speakers/{speakerId}`, `GET .../speakers/{speakerId}/available-voices` (compatible-only: language match + gender/style constraints from `src/DubbingPlatform.Application/Voices/VoiceCompatibility.cs`; incompatible voices excluded with `excludedCount` + reasons, never returned as selectable).
2. Add `PUT .../speakers/{speakerId}/voice-assignment` (body `{ voiceId, reason? }`): enforces compatibility server-side (incompatible → 422 `VOICE_INCOMPATIBLE`), consent gate for cloning voices (no consent → 403 `VOICE_CONSENT_REQUIRED`), keeps exactly one voice per speaker, publishes dependent-invalidation event, marks stale output when final output exists; audit with actor + old/new voice.
3. Add `POST .../voice-previews` (body `{ speakerId, voiceId, text?, Idempotency-Key }` → delegates to Task 004 `VoicePreviewService`, returns 202 + `previewId` `vpv_`) and `GET .../voice-previews[/{previewId}]` (status + artifact reference when Completed; artifact served as signed URL only, never internal path).
4. Implement `VoiceCompatibility.cs` rule set: target-language match required; sample-rate/channel floor; cloning flag requires consent record; style-tag allowlist per project settings.
5. Enforce `project.view` for GETs, `project.edit` for assignment/preview creation.
6. Update OpenAPI for speaker/voice/preview schemas and error codes (`VOICE_INCOMPATIBLE`, `VOICE_CONSENT_REQUIRED`, `PREVIEW_QUOTA_EXCEEDED`).

## Requirements
- R1: Available-voices lists only compatible voices; incompatible ones never selectable (test attempts direct assignment of excluded voice → 422).
- R2: Exactly one assigned voice per speaker; reassignment replaces, never duplicates.
- R3: Cloning-voice assignment/preview without consent blocked server-side (403), even if frontend allowed it.
- R4: Voice change publishes invalidation + marks stale output when final output exists (response carries `outputStale: true`).
- R5: Preview creation honors Task 004 idempotency/quota/consent (duplicate key → same job; quota → 429).
- R6: Every assignment writes an audit record with actor + old/new voice + reason.

## Edge Cases and Error Handling
- Unknown voice id → 404 `VOICE_NOT_FOUND`.
- Preview text overlong → 400 `PREVIEW_TEXT_INVALID` (inherited from Task 004).
- Assign same voice id → 200 no-op with `changed: false`, no invalidation emitted.
- Speaker with zero segments → assignment allowed, flagged `unusedSpeaker: true`.

## Security and Safety Requirements
- Tenant + membership checks per route; cross-tenant speaker/voice id → 404.
- Compatibility/consent enforced server-side regardless of client filtering.
- No voice-model binaries or provider keys in responses/logs; preview audio via signed URL only.

## Testing
- Create `tests/DubbingPlatform.IntegrationTests/Voices/VoiceApiTests.cs`: compatible-only listing, incompatible-assign 422, consent-block 403, stable single-voice invariant, change invalidation + stale flag, preview 202/status/idempotency/quota, no-op same-voice, audit written, cross-tenant 404.
- Type: integration (WebApplicationFactory + Testcontainers PostgreSQL; provider client mocked).

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~VoiceApiTests
```

## Completion Criteria
- Speaker/voice/preview endpoints + compatibility + consent + audit exist; `VoiceApiTests` pass; incompatible/consent-less assignments provably blocked.

## Traceability
- Plan B §9.6, §12.11. Depends on Task 004.
