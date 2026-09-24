import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { keysForEvent, queryKeys, queryKeyRegistry } from '../queryKeys/index.js';
import type { QueryKey } from '../queryKeys/index.js';

afterEach(() => {
  // No global state in the key factory; placeholder keeps the suite explicit.
});

function prefixOf(key: QueryKey, length: number): QueryKey {
  return key.slice(0, length);
}

describe('queryKeys factory', () => {
  it('produces stable keys across calls', () => {
    expect(queryKeys.projects.list({ page: 1, pageSize: 20 })).toEqual(
      queryKeys.projects.list({ page: 1, pageSize: 20 }),
    );
    expect(queryKeys.segments.list('prj_1', { page: 2 })).toEqual(queryKeys.segments.list('prj_1', { page: 2 }));
    expect(queryKeys.segment.detail('prj_1', 'seg_9')).toEqual(queryKeys.segment.detail('prj_1', 'seg_9'));
  });

  it('nests children under their parent scope for prefix invalidation', () => {
    const project = queryKeys.project.detail('prj_1');
    expect(prefixOf(queryKeys.workspace.detail('prj_1'), project.length)).toEqual(project);
    expect(prefixOf(queryKeys.progress.detail('prj_1'), project.length)).toEqual(project);
    expect(prefixOf(queryKeys.segments.list('prj_1'), project.length)).toEqual(project);
    expect(prefixOf(queryKeys.segment.detail('prj_1', 'seg_1'), project.length)).toEqual(project);
    expect(prefixOf(queryKeys.speakers.detail('prj_1', 'spk_1'), project.length)).toEqual(project);
    expect(prefixOf(queryKeys.exports.detail('prj_1', 'exp_1'), project.length)).toEqual(project);
    expect(prefixOf(queryKeys.projects.detail('prj_1'), queryKeys.projects.details().length)).toEqual(
      queryKeys.projects.details(),
    );
  });

  it('keeps list and detail scopes distinct', () => {
    expect(queryKeys.projects.list()).not.toEqual(queryKeys.projects.details());
    expect(queryKeys.segments.list('prj_1')).not.toEqual(queryKeys.segment.detail('prj_1', 'seg_1'));
    expect(queryKeys.review.list('prj_1')).not.toEqual(queryKeys.review.detail('rev_1'));
    expect(queryKeys.review.detail('rev_1')).not.toEqual(queryKeys.reviewContext.detail('rev_1'));
    expect(queryKeys.notifications.list()).not.toEqual(queryKeys.notifications.unreadCount());
  });

  it('scopes list params per call without leaking between calls', () => {
    const first = queryKeys.projects.list({ page: 1 });
    const second = queryKeys.projects.list({ page: 2 });
    expect(first).not.toEqual(second);
    expect(queryKeys.projects.list({ page: 1 })).toEqual(first);
  });
});

describe('queryKeyRegistry', () => {
  const allEvents = [
    'project.status_changed',
    'run.status_changed',
    'stage.started',
    'stage.progress',
    'stage.completed',
    'stage.failed',
    'stage.review_required',
    'review.created',
    'review.resolved',
    'export.created',
    'export.completed',
    'export.failed',
    'notification.created',
    'output.ready',
  ] as const;

  it('covers every frozen SSE event type', () => {
    expect(Object.keys(queryKeyRegistry).sort()).toEqual([...allEvents].sort());
  });

  it('resolves non-empty keys for project-scoped events', () => {
    expect(keysForEvent('project.status_changed', { projectId: 'prj_1' }).length).toBeGreaterThan(0);
    expect(keysForEvent('run.status_changed', { projectId: 'prj_1' })).toContainEqual(
      queryKeys.progress.detail('prj_1'),
    );
    expect(keysForEvent('output.ready', { projectId: 'prj_1' })).toContainEqual(
      queryKeys.workspace.detail('prj_1'),
    );
    expect(keysForEvent('notification.created')).toContainEqual(queryKeys.notifications.unreadCount());
    expect(keysForEvent('review.resolved', { reviewId: 'rev_1' })).toContainEqual(
      queryKeys.reviewContext.detail('rev_1'),
    );
  });
});

describe('R1 no ad-hoc query keys', () => {
  it('finds no inline queryKey literals outside api/queryKeys', () => {
    // Scans for `queryKey:` followed by an inline literal value (array,
    // string, or template). Factory references such as
    // `queryKey: queryKeys.project(id)` are legal; only literals fail.
    // This file lives under `src/api/__tests__/`, which the walker skips,
    // so the pattern below cannot flag the suite itself.
    const literal = /queryKey\s*:\s*['"`[]/;
    const hits: string[] = [];
    const walk = (dir: string): void => {
      for (const entry of readdirSync(dir)) {
        const full = join(dir, entry);
        const stat = statSync(full);
        if (stat.isDirectory()) {
          if (entry === 'node_modules' || entry === 'dist' || entry === 'coverage' || entry === 'storybook-static') {
            continue;
          }
          walk(full);
          continue;
        }
        if (!/\.(ts|tsx)$/.test(entry)) {
          continue;
        }
        if (full.includes(join('api', 'queryKeys')) || full.includes(join('api', '__tests__'))) {
          continue;
        }
        const text = readFileSync(full, 'utf8');
        if (literal.test(text)) {
          hits.push(full);
        }
      }
    };
    walk(join(process.cwd(), 'src'));
    expect(hits).toEqual([]);
  });
});
