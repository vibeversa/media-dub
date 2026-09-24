import { describe, expect, it } from 'vitest';
import {
  PREFLIGHT_CHARS_PER_SEGMENT,
  PREFLIGHT_FALLBACK_SEGMENTS,
  PREFLIGHT_MAX_SEGMENTS,
  PREFLIGHT_PRICE_TRANSLATION_PER_CHAR,
  PREFLIGHT_PRICE_TRANSCRIPTION_PER_MIN,
  PREFLIGHT_PRICE_TTS_PER_CHAR,
  PREFLIGHT_PRICE_VERSION,
  PREFLIGHT_SECONDS_PER_SEGMENT,
  confirmBlocks,
  estimateRunCostUsd,
  isQuotaExhausted,
  parseClonedVoices,
  sumSpeakerSegments,
} from '../usePreflight.js';
import type { PreflightSnapshot } from '../usePreflight.js';

function snapshot(overrides: Partial<PreflightSnapshot> = {}): PreflightSnapshot {
  return {
    projectId: 'prj_1',
    projectName: 'Pilot',
    status: 'MediaReady',
    configHash: 'cfg_abc',
    settingsVersion: 1,
    sourceLanguage: 'en',
    targetLanguage: 'es',
    isArchived: false,
    estimate: estimateRunCostUsd(10),
    currency: 'USD',
    monthToDate: 12.5,
    quota: { remaining: 5, resetsAt: '2026-09-25T00:00:00Z' },
    clonedVoices: [],
    speakerTotal: 0,
    speakersTruncated: false,
    activeRun: null,
    ...overrides,
  };
}

describe('estimateRunCostUsd (server-formula mirror)', () => {
  it('prices the documented defaults (10 segments ≈ $0.0667)', () => {
    const estimate = estimateRunCostUsd(10);
    const expected =
      ((10 * PREFLIGHT_SECONDS_PER_SEGMENT) / 60) * PREFLIGHT_PRICE_TRANSCRIPTION_PER_MIN +
      10 * PREFLIGHT_CHARS_PER_SEGMENT * PREFLIGHT_PRICE_TRANSLATION_PER_CHAR +
      10 * PREFLIGHT_CHARS_PER_SEGMENT * PREFLIGHT_PRICE_TTS_PER_CHAR;
    expect(estimate.amountUsd).toBeCloseTo(expected, 10);
    expect(estimate.amountUsd).toBeCloseTo(0.0667, 4);
    expect(estimate.segmentCount).toBe(10);
    expect(estimate.priceVersion).toBe(PREFLIGHT_PRICE_VERSION);
  });

  it('falls back to 10 segments for zero/unknown counts (server branch mirror)', () => {
    expect(estimateRunCostUsd(0).segmentCount).toBe(PREFLIGHT_FALLBACK_SEGMENTS);
    expect(estimateRunCostUsd(-3).segmentCount).toBe(PREFLIGHT_FALLBACK_SEGMENTS);
    expect(estimateRunCostUsd(Number.NaN).segmentCount).toBe(PREFLIGHT_FALLBACK_SEGMENTS);
  });

  it('floors fractional counts and clamps to the 2000 server ceiling', () => {
    expect(estimateRunCostUsd(7.9).segmentCount).toBe(7);
    expect(estimateRunCostUsd(5000).segmentCount).toBe(PREFLIGHT_MAX_SEGMENTS);
  });

  it('scales linearly with segments', () => {
    expect(estimateRunCostUsd(20).amountUsd).toBeCloseTo(estimateRunCostUsd(10).amountUsd * 2, 10);
  });
});

describe('sumSpeakerSegments', () => {
  it('sums finite positive counts and ignores the rest', () => {
    expect(sumSpeakerSegments([{ segmentCount: 3 }, { segmentCount: 4.7 }, {}])).toBe(7);
    expect(
      sumSpeakerSegments([{ segmentCount: -2 }, { segmentCount: Number.NaN }, { segmentCount: '9' }, {}]),
    ).toBe(0);
  });
});

describe('parseClonedVoices', () => {
  it('returns [] without items', () => {
    expect(parseClonedVoices(undefined)).toEqual([]);
  });

  it('picks only Cloned assigned voices with display fallbacks', () => {
    const items = [
      {
        id: 'spk_1',
        speakerKey: 'SPK1',
        displayName: 'Alice',
        segmentCount: 4,
        assignedVoice: { voiceProfileId: 'vp_1', voiceId: 'voice_clone', provider: 'acme', language: 'es', type: 'Cloned' },
      },
      {
        id: 'spk_2',
        speakerKey: 'SPK2',
        displayName: '',
        assignedVoice: { voiceId: 'voice_stock', type: 'Stock' },
      },
      { id: 'spk_3', speakerKey: '', assignedVoice: null },
      { id: 'spk_4', assignedVoice: { voiceId: '', type: 'Cloned' } },
    ];
    // Stock voices need no consent; empty voice ids carry no actionable warning.
    expect(parseClonedVoices(items)).toEqual([
      { speakerId: 'spk_1', speakerKey: 'SPK1', displayName: 'Alice', voiceId: 'voice_clone' },
    ]);
  });
});

describe('isQuotaExhausted', () => {
  it('treats zero and negatives as exhausted', () => {
    expect(isQuotaExhausted(0)).toBe(true);
    expect(isQuotaExhausted(-1)).toBe(true);
    expect(isQuotaExhausted(1)).toBe(false);
  });
});

describe('confirmBlocks (enablement matrix)', () => {
  it('clears a clean snapshot', () => {
    expect(
      confirmBlocks(snapshot(), { consentAcknowledged: false, overrideReason: '', starting: false, inConflict: false }),
    ).toEqual([]);
  });

  it('blocks while a start is in flight', () => {
    expect(
      confirmBlocks(snapshot(), { consentAcknowledged: true, overrideReason: '', starting: true, inConflict: false }),
    ).toEqual(['starting']);
  });

  it('blocks conflicts ahead of everything actionable', () => {
    expect(
      confirmBlocks(snapshot(), { consentAcknowledged: true, overrideReason: 'x', starting: false, inConflict: true }),
    ).toEqual(['conflict']);
  });

  it('blocks unacknowledged cloning consent only when cloned voices exist', () => {
    const withClone = snapshot({
      clonedVoices: [{ speakerId: 'spk_1', speakerKey: 'SPK1', displayName: 'Alice', voiceId: 'voice_clone' }],
    });
    expect(
      confirmBlocks(withClone, { consentAcknowledged: false, overrideReason: '', starting: false, inConflict: false }),
    ).toEqual(['consent']);
    expect(
      confirmBlocks(withClone, { consentAcknowledged: true, overrideReason: '', starting: false, inConflict: false }),
    ).toEqual([]);
  });

  it('requires an override reason only when the quota is exhausted', () => {
    const exhausted = snapshot({ quota: { remaining: 0, resetsAt: '2026-09-25T00:00:00Z' } });
    expect(
      confirmBlocks(exhausted, { consentAcknowledged: false, overrideReason: '   ', starting: false, inConflict: false }),
    ).toEqual(['quota']);
    expect(
      confirmBlocks(exhausted, { consentAcknowledged: false, overrideReason: 'launch cannot wait', starting: false, inConflict: false }),
    ).toEqual([]);
  });

  it('accumulates independent blocks in stable order', () => {
    const stuck = snapshot({
      clonedVoices: [{ speakerId: 'spk_1', speakerKey: 'SPK1', displayName: 'Alice', voiceId: 'voice_clone' }],
      quota: { remaining: 0, resetsAt: '2026-09-25T00:00:00Z' },
    });
    expect(
      confirmBlocks(stuck, { consentAcknowledged: false, overrideReason: '', starting: true, inConflict: true }),
    ).toEqual(['starting', 'conflict', 'consent', 'quota']);
  });
});
