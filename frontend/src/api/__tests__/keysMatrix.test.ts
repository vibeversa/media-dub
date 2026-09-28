// Task 039B: query-key factory matrix.
//
// Calls every `queryKeys` factory and every `queryKeyRegistry` entry so the
// per-file 80% function target holds as new scopes land. Assertions pin the
// scope invariants (project nesting for prefix invalidation, review/notification
// separation), not just call counts.
import { describe, expect, it } from 'vitest';
import { keysForEvent, queryKeys } from '../queryKeys/index.js';

const PROJECT = 'prj_1';

describe('queryKeys full factory matrix', () => {
  it('scopes dashboard and project roots', () => {
    expect(queryKeys.dashboard.summary()).toEqual(['dashboard', 'summary']);
    expect(queryKeys.projects.lists()).toEqual(['projects', 'list']);
    expect(queryKeys.projects.list()).toEqual(['projects', 'list', {}]);
    expect(queryKeys.projects.list({ page: 1, pageSize: 20 })).toEqual([
      'projects',
      'list',
      { page: 1, pageSize: 20 },
    ]);
    expect(queryKeys.projects.details()).toEqual(['projects', 'detail']);
    expect(queryKeys.projects.detail(PROJECT)).toEqual(['projects', 'detail', PROJECT]);
    expect(queryKeys.project.all(PROJECT)).toEqual(['projects', 'detail', PROJECT]);
    expect(queryKeys.project.detail(PROJECT)).toEqual(['projects', 'detail', PROJECT]);
  });

  it('nests workspace, progress, and segment scopes under the project', () => {
    expect(queryKeys.workspace.all(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'workspace']);
    expect(queryKeys.workspace.detail(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'workspace']);
    expect(queryKeys.progress.all(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'progress']);
    expect(queryKeys.progress.detail(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'progress']);
    expect(queryKeys.segments.all(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'segments']);
    expect(queryKeys.segments.lists(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'segments', 'list']);
    expect(queryKeys.segments.list(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'segments', 'list', {}]);
    expect(queryKeys.segments.list(PROJECT, { page: 2 })).toEqual([
      'projects',
      'detail',
      PROJECT,
      'segments',
      'list',
      { page: 2 },
    ]);
    expect(queryKeys.segments.details(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'segments', 'detail']);
    expect(queryKeys.segments.detail(PROJECT, 'seg_1')).toEqual([
      'projects',
      'detail',
      PROJECT,
      'segments',
      'detail',
      'seg_1',
    ]);
    expect(queryKeys.segment.all(PROJECT, 'seg_1')).toEqual(queryKeys.segments.detail(PROJECT, 'seg_1'));
    expect(queryKeys.segment.detail(PROJECT, 'seg_1')).toEqual(queryKeys.segments.detail(PROJECT, 'seg_1'));
  });

  it('scopes speakers, voices, runs, and exports under the project', () => {
    expect(queryKeys.speakers.all(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'speakers']);
    expect(queryKeys.speakers.lists(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'speakers', 'list']);
    expect(queryKeys.speakers.list(PROJECT, { pageSize: 50 })).toEqual([
      'projects',
      'detail',
      PROJECT,
      'speakers',
      'list',
      { pageSize: 50 },
    ]);
    expect(queryKeys.speakers.details(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'speakers', 'detail']);
    expect(queryKeys.speakers.detail(PROJECT, 'spk_1')).toEqual([
      'projects',
      'detail',
      PROJECT,
      'speakers',
      'detail',
      'spk_1',
    ]);
    expect(queryKeys.voices.all(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'voices']);
    expect(queryKeys.voices.available(PROJECT, 'spk_1')).toEqual([
      'projects',
      'detail',
      PROJECT,
      'voices',
      'available',
      'spk_1',
    ]);
    expect(queryKeys.voices.preview(PROJECT, 'prev_1')).toEqual([
      'projects',
      'detail',
      PROJECT,
      'voices',
      'preview',
      'prev_1',
    ]);
    expect(queryKeys.runs.all(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'runs']);
    expect(queryKeys.runs.lists(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'runs', 'list']);
    expect(queryKeys.runs.list(PROJECT, { page: 1 })).toEqual([
      'projects',
      'detail',
      PROJECT,
      'runs',
      'list',
      { page: 1 },
    ]);
    expect(queryKeys.exports.all(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'exports']);
    expect(queryKeys.exports.lists(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'exports', 'list']);
    expect(queryKeys.exports.list(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'exports', 'list', {}]);
    expect(queryKeys.exports.details(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'exports', 'detail']);
    expect(queryKeys.exports.detail(PROJECT, 'exp_1')).toEqual([
      'projects',
      'detail',
      PROJECT,
      'exports',
      'detail',
      'exp_1',
    ]);
  });

  it('branches the timeline peaks key on resolution', () => {
    expect(queryKeys.timeline.all(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'timeline']);
    expect(queryKeys.timeline.media(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'timeline', 'media']);
    expect(queryKeys.timeline.peaks(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'timeline', 'peaks']);
    expect(queryKeys.timeline.peaks(PROJECT, 128)).toEqual([
      'projects',
      'detail',
      PROJECT,
      'timeline',
      'peaks',
      128,
    ]);
  });

  it('scopes quality, outputs, cost, transcript, translations, and activity', () => {
    expect(queryKeys.quality.all(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'quality']);
    expect(queryKeys.quality.detail(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'quality']);
    expect(queryKeys.outputs.all(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'outputs']);
    expect(queryKeys.outputs.detail(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'outputs']);
    expect(queryKeys.cost.all(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'cost']);
    expect(queryKeys.cost.detail(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'cost']);
    expect(queryKeys.transcript.all(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'transcript']);
    expect(queryKeys.transcript.list(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'transcript', 'list']);
    expect(queryKeys.transcript.detail(PROJECT, 'seg_1')).toEqual([
      'projects',
      'detail',
      PROJECT,
      'transcript',
      'detail',
      'seg_1',
    ]);
    expect(queryKeys.translations.all(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'translations']);
    expect(queryKeys.translations.list(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'translations', 'list']);
    expect(queryKeys.translations.detail(PROJECT, 'seg_1')).toEqual([
      'projects',
      'detail',
      PROJECT,
      'translations',
      'detail',
      'seg_1',
    ]);
    expect(queryKeys.activity.all(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'activity']);
    expect(queryKeys.activity.lists(PROJECT)).toEqual(['projects', 'detail', PROJECT, 'activity', 'list']);
    expect(queryKeys.activity.list(PROJECT, { page: 3 })).toEqual([
      'projects',
      'detail',
      PROJECT,
      'activity',
      'list',
      { page: 3 },
    ]);
  });

  it('keeps review, notification, preference, and admin roots separate', () => {
    expect(queryKeys.review.all).toEqual(['reviews']);
    expect(queryKeys.review.lists()).toEqual(['reviews', 'list']);
    expect(queryKeys.review.list(PROJECT)).toEqual(['reviews', 'list', PROJECT, {}]);
    expect(queryKeys.review.list(PROJECT, { severity: 'blocking' })).toEqual([
      'reviews',
      'list',
      PROJECT,
      { severity: 'blocking' },
    ]);
    expect(queryKeys.review.details()).toEqual(['reviews', 'detail']);
    expect(queryKeys.review.detail('rev_1')).toEqual(['reviews', 'detail', 'rev_1']);
    expect(queryKeys.reviewContext.all('rev_1')).toEqual(['reviews', 'context', 'rev_1']);
    expect(queryKeys.reviewContext.detail('rev_1')).toEqual(['reviews', 'context', 'rev_1']);
    expect(queryKeys.notifications.all).toEqual(['notifications']);
    expect(queryKeys.notifications.lists()).toEqual(['notifications', 'list']);
    expect(queryKeys.notifications.list({ page: 1 })).toEqual(['notifications', 'list', { page: 1 }]);
    expect(queryKeys.notifications.unreadCount()).toEqual(['notifications', 'unread-count']);
    expect(queryKeys.preferences.all).toEqual(['me', 'preferences']);
    expect(queryKeys.preferences.lists()).toEqual(['me', 'preferences']);
    expect(queryKeys.preferences.list()).toEqual(['me', 'preferences']);
    expect(queryKeys.preferences.detail('locale')).toEqual(['me', 'preferences', 'locale']);
    expect(queryKeys.admin.all).toEqual(['admin']);
    expect(queryKeys.admin.section('flags')).toEqual(['admin', 'flags', {}]);
    expect(queryKeys.admin.section('flags', { page: 2 })).toEqual(['admin', 'flags', { page: 2 }]);
    expect(queryKeys.adminTenants.all).toEqual(['admin', 'tenants']);
    expect(queryKeys.adminTenants.lists()).toEqual(['admin', 'tenants', 'list']);
    expect(queryKeys.adminTenants.list({ pageSize: 10 })).toEqual(['admin', 'tenants', 'list', { pageSize: 10 }]);
    expect(queryKeys.adminTenants.details()).toEqual(['admin', 'tenants', 'detail']);
    expect(queryKeys.adminTenants.detail('ten_1')).toEqual(['admin', 'tenants', 'detail', 'ten_1']);
    expect(queryKeys.adminUsers.all).toEqual(['admin', 'users']);
    expect(queryKeys.adminUsers.lists()).toEqual(['admin', 'users', 'list']);
    expect(queryKeys.adminUsers.list()).toEqual(['admin', 'users', 'list', {}]);
    expect(queryKeys.adminUsers.details()).toEqual(['admin', 'users', 'detail']);
    expect(queryKeys.adminUsers.detail('usr_1')).toEqual(['admin', 'users', 'detail', 'usr_1']);
    expect(queryKeys.diagnostics.all).toEqual(['admin', 'diagnostics']);
    expect(queryKeys.diagnostics.queues()).toEqual(['admin', 'diagnostics', 'queues']);
    expect(queryKeys.diagnostics.dlq()).toEqual(['admin', 'diagnostics', 'dlq', {}]);
    expect(queryKeys.diagnostics.dlq({ page: 1 })).toEqual(['admin', 'diagnostics', 'dlq', { page: 1 }]);
    expect(queryKeys.diagnostics.leases({ pageSize: 5 })).toEqual(['admin', 'diagnostics', 'leases', { pageSize: 5 }]);
    expect(queryKeys.diagnostics.orphans()).toEqual(['admin', 'diagnostics', 'orphans', { pageSize: undefined, cursor: null }]);
    expect(queryKeys.diagnostics.orphans({ pageSize: 25, cursor: 'cur_1' })).toEqual([
      'admin',
      'diagnostics',
      'orphans',
      { pageSize: 25, cursor: 'cur_1' },
    ]);
    expect(queryKeys.diagnostics.backlog()).toEqual(['admin', 'diagnostics', 'review-backlog']);
    expect(queryKeys.diagnostics.usage()).toEqual(['admin', 'diagnostics', 'usage']);
    expect(queryKeys.diagnostics.quotas()).toEqual(['admin', 'diagnostics', 'quotas']);
    expect(queryKeys.diagnostics.health()).toEqual(['admin', 'diagnostics', 'provider-health']);
    expect(queryKeys.diagnostics.routes()).toEqual(['admin', 'diagnostics', 'provider-routes']);
    expect(queryKeys.diagnostics.status()).toEqual(['admin', 'diagnostics', 'status']);
  });
});

describe('queryKeyRegistry full event matrix', () => {
  it('resolves keys for every event with and without ids', () => {
    const events = [
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
    for (const event of events) {
      const scoped = keysForEvent(event, { projectId: PROJECT, reviewId: 'rev_1' });
      expect(Array.isArray(scoped), event).toBe(true);
      const unscoped = keysForEvent(event);
      expect(Array.isArray(unscoped), event).toBe(true);
    }
    expect(keysForEvent('project.status_changed')).toContainEqual(queryKeys.projects.all);
    expect(keysForEvent('project.status_changed', { projectId: PROJECT })).toContainEqual(
      queryKeys.project.detail(PROJECT),
    );
    expect(keysForEvent('run.status_changed')).toContainEqual(queryKeys.projects.all);
    expect(keysForEvent('stage.started')).toEqual([]);
    expect(keysForEvent('stage.review_required')).toContainEqual(queryKeys.review.lists());
    expect(keysForEvent('review.created')).toContainEqual(queryKeys.notifications.unreadCount());
    expect(keysForEvent('review.resolved')).toContainEqual(queryKeys.review.lists());
    expect(keysForEvent('export.created')).toEqual([]);
  });
});
