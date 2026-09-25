import { describe, expect, it } from 'vitest';
import {
  INVALIDATION_DEBOUNCE_MS,
  POLL_ACTIVE_VISIBLE_MS,
  POLL_HIDDEN_MS,
  SSE_BASE_DELAY_MS,
  SSE_JITTER_MS,
  SSE_MAX_DELAY_MS,
  SSE_MAX_FAILURES_BEFORE_POLL,
  computeSseBackoffMs,
  formatApproximatePercent,
  getProgressPollInterval,
  isTerminalProgressStatus,
} from '../../useProgressStream.js';

describe('computeSseBackoffMs sequence', () => {
  it('doubles from the base with zero jitter', () => {
    expect(computeSseBackoffMs(0, 0)).toBe(SSE_BASE_DELAY_MS);
    expect(computeSseBackoffMs(1, 0)).toBe(2000);
    expect(computeSseBackoffMs(2, 0)).toBe(4000);
    expect(computeSseBackoffMs(3, 0)).toBe(8000);
  });

  it('caps at 30s', () => {
    expect(computeSseBackoffMs(5, 0)).toBe(SSE_MAX_DELAY_MS);
    expect(computeSseBackoffMs(10, 0)).toBe(SSE_MAX_DELAY_MS);
    expect(computeSseBackoffMs(100, 0)).toBe(SSE_MAX_DELAY_MS);
  });

  it('adds bounded jitter', () => {
    expect(computeSseBackoffMs(0, 0.5)).toBe(SSE_BASE_DELAY_MS + Math.floor(0.5 * SSE_JITTER_MS));
    expect(computeSseBackoffMs(1, 1)).toBe(2000 + SSE_JITTER_MS);
  });

  it('never exceeds the cap even with jitter', () => {
    expect(computeSseBackoffMs(10, 0.999)).toBe(SSE_MAX_DELAY_MS);
    expect(computeSseBackoffMs(5, 1)).toBe(SSE_MAX_DELAY_MS);
  });

  it('treats invalid attempts as zero', () => {
    expect(computeSseBackoffMs(-2, 0)).toBe(SSE_BASE_DELAY_MS);
    expect(computeSseBackoffMs(Number.NaN, 0)).toBe(SSE_BASE_DELAY_MS);
  });

  it('exposes the fallback threshold', () => {
    expect(SSE_MAX_FAILURES_BEFORE_POLL).toBe(3);
    expect(INVALIDATION_DEBOUNCE_MS).toBe(500);
  });
});

describe('isTerminalProgressStatus', () => {
  it('matches terminal statuses case-insensitively', () => {
    expect(isTerminalProgressStatus('Completed')).toBe(true);
    expect(isTerminalProgressStatus('completed')).toBe(true);
    expect(isTerminalProgressStatus('Failed')).toBe(true);
    expect(isTerminalProgressStatus('CANCELLED')).toBe(true);
  });

  it('rejects active and empty statuses', () => {
    expect(isTerminalProgressStatus('Running')).toBe(false);
    expect(isTerminalProgressStatus('Pending')).toBe(false);
    expect(isTerminalProgressStatus(undefined)).toBe(false);
    expect(isTerminalProgressStatus(null)).toBe(false);
    expect(isTerminalProgressStatus('')).toBe(false);
  });
});

describe('getProgressPollInterval', () => {
  it('polls fast when visible and active', () => {
    expect(getProgressPollInterval({ isVisible: true, isTerminal: false })).toBe(POLL_ACTIVE_VISIBLE_MS);
    expect(POLL_ACTIVE_VISIBLE_MS).toBeGreaterThanOrEqual(2000);
    expect(POLL_ACTIVE_VISIBLE_MS).toBeLessThanOrEqual(5000);
  });

  it('polls slow when hidden', () => {
    const interval = getProgressPollInterval({ isVisible: false, isTerminal: false });
    expect(interval).toBe(POLL_HIDDEN_MS);
    expect(POLL_HIDDEN_MS).toBeGreaterThanOrEqual(15000);
    expect(POLL_HIDDEN_MS).toBeLessThanOrEqual(30000);
  });

  it('stops at terminal states', () => {
    expect(getProgressPollInterval({ isVisible: true, isTerminal: true })).toBe(false);
    expect(getProgressPollInterval({ isVisible: false, isTerminal: true })).toBe(false);
  });
});

describe('formatApproximatePercent', () => {
  it('prefixes with tilde and clamps', () => {
    expect(formatApproximatePercent(42)).toBe('~42%');
    expect(formatApproximatePercent(42.6)).toBe('~43%');
    expect(formatApproximatePercent(-5)).toBe('~0%');
    expect(formatApproximatePercent(150)).toBe('~100%');
    expect(formatApproximatePercent(Number.NaN)).toBe('~0%');
  });
});
