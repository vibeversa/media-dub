// Task 039B: normalizeError branch matrix.
//
// Exercises every shape guard in `normalizeError`: the NOT_AUTHENTICATED
// envelope variants, network vs abort discrimination, ApiError-like shape
// rejections, per-status kind/hint fallbacks, empty-message defaults, and
// the unknown-value tail. Recovery hints are asserted per §11.6 (every
// failure maps to an action, never a bare code).
import { describe, expect, it } from 'vitest';
import { NETWORK_ERROR, NOT_AUTHENTICATED } from '../errors/index.js';
import { normalizeError } from '../errors/index.js';

describe('NOT_AUTHENTICATED envelope matrix', () => {
  it('keeps an explicit message and correlation id', () => {
    const err = normalizeError(
      { code: NOT_AUTHENTICATED, message: 'custom', correlationId: 'corr-a' },
      { method: 'POST' },
    );
    expect(err).toMatchObject({ kind: 'Auth', code: NOT_AUTHENTICATED, message: 'custom', correlationId: 'corr-a', retryable: false });
    expect(err.recoveryHint).toContain('Sign in again');
  });

  it('defaults missing message, blank id, and absent fallback id', () => {
    const bare = normalizeError({ code: NOT_AUTHENTICATED }, { method: 'POST' });
    expect(bare.message).toBe('Not authenticated.');
    expect(bare.correlationId.length).toBeGreaterThan(0);
    const blank = normalizeError(
      { code: NOT_AUTHENTICATED, message: 7, correlationId: '' },
      { method: 'POST', fallbackCorrelationId: 'req-9' },
    );
    expect(blank.message).toBe('Not authenticated.');
    expect(blank.correlationId).toBe('req-9');
  });
});

describe('network vs abort discrimination', () => {
  it('treats message-matching Errors as network failures', () => {
    const err = normalizeError(new Error('Network request failed'), { method: 'GET' });
    expect(err.code).toBe(NETWORK_ERROR);
    expect(err.retryable).toBe(true);
    expect(err.recoveryHint).toContain('offline');
  });

  it('never mistakes aborts for network failures', () => {
    const plain = normalizeError({ name: 'AbortError' }, { method: 'GET' });
    expect(plain.code).toBe('REQUEST_ABORTED');
    expect(plain.retryable).toBe(false);
    expect(plain.recoveryHint).toContain('cancelled');
    const dom = normalizeError(new DOMException('Aborted', 'AbortError'), { method: 'GET' });
    expect(dom.code).toBe('REQUEST_ABORTED');
    const named = normalizeError({ name: 'TypeError', message: 'x' }, { method: 'GET' });
    expect(named.code).toBe('UNKNOWN_ERROR');
  });

  it('falls back to UNKNOWN_ERROR for plain values', () => {
    expect(normalizeError(null)).toMatchObject({ kind: 'Unknown', code: 'UNKNOWN_ERROR' });
    expect(normalizeError('boom', { fallbackCorrelationId: 'req-1' })).toMatchObject({
      code: 'UNKNOWN_ERROR',
      message: 'An unexpected error occurred.',
      correlationId: 'req-1',
    });
    expect(normalizeError(new Error(''), { method: 'GET' }).message).toBe('An unexpected error occurred.');
    expect(normalizeError(undefined).correlationId.length).toBeGreaterThan(0);
  });
});

describe('ApiError-like shape guards', () => {
  it('rejects non-objects and partial envelopes', () => {
    expect(normalizeError(42).code).toBe('UNKNOWN_ERROR');
    expect(normalizeError({ status: 'x', code: 'C', message: 'm' }).code).toBe('UNKNOWN_ERROR');
    expect(normalizeError({ status: 500, code: 7, message: 'm' }).code).toBe('UNKNOWN_ERROR');
    expect(normalizeError({ status: 500, code: 'C' }).code).toBe('UNKNOWN_ERROR');
  });

  it('normalizes missing correlation ids and non-object details', () => {
    const err = normalizeError(
      { status: 500, code: 'INTERNAL_ERROR', message: 'm', correlationId: 9, details: 'nope' },
      { method: 'GET', fallbackCorrelationId: 'req-2' },
    );
    expect(err.correlationId).toBe('req-2');
    expect(err.details).toEqual({});
    const kept = normalizeError(
      { status: 500, code: 'INTERNAL_ERROR', message: '', correlationId: 'corr-k', details: { a: 1 } },
    );
    expect(kept.message).toBe('An unexpected error occurred.');
    expect(kept.details).toEqual({ a: 1 });
  });
});

describe('status kind/hint fallback matrix', () => {
  const cases = [
    [400, 'Validation', 'highlighted fields'],
    [404, 'Validation', 'deleted'],
    [410, 'Validation', 'Report this ID'],
    [413, 'Validation', 'Report this ID'],
    [415, 'Validation', 'Report this ID'],
    [422, 'Validation', 'Report this ID'],
    [401, 'Auth', 'Sign in again'],
    [403, 'Auth', 'tenant admin'],
    [409, 'Conflict', 'changed while you worked'],
    [429, 'Quota', 'Too many requests'],
    [418, 'Unknown', 'went wrong'],
    [503, 'Unknown', 'went wrong'],
  ] as const;
  it.each(cases)('maps status %i to %s with an actionable hint', (status, kind, hint) => {
    const err = normalizeError(
      { status, code: 'BRAND_NEW_CODE', message: 'new', correlationId: 'corr-s', details: {} },
      { method: 'GET' },
    );
    expect(err.kind).toBe(kind);
    expect(err.status).toBe(status);
    expect(err.recoveryHint).toContain(hint);
  });

  it('marks retryable only for GET on retryable statuses with known codes', () => {
    expect(
      normalizeError({ status: 504, code: 'PROVIDER_FAILED', message: 'm', correlationId: 'c', details: {} }, { method: 'get' })
        .retryable,
    ).toBe(true);
    expect(
      normalizeError({ status: 504, code: 'PROVIDER_FAILED', message: 'm', correlationId: 'c', details: {} }, { method: 'POST' })
        .retryable,
    ).toBe(false);
  });
});
