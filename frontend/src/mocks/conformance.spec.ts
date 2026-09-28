// Task 039A R2: MSW taxonomy conformance. Asserts every taxonomy entry from
// `taxonomy.ts` is served by `handlers.ts` with the documented envelope.
// A missing handler fails as `MSW_HANDLER_MISSING:<id>` (never a generic
// network error); an envelope-incorrect handler fails on the shape assertion.
// Single fetch per entry: retry is forbidden (a retry would mask a missing or
// flaky handler). Config-only task: no feature test logic lives here.

import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { taxonomyHandlers } from './handlers.js';
import { taxonomyServer } from './server.js';
import { MOCK_BASE_URL, TAXONOMY, isErrorEnvelope, taxonomyEntry } from './taxonomy.js';

beforeAll(() => {
  taxonomyServer.listen({ onUnhandledRequest: 'error' });
});

afterEach(() => {
  taxonomyServer.resetHandlers();
});

afterAll(() => {
  taxonomyServer.close();
});

describe('MSW taxonomy conformance (039A R2)', () => {
  it('registers exactly one handler per taxonomy entry', () => {
    expect(taxonomyHandlers.length).toBe(TAXONOMY.length);
  });

  for (const entry of TAXONOMY) {
    it(`serves ${entry.id} with the documented envelope`, async () => {
      expect(taxonomyEntry(entry.id)).toBe(entry);
      let response: Response;
      try {
        response = await fetch(`${MOCK_BASE_URL}${entry.path}`);
      } catch {
        expect.unreachable(`MSW_HANDLER_MISSING:${entry.id}`);
      }
      expect(response.status).toBe(entry.status);
      const body: unknown = await response.json();
      if (entry.code === null) {
        const record = body as Record<string, unknown>;
        expect(record, entry.id).toMatchObject({ correlationId: `corr-taxonomy-${entry.id}` });
        expect('error' in record, entry.id).toBe(false);
        expect(record['data'], entry.id).toBeDefined();
        if (entry.id === 'partial') {
          expect(record['partial'], entry.id).toBe(true);
          expect(Array.isArray(record['warnings']), entry.id).toBe(true);
        }
        return;
      }
      if (!isErrorEnvelope(body)) {
        expect.unreachable(`MSW_ENVELOPE_INCORRECT:${entry.id}`);
      }
      expect(body.error.code).toBe(entry.code);
      expect(body.error.message.length).toBeGreaterThan(0);
      expect(body.error.correlationId).toBe(`corr-taxonomy-${entry.id}`);
      expect(typeof body.error.details).toBe('object');
      if (entry.id === 'stale-conflict') {
        expect(body.error.details).toEqual({ currentSelectionVersion: 4, currentVersionIds: ['ver_2'] });
      }
      if (entry.id === 'validation') {
        expect(body.error.details).toEqual({ title: ['Title is required.'] });
      }
    });
  }

  it('rejects non-envelopes in the shared guard', () => {
    expect(isErrorEnvelope(null)).toBe(false);
    expect(isErrorEnvelope('error')).toBe(false);
    expect(isErrorEnvelope({})).toBe(false);
    expect(isErrorEnvelope({ error: null })).toBe(false);
    expect(isErrorEnvelope({ error: { code: 1, message: 'x', correlationId: 'c', details: {} } })).toBe(false);
    expect(isErrorEnvelope(taxonomyEntry('internal-500').body)).toBe(true);
  });
});
