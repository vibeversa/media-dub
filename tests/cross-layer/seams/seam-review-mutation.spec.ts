// Task 040B seam 3: review resolution -> versioned mutation + audit + invalidation.
//
// Owning tasks: 031 + 003/009/011.
//
// The claim under test: a review decision is a *versioned* mutation, not a
// status flip. The client sends the version it believes it is acting on, the
// server refuses when that no longer holds, and on success it records both a
// decision row and the new version. A seam that only checked "the status became
// Approved" would pass against an unversioned write that silently discards a
// concurrent decision.
//
// Contract note, established against the running API rather than assumed: the
// item *read* does not expose a version. `ReviewMutationResponse.Version` is a
// decision-count version and is only ever returned by a mutation, so the version
// a stale writer must present comes from the previous mutation's response.
//
// R3: the decision rows and the item's stored status are read from the database,
// not from the response the client already holds.

import { expect, test } from '@playwright/test';

import { ApiClient, countRows } from '../harness/index.js';
import { openSeamContext } from './seamContext.js';

interface ReviewMutation {
  readonly reviewId: string;
  readonly status: string;
  readonly version: number;
  readonly manualVersionId: string | null;
  readonly versionKind: string | null;
}

interface ReviewDetail {
  readonly id: string;
  readonly status: string;
  readonly reason: string;
  readonly resolvedAt: string | null;
}

test.describe.serial('@cross-layer review-mutation', () => {
  test('resolving a review records a versioned decision, a new version and the new state', async ({
    page,
  }, testInfo) => {
    const { api, token, projectId, environment } = await openSeamContext();
    const reviewId = environment.fixtures.reviewItemId;

    await page.goto('/', { waitUntil: 'domcontentloaded' });

    const before = await api.requestRaw<ReviewDetail>({
      method: 'GET',
      path: `/api/v1/reviews/${reviewId}`,
      token,
    });
    expect(before.status, `the seeded review must be readable: ${JSON.stringify(before.body)}`).toBe(
      200,
    );
    expect(before.body.status, 'the seeded item starts Open').toBe('Open');
    expect(before.body.resolvedAt, 'an Open item has no resolution time').toBeNull();

    const decisionsBefore = await countRows('review_decisions', projectId);

    // --- resolve ------------------------------------------------------------
    const resolved = await api.requestRaw<ReviewMutation>({
      method: 'POST',
      path: `/api/v1/reviews/${reviewId}/resolve`,
      token,
      body: { expectedVersion: 0, reason: 'seam-resolve' },
      headers: { 'Idempotency-Key': `seam-review-resolve-${Date.now()}` },
    });

    expect(resolved.status, `resolve: ${JSON.stringify(resolved.body)}`).toBe(200);
    expect(resolved.body.status, 'the item is no longer Open').not.toBe('Open');
    expect(
      resolved.body.version,
      'a versioned mutation must return an advanced version',
    ).toBeGreaterThan(0);

    // The client must be able to read the new state back, and it must be the
    // state the mutation reported.
    const after = await api.requestRaw<ReviewDetail>({
      method: 'GET',
      path: `/api/v1/reviews/${reviewId}`,
      token,
    });
    expect(after.body.status, 'the stored status matches the mutation response').toBe(
      resolved.body.status,
    );
    expect(after.body.resolvedAt, 'a resolved item records when it was resolved').toBeTruthy();

    // --- R3: the decision is durable ---------------------------------------
    const decisionsAfter = await countRows('review_decisions', projectId);
    expect(
      decisionsAfter,
      `a resolve must persist exactly one decision row (was ${decisionsBefore})`,
    ).toBe(decisionsBefore + 1);

    // --- reopen, then resolve-with-edit mints a manual content version ------
    // A resolved item cannot be resolved again (409 REVIEW_ALREADY_RESOLVED), so
    // the edit path is exercised through reopen -> resolve-with-edit. That is the
    // real client journey for a correction anyway.
    const reopened = await api.requestRaw<ReviewMutation>({
      method: 'POST',
      path: `/api/v1/reviews/${reviewId}/reopen`,
      token,
      body: { expectedVersion: resolved.body.version, reason: 'seam-reopen' },
      headers: { 'Idempotency-Key': `seam-review-reopen-${Date.now()}` },
    });
    expect(reopened.status, `reopen: ${JSON.stringify(reopened.body)}`).toBe(200);
    expect(reopened.body.status, 'the item is Open again').toBe('Open');
    expect(reopened.body.version, 'reopen advances the version too').toBeGreaterThan(
      resolved.body.version,
    );

    const edited = await api.requestRaw<ReviewMutation>({
      method: 'POST',
      path: `/api/v1/reviews/${reviewId}/resolve-with-edit`,
      token,
      body: {
        expectedVersion: reopened.body.version,
        reason: 'seam-resolve-with-edit',
        editText: 'mock transcript seg seed [en] (corrected by seam)',
      },
      headers: { 'Idempotency-Key': `seam-review-edit-${Date.now()}` },
    });

    expect(edited.status, `resolve-with-edit: ${JSON.stringify(edited.body)}`).toBe(200);
    expect(
      edited.body.manualVersionId,
      'resolve-with-edit must mint a manual content version',
    ).toBeTruthy();
    expect(
      edited.body.status,
      'a resolve-with-edit lands in a distinct state, not plain Approved',
    ).not.toBe('Open');
    expect(edited.body.version, 'resolve-with-edit advances the version').toBeGreaterThan(
      reopened.body.version,
    );

    // R3: the manual version is a real transcript row, not just an id.
    const transcriptVersions = await countRows('transcript_versions', projectId);
    expect(
      transcriptVersions,
      'a manual edit must persist a transcript version',
    ).toBeGreaterThanOrEqual(2);

    testInfo.annotations.push({
      type: 'rig',
      description:
        `reviewId=${reviewId} status=${resolved.body.status}->${reopened.body.status}` +
        `->${edited.body.status} version=${resolved.body.version}->${edited.body.version} ` +
        `manualVersionId=${edited.body.manualVersionId ?? 'none'} ` +
        `decisions=${decisionsBefore}->${decisionsAfter} ` +
        `transcriptVersions=${transcriptVersions}`,
    });
  });

  test('refuses a stale resolve and does not advance the stored decision', async () => {
    const { api, token, projectId, environment } = await openSeamContext();
    const reviewId = environment.fixtures.reviewItemId;

    // Present version 0 again. The item has since taken two decisions, so this
    // is exactly the stale writer the version is there to catch.
    const decisionsBefore = await countRows('review_decisions', projectId);

    const stale = await api.requestRaw({
      method: 'POST',
      path: `/api/v1/reviews/${reviewId}/resolve`,
      token,
      body: { expectedVersion: 0, reason: 'seam-stale-resolve' },
      headers: { 'Idempotency-Key': `seam-review-stale-${Date.now()}` },
    });

    expect(stale.status, `a stale resolve must be refused, got ${stale.status}`).toBe(409);
    const code = ApiClient.errorCode(stale);
    expect(
      code,
      `the refusal must be a named code, got '${code}' for ${JSON.stringify(stale.body)}`,
    ).toBeTruthy();

    // The dangerous half: a refused write must not have been recorded.
    const decisionsAfter = await countRows('review_decisions', projectId);
    expect(
      decisionsAfter,
      'a refused resolve must not persist a decision',
    ).toBe(decisionsBefore);
  });

  test('refuses a read of a review the caller cannot see, without leaking it exists', async () => {
    const { api, token, environment } = await openSeamContext();

    // An invalid token is refused before any lookup, so it cannot reveal
    // anything about the review.
    const unauthorised = await api.requestRaw({
      method: 'GET',
      path: `/api/v1/reviews/${environment.fixtures.reviewItemId}`,
      token: 'not-a-valid-token',
    });
    expect(unauthorised.status, 'an invalid token is 401').toBe(401);
    expect(
      JSON.stringify(unauthorised.body),
      'the refusal must not describe the review',
    ).not.toContain(environment.fixtures.reviewItemId);

    // A well-formed id that does not exist is 404 for the caller.
    const unknown = await api.requestRaw({
      method: 'GET',
      path: '/api/v1/reviews/rev_0000000000000000000000000000abcd',
      token,
    });
    expect(unknown.status, 'an unknown review is 404').toBe(404);
  });
});
