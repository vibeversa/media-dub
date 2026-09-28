// Delta: upload fingerprint remaining-branch closure.
//
// Supplements fingerprint.test (hashing, stability, change detection)
// with the read-slice transports (arrayBuffer fast path, FileReader
// fallback success + failure legs), missing-type fingerprints, large-file
// head/tail windowing, and comparison guards. Synthetic bytes only.
//
// Intentional-exclusion candidate: `hashBytes`' `bytes[i] ?? 0` fallback
// is defensive — the loop is bounded by `bytes.length`, so the index is
// never out of range under the typed API.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { FINGERPRINT_SAMPLE_BYTES, FINGERPRINT_VERSION, fingerprintFile, hashBytes, isSameFingerprint } from '../fingerprint.js';

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe('readSlice transports', () => {
  it('prefers arrayBuffer when the slice offers it', async () => {
    const bytes = new Uint8Array([9, 9, 9]);
    const fakeFile = {
      size: 3,
      type: 'video/mp4',
      slice: (start: number, end: number) => ({
        arrayBuffer: async (): Promise<ArrayBuffer> => bytes.slice(start, end).buffer as ArrayBuffer,
      }),
    } as unknown as File;
    const printed = await fingerprintFile(fakeFile);
    expect(printed.startsWith(`v${String(FINGERPRINT_VERSION)}:3:video/mp4:`)).toBe(true);
    expect(printed.split(':').length).toBe(5);
  });

  it('rejects when bytes cannot be read (recovery: retry upload)', async () => {
    const failingReader = vi.fn().mockImplementation(function (this: unknown) {
      const self = this as { onload: (() => void) | null; onerror: (() => void) | null; error: DOMException | null; readAsArrayBuffer: () => void };
      self.error = null;
      self.readAsArrayBuffer = (): void => {
        self.onerror?.();
      };
      return self;
    });
    vi.stubGlobal('FileReader', failingReader);
    const blob = new Blob([new Uint8Array([1, 2, 3])]);
    const original = (blob as unknown as { arrayBuffer?: unknown }).arrayBuffer;
    Object.defineProperty(blob, 'arrayBuffer', { value: undefined, configurable: true });
    const fakeFile = { size: 3, type: 'video/mp4', slice: () => blob } as unknown as File;
    await expect(fingerprintFile(fakeFile)).rejects.toThrow('READ_FAILED');
    void original;
  });

  it('surfaces reader errors verbatim when present', async () => {
    const domError = new DOMException('denied', 'NotReadableError');
    const failingReader = vi.fn().mockImplementation(function (this: unknown) {
      const self = this as { onerror: (() => void) | null; error: DOMException | null; readAsArrayBuffer: () => void };
      self.error = domError;
      self.readAsArrayBuffer = (): void => {
        self.onerror?.();
      };
      return self;
    });
    vi.stubGlobal('FileReader', failingReader);
    const blob = new Blob([new Uint8Array([1])]);
    Object.defineProperty(blob, 'arrayBuffer', { value: undefined, configurable: true });
    const fakeFile = { size: 1, type: 'video/mp4', slice: () => blob } as unknown as File;
    await expect(fingerprintFile(fakeFile)).rejects.toBe(domError);
  });
});

describe('fingerprint shape branches', () => {
  it('fingerprints untyped payloads with an empty type slot', async () => {
    const fakeFile = {
      size: 2,
      slice: (start: number, end: number) => new Blob([new Uint8Array([4, 5])]).slice(start, end),
    } as unknown as File;
    const printed = await fingerprintFile(fakeFile);
    expect(printed.startsWith(`v${String(FINGERPRINT_VERSION)}:2::`)).toBe(true);
  });

  it('windows head/tail on files larger than the sample', async () => {
    const big = new Uint8Array(FINGERPRINT_SAMPLE_BYTES + 100);
    for (let i = 0; i < big.length; i += 1) big[i] = i % 251;
    const file = new File([big], 'big.mp4', { type: 'video/mp4' });
    const printed = await fingerprintFile(file);
    expect(printed.startsWith(`v${String(FINGERPRINT_VERSION)}:${String(big.length)}:video/mp4:`)).toBe(true);
    const head = hashBytes(big.slice(0, FINGERPRINT_SAMPLE_BYTES));
    expect(printed).toContain(head);
  });

  it('guards comparisons against either side missing', () => {
    expect(isSameFingerprint('', 'v1:x')).toBe(false);
    expect(isSameFingerprint('v1:x', '')).toBe(false);
    expect(isSameFingerprint('v1:x', 'v1:x')).toBe(true);
    expect(isSameFingerprint('v1:x', 'v1:y')).toBe(false);
  });
});
