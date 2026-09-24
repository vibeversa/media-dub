/**
 * Client content fingerprint for duplicate detection (Task 023).
 *
 * The fingerprint binds identity (name/size/type) to sampled content (head +
 * tail chunks) so a file changed on disk between sessions mismatches and the
 * upload restarts instead of resuming foreign bytes. It is a UX hint only:
 * server checksums stay authoritative. Pure and dependency-free (FNV-1a, no
 * `SubtleCrypto` async surface) so unit tests stay hermetic. No media bytes
 * ever leave this module — only the hex digest is returned and persisted.
 */

export const FINGERPRINT_VERSION = 1;

/** Sample window per file edge (64 KiB). */
export const FINGERPRINT_SAMPLE_BYTES = 64 * 1024;

/** FNV-1a (32-bit) over bytes, returned as 8 lowercase hex chars. */
export function hashBytes(bytes: Uint8Array): string {
  let hash = 0x811c9dc5;
  for (let i = 0; i < bytes.length; i += 1) {
    hash ^= bytes[i] ?? 0;
    hash = Math.imul(hash, 0x01000193);
  }
  return (hash >>> 0).toString(16).padStart(8, '0');
}

function toBytes(view: ArrayBuffer): Uint8Array {
  return new Uint8Array(view);
}

/**
 * Reads a blob slice. Prefers `arrayBuffer()` and falls back to `FileReader`
 * (jsdom implements `Blob` without `arrayBuffer()`).
 */
function readSlice(blob: Blob): Promise<Uint8Array> {
  if (typeof blob.arrayBuffer === 'function') {
    return blob.arrayBuffer().then((view) => toBytes(view));
  }
  return new Promise<Uint8Array>((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = (): void => {
      resolve(toBytes(reader.result as ArrayBuffer));
    };
    reader.onerror = (): void => {
      reject(reader.error ?? new Error('READ_FAILED'));
    };
    reader.readAsArrayBuffer(blob);
  });
}

/**
 * Fingerprints a file as `v1:{size}:{type}:{head}:{tail}`. Head/tail are
 * FNV-1a digests of the first/last sample windows (the whole file when it is
 * smaller than one window). Rejects only when the bytes cannot be read.
 */
export async function fingerprintFile(file: File): Promise<string> {
  const size = file.size;
  const headEnd = Math.min(FINGERPRINT_SAMPLE_BYTES, size);
  const tailStart = Math.max(0, size - FINGERPRINT_SAMPLE_BYTES);
  const [head, tail] = await Promise.all([
    readSlice(file.slice(0, headEnd)),
    readSlice(file.slice(tailStart, size)),
  ]);
  const type = file.type ?? '';
  return `v${FINGERPRINT_VERSION}:${size}:${type}:${hashBytes(toBytes(head))}:${hashBytes(toBytes(tail))}`;
}

/** True when both fingerprints are non-empty and identical. */
export function isSameFingerprint(left: string, right: string): boolean {
  if (left === '' || right === '') {
    return false;
  }
  return left === right;
}
