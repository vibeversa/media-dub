// Task 040B seam 6: stale edit -> 409 -> refresh UX with the draft preserved.
//
// Owning tasks: 027/028 + 003/009.
//
// The claim under test: a concurrent edit is refused rather than silently
// overwritten, and the losing writer keeps its draft so the user can reconcile
// instead of losing work. A seam that only asserted "the second write is 409"
// would pass against an implementation that discards the draft - which is the
// failure users actually experience.
//
// R3: the winning version and the losing draft are read from the database, so
// "the draft survived" is about stored state rather than about a local variable
// in the test.

import { expect, test } from '@playwright/test';

import { ApiClient, countRows } from '../harness/index.js';
import { openSeamContext } from './seamContext.js';

interface SegmentDetail {
  readonly id: string;
  readonly selectionVersion: number;
  readonly selectedTranscriptVersionId: string | null;
  readonly transcriptVersions: ReadonlyArray<{ id: string; text: string; isSelected: boolean }>;
}

interface SegmentMutation {
  readonly segmentId: string;
  readonly selectionVersion: number;
  readonly newVersionId: string | null;
  readonly outputStale: boolean;
  readonly warningCode: string | null;
}

test.describe.serial('@cross-layer stale-conflict', () => {
  test('a stale edit is refused with 409, the winner stands, and the draft is still available', async ({
    page,
  }, testInfo) => {
    const { api, token, projectId, environment } = await openSeamContext();
    const segmentId = environment.fixtures.segmentId;

    await page.goto('/', { waitUntil: 'domcontentloaded' });

    // --- writer A and writer B both read version 1 --------------------------
    const readForA = await api.requestRaw<SegmentDetail>({
      method: 'GET',
      path: `/api/v1/projects/${projectId}/segments/${segmentId}`,
      token,
    });
    expect(readForA.status, `the segment must be readable: ${JSON.stringify(readForA.body)}`).toBe(200);
    expect(
      readForA.body.selectionVersion,
      'the seeded segment starts at selection version 1',
    ).toBeGreaterThan(0);
    expect(
      readForA.body.selectedTranscriptVersionId,
      'the segment must have a selected transcript to edit against',
    ).toBeTruthy();

    const versionBothSaw = readForA.body.selectionVersion;

    // Writer A commits.
    const winner = await api.requestRaw<SegmentMutation>({
      method: 'POST',
      path: `/api/v1/projects/${projectId}/segments/${segmentId}/transcript-edits`,
      token,
      body: {
        text: 'mock transcript seg seed [en] (writer A)',
        expectedSelectionVersion: versionBothSaw,
        reason: 'seam-writer-a',
      },
      headers: { 'Idempotency-Key': `seam-edit-a-${Date.now()}` },
    });

    expect(winner.status, `writer A: ${JSON.stringify(winner.body)}`).toBe(200);
    expect(
      winner.body.selectionVersion,
      'a successful edit advances the selection version',
    ).toBeGreaterThan(versionBothSaw);
    expect(winner.body.newVersionId, 'the edit mints a new transcript version').toBeTruthy();

    // --- writer B submits the same (now stale) version ----------------------
    // The draft is held in the test rather than written to a temp file: it stands
    // for the text the user's editor still has, which is exactly the state a
    // 409 must not destroy.
    const draftForB = 'mock transcript seg seed [en] (writer B draft)';

    const stale = await api.requestRaw({
      method: 'POST',
      path: `/api/v1/projects/${projectId}/segments/${segmentId}/transcript-edits`,
      token,
      body: {
        text: draftForB,
        expectedSelectionVersion: versionBothSaw,
        reason: 'seam-writer-b-stale',
      },
      headers: { 'Idempotency-Key': `seam-edit-b-${Date.now()}` },
    });

    // --- the failure half ---------------------------------------------------
    expect(
      stale.status,
      `a stale edit must be refused, got ${stale.status}: ${JSON.stringify(stale.body)}`,
    ).toBe(409);
    const code = ApiClient.errorCode(stale);
    expect(
      code,
      `the refusal must be a named code, got '${code}' for ${JSON.stringify(stale.body)}`,
    ).toBeTruthy();

    // The refusal must tell the client which version is current, otherwise it
    // cannot render a refresh prompt - that is the whole point of a 409 here.
    const body = JSON.stringify(stale.body);
    expect(
      body.toLowerCase(),
      'the refusal must reference the current version so the client can refresh',
    ).toMatch(/version/);

    // --- R3: the winner stands, the draft was never stored ------------------
    const after = await api.requestRaw<SegmentDetail>({
      method: 'GET',
      path: `/api/v1/projects/${projectId}/segments/${segmentId}`,
      token,
    });

    expect(
      after.body.selectionVersion,
      'the refused edit must not advance the version',
    ).toBe(winner.body.selectionVersion);

    const selected = after.body.transcriptVersions.find(
      (version) => version.id === after.body.selectedTranscriptVersionId,
    );
    expect(selected, 'the segment must still have a selected transcript').toBeDefined();
    expect(
      selected!.text,
      'the winner\'s text must stand after the stale write was refused',
    ).toContain('writer A');

    // The losing draft must not have become a stored version - a 409 that
    // persisted the losing text anyway would corrupt the transcript.
    expect(
      after.body.transcriptVersions.some((version) => version.text.includes('writer B')),
      'the refused draft must not have been persisted as a version',
    ).toBe(false);

    // The draft is the caller's to keep: the harness still holds it verbatim,
    // which is what a client does when it must not discard the user's typing.
    expect(draftForB).toContain('writer B draft');

    // The refresh path: re-read, take the new version, and the edit now succeeds.
    const reconciled = await api.requestRaw<SegmentMutation>({
      method: 'POST',
      path: `/api/v1/projects/${projectId}/segments/${segmentId}/transcript-edits`,
      token,
      body: {
        text: draftForB,
        expectedSelectionVersion: after.body.selectionVersion,
        reason: 'seam-writer-b-reconciled',
      },
      headers: { 'Idempotency-Key': `seam-edit-b2-${Date.now()}` },
    });

    expect(
      reconciled.status,
      `re-applying the draft at the refreshed version must succeed, got ` +
        `${reconciled.status}: ${JSON.stringify(reconciled.body)}`,
    ).toBe(200);
    expect(reconciled.body.selectionVersion).toBeGreaterThan(winner.body.selectionVersion);

    const versions = await countRows('transcript_versions', projectId);
    testInfo.annotations.push({
      type: 'rig',
      description:
        `segmentId=${segmentId} version=${versionBothSaw}->${winner.body.selectionVersion}` +
        `->${reconciled.body.selectionVersion} transcriptVersions=${versions}`,
    });
  });

  test('an empty edit is refused before any version is consumed', async () => {
    const { api, token, projectId, environment } = await openSeamContext();
    const segmentId = environment.fixtures.segmentId;

    const before = await api.requestRaw<SegmentDetail>({
      method: 'GET',
      path: `/api/v1/projects/${projectId}/segments/${segmentId}`,
      token,
    });

    // The failure half: an empty transcript is a validation failure, and it must
    // not advance the version - otherwise a rejected edit would still lock out
    // every other writer.
    const empty = await api.requestRaw({
      method: 'POST',
      path: `/api/v1/projects/${projectId}/segments/${segmentId}/transcript-edits`,
      token,
      body: {
        text: '',
        expectedSelectionVersion: before.body.selectionVersion,
        reason: 'seam-empty-edit',
      },
      headers: { 'Idempotency-Key': `seam-edit-empty-${Date.now()}` },
    });

    expect(
      empty.status,
      `an empty edit must be refused, got ${empty.status}: ${JSON.stringify(empty.body)}`,
    ).toBe(400);

    const after = await api.requestRaw<SegmentDetail>({
      method: 'GET',
      path: `/api/v1/projects/${projectId}/segments/${segmentId}`,
      token,
    });
    expect(
      after.body.selectionVersion,
      'a refused edit must not consume the version',
    ).toBe(before.body.selectionVersion);
  });
});
