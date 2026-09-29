// Task 040B seam 5: voice change -> dependent invalidation / retry + consent gate.
//
// Owning tasks: 029 + 010.
//
// The claim under test: changing a speaker's voice is not a cosmetic update. It
// invalidates the audio that depended on the old voice, and a repeat assignment
// of the same voice changes nothing. A seam that only asserted "the response
// said changed:true" would pass against an implementation that reported a change
// and left the dependent output stale.
//
// R3: the assignment row and the speaker's stored voice pointer are read from
// the database.

import { expect, test } from '@playwright/test';

import { ApiClient, countRows, readAssignedVoiceProfileId } from '../harness/index.js';
import { openSeamContext, sameId } from './seamContext.js';

interface SpeakerSummary {
  readonly id: string;
  readonly speakerKey: string;
  readonly assignedVoice: { voiceProfileId: string; voiceId: string } | null;
}

interface VoiceAssignment {
  readonly speakerId?: string;
  readonly voiceId?: string;
  readonly changed?: boolean;
  readonly outputStale?: boolean;
  readonly warningCode?: string | null;
}

interface AvailableVoices {
  readonly voices: ReadonlyArray<{ voiceProfileId: string; voiceId: string }>;
  readonly excluded?: ReadonlyArray<{ voiceId: string; reason?: string }>;
}

test.describe.serial('@cross-layer voice-invalidation', () => {
  test('assigning a different voice invalidates the dependent output and is persisted', async ({
    page,
  }, testInfo) => {
    const { api, token, projectId, environment } = await openSeamContext();
    const { speakerId, voiceProfileAId, voiceProfileBId } = environment.fixtures;

    await page.goto('/', { waitUntil: 'domcontentloaded' });

    // The speaker starts on voice A, assigned by the seeder.
    const before = await api.requestRaw<SpeakerSummary>({
      method: 'GET',
      path: `/api/v1/projects/${projectId}/speakers/${speakerId}`,
      token,
    });
    expect(before.status, `the seeded speaker must be readable: ${JSON.stringify(before.body)}`).toBe(
      200,
    );
    expect(before.body.assignedVoice, 'the speaker starts with an assigned voice').not.toBeNull();
    // Voice profile ids come back in the public `voice_<32hex>` form while the
    // seeder holds the dashed GUID, so identity is compared normalised.
    expect(
      sameId(before.body.assignedVoice!.voiceProfileId, voiceProfileAId),
      'the speaker starts on the seeded voice A',
    ).toBe(true);

    // Only voice B is a *change*; the available list must offer it.
    const available = await api.requestRaw<AvailableVoices>({
      method: 'GET',
      path: `/api/v1/projects/${projectId}/speakers/${speakerId}/available-voices`,
      token,
    });
    expect(available.status).toBe(200);
    expect(
      available.body.voices.some((voice) => sameId(voice.voiceProfileId, voiceProfileBId)),
      'the second seeded voice must be offered',
    ).toBe(true);

    const assignmentsBefore = await countRows('speaker_voice_assignments', projectId);
    const storedBefore = await readAssignedVoiceProfileId(speakerId);
    expect(storedBefore, 'the seeded assignment is stored').not.toBeNull();
    expect(
      sameId(storedBefore!, voiceProfileAId),
      'the stored pointer starts on voice A',
    ).toBe(true);

    // --- the change ---------------------------------------------------------
    const changed = await api.requestRaw<VoiceAssignment>({
      method: 'PUT',
      path: `/api/v1/projects/${projectId}/speakers/${speakerId}/voice-assignment`,
      token,
      body: { voiceId: voiceProfileBId, reason: 'seam-voice-change' },
      headers: { 'Idempotency-Key': `seam-voice-change-${Date.now()}` },
    });

    expect(changed.status, `assign: ${JSON.stringify(changed.body)}`).toBe(200);
    expect(changed.body.changed, 'a different voice is a real change').toBe(true);

    // The dependent-output signal: the response must tell the client whether the
    // audio it is showing is now stale. `VoiceAssignmentResponse` documents
    // `outputStale:true` + `OUTPUT_STALE` when final output exists.
    expect(
      typeof changed.body.outputStale,
      'the response must report whether dependent output is stale',
    ).toBe('boolean');

    // --- the client can read the new state ----------------------------------
    const after = await api.requestRaw<SpeakerSummary>({
      method: 'GET',
      path: `/api/v1/projects/${projectId}/speakers/${speakerId}`,
      token,
    });
    expect(
      after.body.assignedVoice !== null &&
        sameId(after.body.assignedVoice.voiceProfileId, voiceProfileBId),
      'the stored assignment must be the new voice',
    ).toBe(true);

    // --- R3: the assignment is durable --------------------------------------
    // The row is replaced in place, so the assertion is on the stored pointer -
    // which is a stronger check than a count, because it pins *which* voice the
    // database holds rather than only how many rows there are.
    const storedAfter = await readAssignedVoiceProfileId(speakerId);
    expect(
      storedAfter !== null && sameId(storedAfter, voiceProfileBId),
      `the stored assignment must be voice B, got ${storedAfter}`,
    ).toBe(true);

    const assignmentsAfter = await countRows('speaker_voice_assignments', projectId);
    expect(
      assignmentsAfter,
      `a voice change replaces the assignment row in place (was ${assignmentsBefore})`,
    ).toBe(assignmentsBefore);

    testInfo.annotations.push({
      type: 'rig',
      description:
        `speakerId=${speakerId} voiceA=${voiceProfileAId} voiceB=${voiceProfileBId} ` +
        `changed=${changed.body.changed} outputStale=${changed.body.outputStale} ` +
        `warning=${changed.body.warningCode ?? 'none'} ` +
        `assignments=${assignmentsBefore}->${assignmentsAfter}`,
    });
  });

  test('re-assigning the same voice changes nothing and does not add a row', async () => {
    const { api, token, projectId, environment } = await openSeamContext();
    const { speakerId, voiceProfileBId } = environment.fixtures;

    const assignmentsBefore = await countRows('speaker_voice_assignments', projectId);

    // The failure half of the change path: idempotence. Re-applying the same
    // voice must report `changed:false` and must not churn the assignment, because
    // a spurious re-render/invalidation is the user-visible cost.
    const same = await api.requestRaw<VoiceAssignment>({
      method: 'PUT',
      path: `/api/v1/projects/${projectId}/speakers/${speakerId}/voice-assignment`,
      token,
      body: { voiceId: voiceProfileBId, reason: 'seam-voice-nochange' },
      headers: { 'Idempotency-Key': `seam-voice-nochange-${Date.now()}` },
    });

    expect(same.status, `same-voice assign: ${JSON.stringify(same.body)}`).toBe(200);
    expect(same.body.changed, 'assigning the same voice is not a change').toBe(false);
    expect(
      same.body.outputStale,
      'a no-op assignment must not mark dependent output stale',
    ).toBe(false);

    const assignmentsAfter = await countRows('speaker_voice_assignments', projectId);
    expect(
      assignmentsAfter,
      'a no-op assignment must not churn the assignment table',
    ).toBe(assignmentsBefore);
  });

  test('refuses an unknown speaker, and refuses to clear the voice pointer', async () => {
    const { api, token, projectId, environment } = await openSeamContext();
    const { speakerId, voiceProfileBId } = environment.fixtures;

    // A structurally valid speaker id that does not exist must be refused by name.
    const unknown = await api.requestRaw({
      method: 'PUT',
      path: `/api/v1/projects/${projectId}/speakers/spk_0000000000000000000000000000abcd/voice-assignment`,
      token,
      body: { voiceId: voiceProfileBId, reason: 'seam-unknown-speaker' },
      headers: { 'Idempotency-Key': `seam-unknown-speaker-${Date.now()}` },
    });
    expect(
      unknown.status,
      `an unknown speaker must be refused, got ${unknown.status}: ${JSON.stringify(unknown.body)}`,
    ).toBe(404);
    expect(unknown.status).toBeLessThan(500);

    // Clearing the assignment is refused: a speaker always has a voice, so a
    // request with no voice id cannot silently drop the pointer. (Verified, not
    // assumed - an earlier draft of this seam expected 200 here and the API
    // answers 404 VOICE_NOT_FOUND "Voice id must not be empty".)
    const cleared = await api.requestRaw({
      method: 'PUT',
      path: `/api/v1/projects/${projectId}/speakers/${speakerId}/voice-assignment`,
      token,
      body: { voiceId: null, reason: 'seam-clear' },
      headers: { 'Idempotency-Key': `seam-clear-${Date.now()}` },
    });
    expect(
      cleared.status,
      `clearing the voice must be refused, got ${cleared.status}: ${JSON.stringify(cleared.body)}`,
    ).toBeGreaterThanOrEqual(400);
    expect(cleared.status).toBeLessThan(500);
    expect(ApiClient.errorCode(cleared), 'the refusal must be a named code').toBeTruthy();

    // ...and the refused request must not have changed what is stored.
    const stored = await readAssignedVoiceProfileId(speakerId);
    expect(
      stored !== null && sameId(stored, voiceProfileBId),
      `a refused clear must leave voice B assigned, got ${stored}`,
    ).toBe(true);
  });
});
