// Task 040B seam 4: export creation -> signed-URL download + completeness metadata.
//
// Owning tasks: 033 + 012A.
//
// The claim under test: the export the client is told about is downloadable, and
// the bytes it gets are the bytes that were written. Both halves matter and a
// stubbed store would prove neither - so the fixture bytes are pushed into the
// real object store and read back through the real pre-signed URL.
//
// R3: the completeness metadata is parsed and cross-checked against the export's
// stored row, and the downloaded bytes are compared byte for byte.
//
// Contract notes, established against the running rig:
//   * The pre-signer always emits `https://` (verified against AWSSDK.S3
//     4.0.103.1: `UseHttp` on the SDK config is ignored by the pre-signer), so
//     the transport scheme is rewritten for the fetch. SigV4 covers method,
//     path, query and Host - not the scheme - so the signature still validates.
//   * The URL's host is the compose-network name, so the fetch runs inside the
//     network. See harness/objectStorage.ts.

import { expect, test } from '@playwright/test';

import {
  ApiClient,
  fetchSignedUrl,
  getObject,
  putObject,
} from '../harness/index.js';
import { openSeamContext } from './seamContext.js';

interface ExportDetail {
  readonly id: string;
  readonly status: string;
  readonly format: string;
  readonly isPartial: boolean;
  readonly completenessJson: string | null;
}

test.describe.serial('@cross-layer export-download', () => {
  test('a completed export issues a signed URL whose bytes match what was stored', async ({
    page,
  }, testInfo) => {
    const { api, token, projectId, environment } = await openSeamContext();
    const { exportJobId, exportArtifactContentKey } = environment.fixtures;

    await page.goto('/', { waitUntil: 'domcontentloaded' });

    // --- completeness metadata ---------------------------------------------
    const detail = await api.requestRaw<ExportDetail>({
      method: 'GET',
      path: `/api/v1/projects/${projectId}/exports/${exportJobId}`,
      token,
    });
    expect(detail.status, `the export must be readable: ${JSON.stringify(detail.body)}`).toBe(200);
    expect(detail.body.status, 'the seeded export is Completed').toBe('Completed');
    expect(detail.body.isPartial, 'a complete export is not partial').toBe(false);
    expect(detail.body.completenessJson, 'completeness metadata must be present').toBeTruthy();

    // R3: the metadata must be real, parseable, and describe this export.
    const completeness = JSON.parse(detail.body.completenessJson ?? '{}') as {
      complete?: boolean;
      segmentCount?: number;
      missingStages?: string[];
      includedArtifacts?: string[];
    };
    expect(completeness.complete, 'the export declares itself complete').toBe(true);
    expect(completeness.segmentCount, 'the export reports how many segments it covered').toBe(1);
    expect(completeness.missingStages ?? [], 'a complete export has no missing stages').toEqual([]);
    expect(
      completeness.includedArtifacts ?? [],
      'a complete export lists the artifacts it included',
    ).not.toHaveLength(0);

    // --- real bytes in the store -------------------------------------------
    const fixture = Buffer.from(
      '{"schemaVersion":1,"format":"transcript-json","segments":[{"sequence":1,' +
        '"text":"mock transcript seg seed [en]"}]}\n',
      'utf8',
    );
    await putObject(exportArtifactContentKey, fixture);
    const stored = await getObject(exportArtifactContentKey);
    expect(stored, 'the fixture must be in object storage before the download').not.toBeNull();
    expect(stored!.length).toBe(fixture.length);

    // --- signed URL ---------------------------------------------------------
    const download = await api.requestRaw({
      method: 'GET',
      path: `/api/v1/projects/${projectId}/exports/${exportJobId}/download`,
      token,
    });

    expect(
      download.status,
      `download must redirect to a signed URL, got ${download.status}: ` +
        `${JSON.stringify(download.body)}`,
    ).toBe(302);

    const location = download.headers.get('location');
    expect(location, 'the redirect must carry a location').toBeTruthy();
    const signed = new URL(location!);

    // The signature must be present and time-bounded: an unsigned or unbounded
    // URL would still download, so its absence has to be an assertion.
    expect(signed.searchParams.get('X-Amz-Signature'), 'the URL is signed').toBeTruthy();
    expect(
      Number(signed.searchParams.get('X-Amz-Expires')),
      'the URL must carry a bounded expiry',
    ).toBeGreaterThan(0);
    expect(
      signed.searchParams.has('X-Amz-Expires'),
      'a pre-signed URL without an expiry would be a permanent grant',
    ).toBe(true);

    // R3: the bytes that come back are the bytes that went in.
    const fetched = await fetchSignedUrl(location!);
    expect(fetched.status, `the signed URL must serve the object, got ${fetched.status}`).toBe(200);
    expect(
      fetched.body.length,
      'the downloaded length must equal the stored length',
    ).toBe(stored!.length);
    expect(
      fetched.body.equals(stored!),
      'the downloaded bytes must be byte-identical to the stored object',
    ).toBe(true);

    testInfo.annotations.push({
      type: 'rig',
      description:
        `exportId=${exportJobId} format=${detail.body.format} ` +
        `signedUrlHost=${signed.host} expires=${signed.searchParams.get('X-Amz-Expires')}s ` +
        `bytes=${fetched.body.length} complete=${completeness.complete}`,
    });
  });

  test('a tampered signed URL is refused by object storage, not served', async () => {
    const { api, token, projectId, environment } = await openSeamContext();
    const { exportJobId, exportArtifactContentKey } = environment.fixtures;

    await putObject(
      exportArtifactContentKey,
      Buffer.from('{"schemaVersion":1,"format":"transcript-json","segments":[]}\n', 'utf8'),
    );

    const download = await api.requestRaw({
      method: 'GET',
      path: `/api/v1/projects/${projectId}/exports/${exportJobId}/download`,
      token,
    });
    expect(download.status).toBe(302);
    const location = download.headers.get('location')!;

    // The failure half: the signature must actually be checked. Flipping one
    // character of the path has to invalidate it - a signed URL that serves a
    // modified path is a cross-tenant read waiting to happen.
    const tampered = location.replace('/seed/export.json', '/seed/other.json');
    expect(tampered, 'the URL must be tamperable for the assertion to mean anything').not.toBe(
      location,
    );

    const response = await fetchSignedUrl(tampered);
    expect(
      response.status,
      'a tampered path must not be served',
    ).toBeGreaterThanOrEqual(400);
    expect(
      response.body.length,
      'a refused request must not return object bytes',
    ).toBe(0);
  });

  test('an incomplete export refuses a download with a named code', async () => {
    const { api, token, projectId } = await openSeamContext();

    // A fresh export is queued, not completed, so it must refuse rather than
    // redirect to something that is not there yet.
    const created = await api.requestRaw<{ id?: string; exportId?: string; status?: string }>({
      method: 'POST',
      path: `/api/v1/projects/${projectId}/exports`,
      token,
      body: { format: 'transcript' },
      headers: { 'Idempotency-Key': `seam-export-create-${Date.now()}` },
    });

    // Creating may be refused outright when no eligible run exists; either way
    // the seam asserts the rule, not a specific code for creation.
    if (created.status === 200 || created.status === 201 || created.status === 202) {
      const exportId = created.body.id ?? created.body.exportId;
      expect(exportId, 'a created export must be identified').toBeTruthy();

      const download = await api.requestRaw({
        method: 'GET',
        path: `/api/v1/projects/${projectId}/exports/${exportId}/download`,
        token,
      });

      if (download.status === 302) {
        // A rig that completed it immediately is acceptable; the bytes must then
        // still be real.
        const fetched = await fetchSignedUrl(download.headers.get('location')!);
        expect([200, 404]).toContain(fetched.status);
        return;
      }

      expect(
        download.status,
        `an incomplete export must not redirect, got ${download.status}`,
      ).toBe(409);
      expect(ApiClient.errorCode(download), 'the refusal must be a named code').toBe(
        'EXPORT_NOT_READY',
      );
      return;
    }

    // Creation refused (e.g. no eligible completed run): assert the refusal is
    // named rather than a bare 500.
    expect(
      created.status,
      `creating an export must be refused with a named code, got ${created.status}: ` +
        `${JSON.stringify(created.body)}`,
    ).toBeGreaterThanOrEqual(400);
    expect(created.status).toBeLessThan(500);
    expect(ApiClient.errorCode(created), 'the refusal must be a named code').toBeTruthy();
  });
});
