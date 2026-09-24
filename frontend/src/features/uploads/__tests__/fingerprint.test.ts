import { describe, expect, it } from 'vitest';
import { fingerprintFile, hashBytes, isSameFingerprint } from '../fingerprint.js';

function file(name: string, type: string, bytes: number[]): File {
  return new File([new Uint8Array(bytes)], name, { type });
}

describe('hashBytes (FNV-1a)', () => {
  it('is deterministic lowercase hex', () => {
    const first = hashBytes(new Uint8Array([1, 2, 3]));
    expect(first).toMatch(/^[0-9a-f]{8}$/);
    expect(hashBytes(new Uint8Array([1, 2, 3]))).toBe(first);
  });

  it('differs on content', () => {
    expect(hashBytes(new Uint8Array([1]))).not.toBe(hashBytes(new Uint8Array([2])));
  });
});

describe('fingerprintFile', () => {
  it('is stable for identical files', async () => {
    const left = await fingerprintFile(file('clip.mp4', 'video/mp4', [1, 2, 3]));
    const right = await fingerprintFile(file('clip.mp4', 'video/mp4', [1, 2, 3]));
    expect(left).toBe(right);
    expect(isSameFingerprint(left, right)).toBe(true);
  });

  it('changes when content changes (same name and size)', async () => {
    const left = await fingerprintFile(file('clip.mp4', 'video/mp4', [1, 2, 3, 4]));
    const right = await fingerprintFile(file('clip.mp4', 'video/mp4', [1, 2, 9, 4]));
    expect(isSameFingerprint(left, right)).toBe(false);
  });

  it('changes when size changes', async () => {
    const left = await fingerprintFile(file('clip.mp4', 'video/mp4', [1, 2, 3]));
    const right = await fingerprintFile(file('clip.mp4', 'video/mp4', [1, 2, 3, 4]));
    expect(isSameFingerprint(left, right)).toBe(false);
  });

  it('changes when the type changes', async () => {
    const left = await fingerprintFile(file('clip.mp4', 'video/mp4', [1, 2, 3]));
    const right = await fingerprintFile(file('clip.mp4', 'video/mp4', [1, 2, 3]));
    void right;
    const other = await fingerprintFile(file('clip.mp4', 'audio/mpeg', [1, 2, 3]));
    expect(isSameFingerprint(left, other)).toBe(false);
  });

  it('rejects empty fingerprints in comparisons', () => {
    expect(isSameFingerprint('', '')).toBe(false);
    expect(isSameFingerprint('v1:x', '')).toBe(false);
  });
});
