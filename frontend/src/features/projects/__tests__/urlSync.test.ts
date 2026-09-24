import { describe, expect, it } from 'vitest';
import {
  DEFAULT_FILTERS,
  applyClientFilters,
  hasActiveFilters,
  isApproximateSort,
  parseFilters,
  serializeFilters,
  toServerQuery,
} from '../api.js';

function params(query: string): URLSearchParams {
  return new URLSearchParams(query);
}

describe('URL codec round-trip (R1)', () => {
  it('restores defaults from an empty query', () => {
    expect(parseFilters(params(''))).toEqual(DEFAULT_FILTERS);
  });

  it('round-trips a full filter set', () => {
    const full = {
      ...DEFAULT_FILTERS,
      status: 'Failed',
      targetLanguage: 'es',
      archived: 'all' as const,
      owner: 'usr_1',
      createdFrom: '2024-01-01',
      createdTo: '2024-02-01',
      sort: 'name' as const,
      sortDir: 'asc' as const,
      page: 3,
      pageSize: 50,
    };
    expect(parseFilters(serializeFilters(full))).toEqual(full);
  });

  it('omits defaults so links stay minimal and shareable', () => {
    expect(serializeFilters(DEFAULT_FILTERS).toString()).toBe('');
  });

  it('falls back to defaults for invalid values', () => {
    const parsed = parseFilters(params('status=Bogus&archived=sometimes&sort=popularity&sortDir=sideways&page=0&pageSize=huge'));
    expect(parsed).toEqual(DEFAULT_FILTERS);
  });

  it('clamps oversized pages to the server maximum', () => {
    expect(parseFilters(params('pageSize=500')).pageSize).toBe(100);
  });
});

describe('server query mapping (R2/R4)', () => {
  it('excludes archived by default (no archived param sent)', () => {
    const query = toServerQuery(DEFAULT_FILTERS);
    expect(query).toEqual({ page: 1, pageSize: 20, sort: 'createdAt', sortDir: 'desc' });
    expect('archived' in query).toBe(false);
  });

  it('sends archived=true for archived-only and archived=all for both', () => {
    expect(toServerQuery({ ...DEFAULT_FILTERS, archived: 'archived' }).archived).toBe('true');
    expect(toServerQuery({ ...DEFAULT_FILTERS, archived: 'all' }).archived).toBe('all');
  });

  it('maps UI sorts to server sorts', () => {
    expect(toServerQuery({ ...DEFAULT_FILTERS, sort: 'created' }).sort).toBe('createdAt');
    expect(toServerQuery({ ...DEFAULT_FILTERS, sort: 'activity' }).sort).toBe('updatedAt');
    expect(toServerQuery({ ...DEFAULT_FILTERS, sort: 'name' }).sort).toBe('name');
    expect(toServerQuery({ ...DEFAULT_FILTERS, sort: 'progress' }).sort).toBe('updatedAt');
  });

  it('flags progress as approximate ordering', () => {
    expect(isApproximateSort('progress')).toBe(true);
    expect(isApproximateSort('created')).toBe(false);
    expect(isApproximateSort('activity')).toBe(false);
    expect(isApproximateSort('name')).toBe(false);
  });

  it('forwards status and owner to the server', () => {
    const query = toServerQuery({ ...DEFAULT_FILTERS, status: 'Failed', owner: 'usr_9' });
    expect(query.status).toBe('Failed');
    expect(query.ownerId).toBe('usr_9');
  });
});

describe('client-side refinement', () => {
  const items = [
    {
      id: 'prj_1',
      name: 'A',
      status: 'Completed',
      targetLanguage: 'es',
      createdAt: '2024-01-15T12:00:00Z',
      settingsVersion: 1,
    },
    {
      id: 'prj_2',
      name: 'B',
      status: 'Failed',
      targetLanguage: 'fr',
      createdAt: '2024-03-10T12:00:00Z',
      settingsVersion: 1,
    },
  ];

  it('returns everything without client filters', () => {
    expect(applyClientFilters(items, DEFAULT_FILTERS).map((i) => i.id)).toEqual(['prj_1', 'prj_2']);
  });

  it('matches target language case-insensitively', () => {
    expect(
      applyClientFilters(items, { ...DEFAULT_FILTERS, targetLanguage: 'ES' }).map((i) => i.id),
    ).toEqual(['prj_1']);
  });

  it('applies the created-date range on the day granularity', () => {
    expect(
      applyClientFilters(items, { ...DEFAULT_FILTERS, createdFrom: '2024-02-01' }).map((i) => i.id),
    ).toEqual(['prj_2']);
    expect(
      applyClientFilters(items, { ...DEFAULT_FILTERS, createdTo: '2024-02-01' }).map((i) => i.id),
    ).toEqual(['prj_1']);
  });

  it('returns empty for undefined items (never crashes)', () => {
    expect(applyClientFilters(undefined, DEFAULT_FILTERS)).toEqual([]);
  });
});

describe('hasActiveFilters', () => {
  it('is false for defaults, true for any narrowing filter', () => {
    expect(hasActiveFilters(DEFAULT_FILTERS)).toBe(false);
    expect(hasActiveFilters({ ...DEFAULT_FILTERS, status: 'Failed' })).toBe(true);
    expect(hasActiveFilters({ ...DEFAULT_FILTERS, archived: 'all' })).toBe(true);
    expect(hasActiveFilters({ ...DEFAULT_FILTERS, targetLanguage: 'es' })).toBe(true);
  });
});
