// Task 039B: api-infra state-matrix gap closure.
//
// Direct specs for the uncovered seams in `src/api/smoke.ts` (frozen-contract
// proofs) and `src/api/client/correlation.ts` (header builders + id
// extraction). All fixtures are synthetic; `loadProjectPage` runs against a
// stub client, so no network or backend boot is required (R3).
import { describe, expect, it } from 'vitest';
import { ApiError } from '../client/index.js';
import type { ApiClient } from '../client/index.js';
import { CORRELATION_HEADER, buildCorrelationHeaders, extractCorrelationId, newCorrelationId } from '../client/correlation.js';
import {
  createClient,
  describeEnvelope,
  errorCodeOf,
  isApiError,
  loadProjectPage,
  mutationBody,
  outputStateOf,
  selectionBody,
  usageSummary,
} from '../smoke.js';

describe('smoke contract proofs', () => {
  it('constructs a client without network access', () => {
    const client = createClient('http://localhost:5000', () => 'test-token');
    expect(client).toBeDefined();
    expect(typeof client.listProjects).toBe('function');
  });

  it('loads a project page through a stub client (synthetic envelope)', async () => {
    const stub = {
      listProjects: async () => ({
        items: [{ id: 'prj_1' }],
        page: 2,
        pageSize: 20,
        total: 1,
        hasMore: false,
      }),
    } as unknown as ApiClient;
    const page = await loadProjectPage(stub, 2);
    expect(page.total).toBe(1);
  });

  it('describes envelopes and builds mutation bodies without content', () => {
    expect(describeEnvelope({ eventType: 'stage.progress', schemaVersion: 1, correlationId: 'corr-1' } as never)).toBe(
      'stage.progress:1:corr-1',
    );
    expect(selectionBody(4)).toEqual({ expectedVersion: 4, selectedVersionIds: ['ver_01JABCDEF'] });
    const mutation = mutationBody(3);
    expect(mutation.expectedVersion).toBe(3);
    expect(mutation.reason.length).toBeGreaterThan(0);
  });

  it('classifies ApiError instances and reads frozen codes', () => {
    const error = new ApiError(500, 'INTERNAL_ERROR', 'boom', 'corr-1', {});
    expect(isApiError(error)).toBe(true);
    expect(isApiError(new Error('plain'))).toBe(false);
    expect(errorCodeOf({ error: { code: 'URL_EXPIRED', message: 'gone', correlationId: 'c', details: {} } })).toBe(
      'URL_EXPIRED',
    );
  });

  it('summarizes usage and output states as plain text (non-color signals)', () => {
    expect(usageSummary({ activeRuns: 2 } as never, { maxActiveProjects: 10 } as never)).toBe('2/10');
    expect(outputStateOf({ state: 'Ready', generationState: 'Final' } as never)).toBe('Ready/Final');
  });
});

describe('correlation matrix', () => {
  it('builds header pairs with explicit and minted ids', () => {
    expect(buildCorrelationHeaders('corr-1')).toEqual({ [CORRELATION_HEADER]: 'corr-1' });
    const minted = buildCorrelationHeaders();
    const mintedId: string = minted[CORRELATION_HEADER] as string;
    expect(typeof mintedId).toBe('string');
    expect(mintedId.length).toBeGreaterThan(0);
    expect(newCorrelationId()).not.toBe(newCorrelationId());
  });

  it('extracts ids from thrown values without throwing', () => {
    expect(extractCorrelationId({ correlationId: 'corr-9' })).toBe('corr-9');
    expect(extractCorrelationId({ correlationId: '' })).toBeUndefined();
    expect(extractCorrelationId(null)).toBeUndefined();
    expect(extractCorrelationId(42)).toBeUndefined();
    const evil = Object.defineProperty({}, 'correlationId', {
      get(): string {
        throw new Error('denied');
      },
    });
    expect(extractCorrelationId(evil)).toBeUndefined();
  });
});
