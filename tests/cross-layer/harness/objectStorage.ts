// Task 040B: object-storage access for the upload and export seams.
//
// The rig's storage is a real MinIO emulator holding real bytes, which is the
// point of these two seams: a stubbed store would make them prove nothing.
// Bytes therefore have to reach the bucket and come back out.
//
// Two constraints shape this module, both established by experiment rather than
// assumption.
//
// 1. The pre-signer always emits `https://`. With `Storage:UseSsl=false` and a
//    plaintext MinIO, a URL fetched as-issued fails with an OpenSSL "wrong
//    version number". `AmazonS3Config.UseHttp` does not help: verified against
//    AWSSDK.S3 4.0.103.1, every combination of `ServiceURL` scheme, `UseHttp` and
//    `ForcePathStyle` still produces an `https` pre-signed URL. So the scheme is
//    rewritten for transport only - SigV4 covers method, path, query and Host,
//    not the scheme, so the signature still validates and a rejection is a real
//    storage-side refusal.
//
// 2. The signed URL's host is the compose-network name `minio:9000`, which the
//    host cannot resolve. Fetching therefore happens inside the compose network,
//    which is also the honest place to do it: the URL is handed to a client that
//    can actually reach the store.

import { execFile } from 'node:child_process';
import { writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { promisify } from 'node:util';

import { CONTAINERS, STORAGE_ACCESS_KEY, STORAGE_BUCKET, STORAGE_SECRET_KEY } from './config.js';

const run = promisify(execFile);

/** Name of the `mc` alias configured inside the storage container. */
const ALIAS = 'rig';

export class ObjectStorageError extends Error {
  readonly detail: string;

  constructor(message: string, detail = '') {
    super(detail.length === 0 ? message : `${message}\n${detail}`);
    this.name = 'ObjectStorageError';
    this.detail = detail;
  }
}

async function execInContainer(
  container: string,
  args: readonly string[],
  maxBuffer = 8 * 1024 * 1024,
): Promise<string> {
  try {
    const { stdout, stderr } = await run('docker', ['exec', container, ...args], { maxBuffer });
    return `${stdout}${stderr}`;
  } catch (error) {
    const failure = error as { stderr?: string; message?: string };
    throw new ObjectStorageError(
      `\`docker exec ${container} ${args[0] ?? ''}\` failed.`,
      `${failure.stderr ?? ''}${failure.message ?? ''}`.slice(0, 500),
    );
  }
}

/**
 * Rewrites a pre-signed URL so it can be fetched from inside the compose
 * network. Only the scheme and the host change; path, query and signature are
 * passed through untouched.
 */
export function toTransportUrl(signedUrl: string): string {
  const url = new URL(signedUrl);
  url.protocol = 'http:';
  return url.toString();
}

/** Configures the `mc` alias and creates the bucket. Idempotent. */
export async function ensureBucket(): Promise<void> {
  // Credentials are separate arguments: `mc alias set` rejects a URL carrying
  // userinfo ("should be of the form scheme://host[:port]/ without resource
  // component").
  await execInContainer(CONTAINERS.minio, [
    'mc',
    'alias',
    'set',
    ALIAS,
    'http://127.0.0.1:9000',
    STORAGE_ACCESS_KEY,
    STORAGE_SECRET_KEY,
  ]);
  await execInContainer(CONTAINERS.minio, ['mc', 'mb', '--ignore-existing', `${ALIAS}/${STORAGE_BUCKET}`]);
}

/**
 * Writes `bytes` directly to a storage key.
 *
 * The payload is staged on the host and copied into the container before
 * `mc cp`. `mc pipe` is the obvious alternative and it deadlocks: it needs a
 * closed stdin, `execFile` cannot supply one, and the command then waits forever
 * rather than failing.
 *
 * This is for content the API did not hand out a URL for. For an upload part the
 * seam must go through the pre-signed URL the API issued - `putPart` does that -
 * because a part written any other way does not exist as far as
 * `ListPartsAsync` is concerned, and completion fails with
 * `UPLOAD_INCOMPLETE` for a reason that has nothing to do with the seam.
 */
export async function putObject(storageKey: string, bytes: Buffer): Promise<void> {
  await ensureBucket();

  const staged = path.join(tmpdir(), 'cross-layer-upload.bin');
  writeFileSync(staged, bytes);
  await run('docker', ['cp', staged, `${CONTAINERS.minio}:/tmp/cross-layer-upload.bin`]);

  await execInContainer(CONTAINERS.minio, [
    'mc',
    'cp',
    '/tmp/cross-layer-upload.bin',
    `${ALIAS}/${STORAGE_BUCKET}/${storageKey}`,
  ]);
}

/**
 * Uploads one part through the pre-signed URL the API issued.
 *
 * The PUT is a signed request, so it has to carry the signature untouched; only
 * the transport scheme is rewritten (see the module header). The body is staged
 * inside the container first because the URL's host only resolves there.
 *
 * Returns the response status: a non-2xx is returned, not thrown, so the upload
 * seam can assert on what the server says about a rejected part.
 */
export async function putPart(signedPartUrl: string, bytes: Buffer, contentType: string): Promise<number> {
  const staged = path.join(tmpdir(), 'cross-layer-part.bin');
  writeFileSync(staged, bytes);
  await run('docker', ['cp', staged, `${CONTAINERS.api}:/tmp/cross-layer-part.bin`]);

  const url = toTransportUrl(signedPartUrl);
  const { stdout } = await run(
    'docker',
    [
      'exec',
      CONTAINERS.api,
      'sh',
      '-c',
      `curl -sS -X PUT --data-binary @/tmp/cross-layer-part.bin ` +
        `-H 'Content-Type: ${contentType}' -o /dev/null -w '%{http_code}' '${url}'`,
    ],
    { maxBuffer: 4 * 1024 * 1024 },
  );

  return Number.parseInt(stdout.trim(), 10);
}

/** Bytes currently stored at `storageKey`, or `null` when the key is absent. */
export async function getObject(storageKey: string): Promise<Buffer | null> {
  try {
    const { stdout } = await run(
      'docker',
      [
        'exec',
        CONTAINERS.minio,
        'mc',
        'cat',
        `${ALIAS}/${STORAGE_BUCKET}/${storageKey}`,
      ],
      { maxBuffer: 32 * 1024 * 1024 },
    );
    return Buffer.from(stdout, 'binary');
  } catch {
    return null;
  }
}

export interface FetchedObject {
  readonly status: number;
  readonly body: Buffer;
}

/**
 * Fetches a pre-signed URL from inside the compose network and returns the raw
 * response.
 *
 * The status is returned rather than thrown because the export seam asserts on
 * what a tampered URL produces, which is a status and not an exception. The body
 * is base64'd out of the container because it is binary and a pipe would
 * corrupt it.
 */
export async function fetchSignedUrl(signedUrl: string): Promise<FetchedObject> {
  const url = toTransportUrl(signedUrl);

  let status: number;
  try {
    const { stdout } = await run(
      'docker',
      [
        'exec',
        CONTAINERS.api,
        'sh',
        '-c',
        `curl -sS -o /tmp/xl-body -w '%{http_code}' '${url}'`,
      ],
      { maxBuffer: 1024 * 1024 },
    );
    status = Number.parseInt(stdout.trim(), 10);
  } catch (error) {
    const failure = error as { stderr?: string; message?: string };
    throw new ObjectStorageError(
      'Could not fetch the signed URL from inside the compose network. Its host is the ' +
        'compose-network name, so the fetch must run in a container rather than on the ' +
        'host. Check that the api service is running.',
      `${failure.stderr ?? ''}${failure.message ?? ''}`.slice(0, 500),
    );
  }

  let body = Buffer.alloc(0);
  if (status === 200) {
    const { stdout } = await run(
      'docker',
      ['exec', CONTAINERS.api, 'base64', '-w', '0', '/tmp/xl-body'],
      { maxBuffer: 64 * 1024 * 1024 },
    );
    body = Buffer.from(stdout.replace(/\s+/g, ''), 'base64');
  }

  return { status, body };
}
