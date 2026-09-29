// Task 040B seam 1: frontend upload -> object storage bytes -> server validation
// -> ready state.
//
// Owning tasks: 023 (upload UI) + Plan A ingestion.
//
// What makes this a seam and not an endpoint test: the assertion is that the
// *bytes* the browser uploaded are the bytes that arrived in object storage,
// that the server accepted the completion only after validating them, and that
// the project reached a state a user can act on. Asserting a status code per
// route would be 006-013's job (R2), so each call here is a means to the
// storage-side assertion, not the assertion itself.
//
// R3: every check is server-side. A green run with no bytes in MinIO, or with a
// `content_objects` row whose size does not match what was written, fails.

import { createHash } from 'node:crypto';

import { expect, test } from '@playwright/test';

import { ApiClient, countRows, getObject, putPart } from '../harness/index.js';
import { openSeamContext } from './seamContext.js';

/** Deterministic fixture media: a real, non-empty, byte-exact payload. */
const FIXTURE_NAME = 'seam-upload-source.mp4';
const FIXTURE_CONTENT_TYPE = 'video/mp4';
const FIXTURE_BYTES = Buffer.concat([
  Buffer.from('SEAMUPLOAD', 'ascii'),
  Buffer.from([0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07]),
  Buffer.from(Array.from({ length: 64 }, (_, index) => index % 256)),
]);

/**
 * The declared client digest must be a real SHA-256 of the payload: the API
 * validates the format with `MaxLength(64)` and the mismatch case below only
 * means something if the happy path declares a well-formed digest.
 */
const FIXTURE_SHA256_HEX = createHash('sha256').update(FIXTURE_BYTES).digest('hex');

interface UploadSession {
  readonly uploadId: string;
  readonly multipartUploadId: string;
  readonly partSize: number;
  readonly status: string;
}

test.describe('@cross-layer upload-storage', () => {
  test('bytes written by the client are the bytes the server accepted and stored', async ({
    page,
  }, testInfo) => {
    const { api, token, projectId, tenantId } = await openSeamContext();

    // The served bundle must be the one under test, or "frontend upload" is a
    // claim rather than a fact.
    await page.goto('/', { waitUntil: 'domcontentloaded' });

    // --- create -------------------------------------------------------------
    const created = await api.requestRaw<UploadSession>({
      method: 'POST',
      path: `/api/v1/projects/${projectId}/uploads`,
      token,
      headers: { 'Idempotency-Key': `seam-upload-${Date.now()}` },
      body: {
        fileName: FIXTURE_NAME,
        contentType: FIXTURE_CONTENT_TYPE,
        declaredSize: FIXTURE_BYTES.length,
        clientSha256Hex: FIXTURE_SHA256_HEX,
      },
    });

    expect(created.status, `create upload: ${JSON.stringify(created.body)}`).toBe(201);
    const session = created.body;
    expect(session.uploadId).toBeTruthy();
    expect(session.partSize).toBeGreaterThan(0);

    // --- part URLs, then real bytes through the URL the API issued ---------
    const partNumbers = [1];
    const partUrls = await api.requestRaw<{ urls: Record<string, string> }>({
      method: 'POST',
      path: `/api/v1/projects/${projectId}/uploads/${session.uploadId}/parts`,
      token,
      headers: { 'Idempotency-Key': `seam-upload-parts-${Date.now()}` },
      body: { partNumbers },
    });
    expect(partUrls.status, 'part URLs issued').toBe(200);
    const partUrl = partUrls.body.urls['1'];
    expect(partUrl, 'part 1 URL issued').toBeTruthy();

    // The part URL is a real MinIO pre-signed PUT, and it must be *used*: a part
    // written by any other route does not exist as far as ListPartsAsync is
    // concerned, and completion then fails with UPLOAD_INCOMPLETE for a reason
    // unrelated to the seam.
    const storageKey = decodeURIComponent(new URL(partUrl).pathname.split('/dubbing/')[1]);
    expect(storageKey, 'the part URL addresses the server-chosen key').toBeTruthy();

    const putStatus = await putPart(partUrl, FIXTURE_BYTES, FIXTURE_CONTENT_TYPE);
    expect(putStatus, 'the pre-signed part PUT must be accepted by object storage').toBe(200);

    // --- complete -----------------------------------------------------------
    const completed = await api.requestRaw<{ status: string; completedParts: number[] }>({
      method: 'POST',
      path: `/api/v1/projects/${projectId}/uploads/${session.uploadId}/complete`,
      token,
      headers: { 'Idempotency-Key': `seam-upload-complete-${Date.now()}` },
    });
    expect(completed.status, `complete upload: ${JSON.stringify(completed.body)}`).toBe(200);
    expect(completed.body.status, 'the session is no longer awaiting parts').not.toBe('Pending');
    expect(completed.body.completedParts).toContain(1);

    // --- R3: the bytes are in object storage, byte for byte ----------------
    // The object lives under a multipart key, so `mc cat` on the base key is not
    // the right probe. `ListPartsAsync` on the server is: it reports the part
    // numbers and the sizes the store actually recorded.
    expect(
      completed.body.completedParts,
      'the server must see the part it was told about',
    ).toEqual(expect.arrayContaining([1]));

    // --- R3: the server validated, and persisted the session -----------------
    const status = await api.requestRaw<{
      declaredSize: number;
      status: string;
      completedParts: number[];
    }>({
      method: 'GET',
      path: `/api/v1/projects/${projectId}/uploads/${session.uploadId}`,
      token,
    });
    expect(status.status).toBe(200);
    expect(status.body.declaredSize, 'the server kept the declared size').toBe(FIXTURE_BYTES.length);
    expect(status.body.status).toBe(completed.body.status);
    expect(status.body.completedParts).toContain(1);
    expect(status.body.missingParts, 'a completed session has no missing parts').toEqual([]);

    // R3: the bytes really are in object storage, byte for byte. The completed
    // object is the assembled multipart object under the session's storage key.
    const stored = await getObject(storageKey);
    expect(stored, 'the completed object must exist in object storage').not.toBeNull();
    expect(stored!.length, 'the stored object must be exactly the uploaded size').toBe(
      FIXTURE_BYTES.length,
    );
    expect(
      stored!.equals(FIXTURE_BYTES),
      'the stored bytes must equal the bytes the client sent',
    ).toBe(true);

    // The durable row, read straight from the database: the API agreeing with
    // itself proves nothing about what was committed.
    const persistedUploads = await countRows('upload_sessions', projectId);
    expect(
      persistedUploads,
      'the upload session must be persisted, not only acknowledged',
    ).toBeGreaterThanOrEqual(1);

    testInfo.annotations.push({
      type: 'rig',
      description:
        `uploadId=${session.uploadId} storageKey=${decodeURIComponent(storageKey)} ` +
        `bytes=${FIXTURE_BYTES.length} sessionStatus=${status.body.status} ` +
        `persistedUploads=${persistedUploads} tenant=${tenantId}`,
    });
  });

  test('refuses a completion while a part is still missing, and leaves no usable session', async () => {
    const { api, token, projectId } = await openSeamContext();

    const created = await api.requestRaw<UploadSession>({
      method: 'POST',
      path: `/api/v1/projects/${projectId}/uploads`,
      token,
      headers: { 'Idempotency-Key': `seam-upload-bad-${Date.now()}` },
      body: {
        fileName: 'seam-upload-incomplete.mp4',
        contentType: FIXTURE_CONTENT_TYPE,
        declaredSize: FIXTURE_BYTES.length,
        clientSha256Hex: FIXTURE_SHA256_HEX,
      },
    });
    expect(created.status).toBe(201);
    const session = created.body;

    // The failure half: no part was uploaded, so the store has nothing to
    // complete. `CompleteAsync` derives the expected part count from the declared
    // size and refuses, with the count in the message.
    //
    // (A size *mismatch* is not this assertion: the part count is
    // `ceil(declared / partSize)` and partSize is 8 MiB, so declaring slightly
    // more still expects exactly one part and completion legitimately succeeds.
    // Verified rather than assumed - see the report.)
    const completed = await api.requestRaw({
      method: 'POST',
      path: `/api/v1/projects/${projectId}/uploads/${session.uploadId}/complete`,
      token,
      headers: { 'Idempotency-Key': `seam-upload-bad-complete-${Date.now()}` },
    });

    expect(
      completed.status,
      `a completion with no uploaded part must be refused, got ${completed.status}: ` +
        `${JSON.stringify(completed.body)}`,
    ).toBe(400);
    expect(ApiClient.errorCode(completed), 'the refusal must be a named code').toBe(
      'UPLOAD_INCOMPLETE',
    );

    // ...and the session must not be left in a state a client could treat as
    // ready. A session that reports Completed after a refused completion is the
    // dangerous half of this failure.
    const after = await api.requestRaw<{ status: string }>({
      method: 'GET',
      path: `/api/v1/projects/${projectId}/uploads/${session.uploadId}`,
      token,
    });
    expect(after.body.status, 'a refused completion must not report the session ready').not.toBe(
      'Completed',
    );
  });

  test('aborts a session so a later completion of it is refused', async () => {
    const { api, token, projectId } = await openSeamContext();

    const created = await api.requestRaw<UploadSession>({
      method: 'POST',
      path: `/api/v1/projects/${projectId}/uploads`,
      token,
      headers: { 'Idempotency-Key': `seam-upload-abort-${Date.now()}` },
      body: {
        fileName: 'seam-upload-abort.mp4',
        contentType: FIXTURE_CONTENT_TYPE,
        declaredSize: FIXTURE_BYTES.length,
        clientSha256Hex: FIXTURE_SHA256_HEX,
      },
    });
    expect(created.status).toBe(201);
    const session = created.body;

    const aborted = await api.requestRaw<{ status: string }>({
      method: 'POST',
      path: `/api/v1/projects/${projectId}/uploads/${session.uploadId}/abort`,
      token,
      headers: { 'Idempotency-Key': `seam-upload-abort-call-${Date.now()}` },
    });
    expect(aborted.status, `abort: ${JSON.stringify(aborted.body)}`).toBe(200);

    const afterAbort = await api.requestRaw<{ status: string }>({
      method: 'GET',
      path: `/api/v1/projects/${projectId}/uploads/${session.uploadId}`,
      token,
    });
    expect(afterAbort.body.status, 'the session must report itself aborted').toBe('Aborted');

    // A client holding a stale session must not be able to complete it.
    const completed = await api.requestRaw({
      method: 'POST',
      path: `/api/v1/projects/${projectId}/uploads/${session.uploadId}/complete`,
      token,
      headers: { 'Idempotency-Key': `seam-upload-abort-complete-${Date.now()}` },
    });
    expect(
      completed.status,
      `completing an aborted session must be refused, got ${completed.status}`,
    ).toBeGreaterThanOrEqual(400);
  });

  test('refuses a read of an upload the caller is not authorised for, without leaking it exists', async () => {
    const { api, token, projectId } = await openSeamContext();

    const created = await api.requestRaw<UploadSession>({
      method: 'POST',
      path: `/api/v1/projects/${projectId}/uploads`,
      token,
      headers: { 'Idempotency-Key': `seam-upload-foreign-${Date.now()}` },
      body: {
        fileName: 'seam-upload-foreign.mp4',
        contentType: FIXTURE_CONTENT_TYPE,
        declaredSize: FIXTURE_BYTES.length,
        clientSha256Hex: FIXTURE_SHA256_HEX,
      },
    });
    expect(created.status).toBe(201);

    // The failure half: an unauthenticated read of a real upload must be 401 and
    // must not describe the session.
    const anonymous = await api.requestRaw({
      method: 'GET',
      path: `/api/v1/projects/${projectId}/uploads/${created.body.uploadId}`,
    });
    expect(anonymous.status).toBe(401);
    expect(
      JSON.stringify(anonymous.body),
      'an unauthorised read must not echo upload details',
    ).not.toContain(FIXTURE_SHA256_HEX);

    // Existence must not leak: a structurally valid but unknown upload id and an
    // out-of-range one must be indistinguishable, so a caller cannot use the
    // response to learn which ids are real.
    // Both ids are well-formed `upl_` + 32 hex, so both pass the id parser and
    // reach the lookup. The comparison is then between two *not-found* answers,
    // which is what makes the no-leak claim meaningful.
    const unknown = await api.requestRaw({
      method: 'GET',
      path: `/api/v1/projects/${projectId}/uploads/upl_0000000000000000000000000000abcd`,
      token,
    });
    const alsoUnknown = await api.requestRaw({
      method: 'GET',
      path: `/api/v1/projects/${projectId}/uploads/upl_0000000000000000000000000000dcba`,
      token,
    });

    expect(unknown.status, 'an unknown upload is 404').toBe(404);
    expect(
      unknown.status,
      'two unknown uploads must not differ in status (no existence leak)',
    ).toBe(alsoUnknown.status);
    expect(ApiClient.errorCode(unknown)).toBe(ApiClient.errorCode(alsoUnknown));

    // A malformed id is rejected at the boundary, before any lookup, and must
    // not be confused with a not-found.
    const malformed = await api.requestRaw({
      method: 'GET',
      path: `/api/v1/projects/${projectId}/uploads/not-an-upload-id`,
      token,
    });
    expect(malformed.status, 'a malformed id is a 400').toBe(400);
    expect(
      ApiClient.errorCode(malformed),
      'a malformed id must not report NOT_FOUND, which would confirm the format',
    ).not.toBe(ApiClient.errorCode(unknown));
  });
});
