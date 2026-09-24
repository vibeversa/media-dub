import { describe, expect, it } from 'vitest';
import {
  QUOTA_WARNING_RATIO,
  getBacklogSlice,
  getCostSlice,
  getCountsSlice,
  getQuotaSlice,
  getRecentOutputs,
  getStorageSlice,
  getStorageUsageRatio,
  getWarningViews,
  isDailyQuotaExhausted,
  isEmptyTenant,
  isQuotaBlocked,
  isStorageBlocked,
  isStorageWarning,
} from '../api.js';

describe('quota thresholds (R2)', () => {
  it('pins the warning ratio at 80%', () => {
    expect(QUOTA_WARNING_RATIO).toBe(0.8);
  });

  it('computes the storage ratio defensively (never NaN)', () => {
    expect(getStorageUsageRatio(undefined)).toBe(0);
    expect(getStorageUsageRatio({ usedBytes: 50, quotaBytes: 0 })).toBe(0);
    expect(getStorageUsageRatio({ usedBytes: 0, quotaBytes: 100 })).toBe(0);
    expect(getStorageUsageRatio({ usedBytes: 80, quotaBytes: 100 })).toBeCloseTo(0.8);
    expect(getStorageUsageRatio({ usedBytes: 120, quotaBytes: 100 })).toBeCloseTo(1.2);
  });

  it('warns at 80% but blocks only at 100%', () => {
    expect(isStorageWarning({ usedBytes: 79, quotaBytes: 100 })).toBe(false);
    expect(isStorageWarning({ usedBytes: 80, quotaBytes: 100 })).toBe(true);
    expect(isStorageWarning(undefined)).toBe(false);
    expect(isStorageBlocked({ usedBytes: 99, quotaBytes: 100 })).toBe(false);
    expect(isStorageBlocked({ usedBytes: 100, quotaBytes: 100 })).toBe(true);
    expect(isStorageBlocked({ usedBytes: 150, quotaBytes: 100 })).toBe(true);
  });

  it('treats daily quota exhaustion as blocked', () => {
    expect(isDailyQuotaExhausted(undefined)).toBe(false);
    expect(isDailyQuotaExhausted({ remaining: 1, resetsAt: '2026-09-25T00:00:00Z' })).toBe(false);
    expect(isDailyQuotaExhausted({ remaining: 0, resetsAt: '2026-09-25T00:00:00Z' })).toBe(true);
    expect(isDailyQuotaExhausted({ remaining: -2, resetsAt: '2026-09-25T00:00:00Z' })).toBe(true);
  });

  it('blocks costly actions on storage-full or daily exhaustion', () => {
    const healthy = { usedBytes: 10, quotaBytes: 100 };
    const full = { usedBytes: 100, quotaBytes: 100 };
    const open = { remaining: 5, resetsAt: '2026-09-25T00:00:00Z' };
    const exhausted = { remaining: 0, resetsAt: '2026-09-25T00:00:00Z' };
    expect(isQuotaBlocked(healthy, open)).toBe(false);
    expect(isQuotaBlocked(full, open)).toBe(true);
    expect(isQuotaBlocked(healthy, exhausted)).toBe(true);
    expect(isQuotaBlocked(undefined, exhausted)).toBe(true);
  });
});

describe('section parsing (R3)', () => {
  it('rejects missing/invalid counts (per-card error, never a blank page)', () => {
    expect(getCountsSlice(undefined)).toBeUndefined();
    expect(getCountsSlice({} as never)).toBeUndefined();
    expect(
      getCountsSlice({ projectCounts: { active: 1, archived: 0, total: 1 } }),
    ).toEqual({ active: 1, archived: 0, total: 1 });
    expect(getCountsSlice({ projectCounts: { active: 'x', archived: 0, total: 1 } } as never)).toBeUndefined();
  });

  it('detects the empty tenant (all-zero → onboarding, not zero cards)', () => {
    expect(isEmptyTenant(undefined)).toBe(false);
    expect(isEmptyTenant({ projectCounts: { active: 0, archived: 0, total: 0 } } as never)).toBe(true);
    expect(isEmptyTenant({ projectCounts: { active: 1, archived: 0, total: 1 } } as never)).toBe(false);
  });

  it('parses rows defensively and flags non-array sections', () => {
    expect(getRecentOutputs(undefined)).toBeUndefined();
    expect(getRecentOutputs({ recentOutputs: null } as never)).toBeUndefined();
    expect(getRecentOutputs({ recentOutputs: [] } as never)).toEqual([]);
    expect(getWarningViews(undefined)).toBeUndefined();
    expect(getWarningViews({ warnings: {} } as never)).toBeUndefined();
    expect(getWarningViews({ warnings: [] } as never)).toEqual([]);
    expect(getStorageSlice({ storage: { usedBytes: 1, quotaBytes: 2 } } as never)).toEqual({
      usedBytes: 1,
      quotaBytes: 2,
    });
    expect(getStorageSlice({} as never)).toBeUndefined();
    expect(getCostSlice({ cost: { monthToDate: 3.5, currency: 'EUR' } } as never)).toEqual({
      monthToDate: 3.5,
      currency: 'EUR',
    });
    expect(getQuotaSlice({ quota: { remaining: 4, resetsAt: '2026-09-25T00:00:00Z' } } as never)).toEqual({
      remaining: 4,
      resetsAt: '2026-09-25T00:00:00Z',
    });
    expect(getBacklogSlice({ backlog: { pendingReviews: 2, runningJobs: 1 } } as never)).toEqual({
      pendingReviews: 2,
      runningJobs: 1,
    });
    expect(getBacklogSlice({} as never)).toBeUndefined();
  });
});
