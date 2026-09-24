import { describe, expect, it } from 'vitest';
import {
  ALL_REJECTION_REASONS,
  CLIENT_PHASES,
  REJECTION_GUIDES,
  SERVER_PHASES,
  TERMINAL_PHASES,
  deriveValidationOutcome,
  reasonFromErrorCode,
  reasonFromSessionStatus,
} from '../uploadStates.js';

describe('phase sets', () => {
  it('keeps client and server phases disjoint', () => {
    const overlap = CLIENT_PHASES.filter((phase) => (SERVER_PHASES as readonly string[]).includes(phase));
    expect(overlap).toEqual([]);
  });

  it('marks ready/rejected/aborted terminal only', () => {
    expect([...TERMINAL_PHASES].sort()).toEqual(['aborted', 'ready', 'rejected']);
  });
});

describe('reasonFromErrorCode', () => {
  it('maps the three media codes', () => {
    expect(reasonFromErrorCode('DUPLICATE_MEDIA')).toBe('duplicate');
    expect(reasonFromErrorCode('MEDIA_UNSUPPORTED')).toBe('unsupported');
    expect(reasonFromErrorCode('MEDIA_CORRUPT')).toBe('corrupt');
  });

  it('falls back to unknown for anything else', () => {
    expect(reasonFromErrorCode('INTERNAL_ERROR')).toBe('unknown');
    expect(reasonFromErrorCode('UPLOAD_INCOMPLETE')).toBe('unknown');
    expect(reasonFromErrorCode('')).toBe('unknown');
  });
});

describe('reasonFromSessionStatus', () => {
  it('maps Duplicate and Expired', () => {
    expect(reasonFromSessionStatus('Duplicate')).toBe('duplicate');
    expect(reasonFromSessionStatus('Expired')).toBe('expired');
  });

  it('returns null for non-failures (Aborted is terminal, not a rejection)', () => {
    expect(reasonFromSessionStatus('Completed')).toBeNull();
    expect(reasonFromSessionStatus('InProgress')).toBeNull();
    expect(reasonFromSessionStatus('Aborted')).toBeNull();
    expect(reasonFromSessionStatus('Created')).toBeNull();
  });
});

describe('rejection guides (R4)', () => {
  it('covers every reason with a distinct next action', () => {
    expect(Object.keys(REJECTION_GUIDES).sort()).toEqual([...ALL_REJECTION_REASONS].sort());
    expect(REJECTION_GUIDES['duplicate'].action).toBe('use-existing');
    expect(REJECTION_GUIDES['unsupported'].action).toBe('pick-format');
    expect(REJECTION_GUIDES['corrupt'].action).toBe('reupload');
    expect(REJECTION_GUIDES['expired'].action).toBe('start-over');
    expect(REJECTION_GUIDES['unknown'].action).toBe('contact-support');
  });
});

describe('deriveValidationOutcome', () => {
  it('prefers terminal states over poll count', () => {
    expect(deriveValidationOutcome({ uploadStatus: 'Completed', projectStatus: 'MediaReady' }, 0)).toEqual({
      kind: 'ready',
    });
    expect(deriveValidationOutcome({ uploadStatus: 'Completed', projectStatus: 'MediaRejected' }, 0)).toEqual({
      kind: 'rejected',
      reason: 'unknown',
    });
    expect(deriveValidationOutcome({ uploadStatus: 'Duplicate', projectStatus: 'Uploading' }, 0)).toEqual({
      kind: 'rejected',
      reason: 'duplicate',
    });
    expect(deriveValidationOutcome({ uploadStatus: 'Expired', projectStatus: 'Uploading' }, 0)).toEqual({
      kind: 'rejected',
      reason: 'expired',
    });
    expect(deriveValidationOutcome({ uploadStatus: 'Aborted', projectStatus: 'Uploading' }, 0)).toEqual({
      kind: 'aborted',
    });
  });

  it('moves from validating to analyzing with poll progression', () => {
    expect(deriveValidationOutcome({ uploadStatus: 'Completed', projectStatus: 'Uploading' }, 0)).toEqual({
      kind: 'validating',
    });
    expect(deriveValidationOutcome({ uploadStatus: 'Completed', projectStatus: 'Uploading' }, 2)).toEqual({
      kind: 'validating',
    });
    expect(deriveValidationOutcome({ uploadStatus: 'Completed', projectStatus: 'Uploading' }, 3)).toEqual({
      kind: 'analyzing',
    });
  });

  it('waits while parts are still in flight', () => {
    expect(deriveValidationOutcome({ uploadStatus: 'InProgress', projectStatus: 'Uploading' }, 9)).toEqual({
      kind: 'waiting',
    });
  });
});
