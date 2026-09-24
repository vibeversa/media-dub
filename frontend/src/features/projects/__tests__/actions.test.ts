import { describe, expect, it } from 'vitest';
import { getAllowedActions, getProjectDisplayName } from '../api.js';
import type { Project } from '../api.js';

function row(overrides: Partial<Pick<Project, 'status' | 'isArchived'>> = {}): Pick<Project, 'status' | 'isArchived'> {
  return { status: 'Completed', isArchived: false, ...overrides };
}

describe('valid-actions-only allowlist (R3)', () => {
  it('opens with the viewer hint, nothing without permissions', () => {
    expect(getAllowedActions(row(), ['project.view'])).toEqual(['open']);
    expect(getAllowedActions(row(), [])).toEqual([]);
  });

  it('shows cancel only for active runs with the cancel hint', () => {
    expect(getAllowedActions(row({ status: 'Processing' }), ['project.view', 'processing.cancel'])).toEqual([
      'open',
      'cancel',
    ]);
    expect(getAllowedActions(row({ status: 'Processing' }), ['project.view'])).toEqual(['open']);
    expect(getAllowedActions(row({ status: 'Cancelling' }), ['project.view', 'processing.cancel'])).toEqual([
      'open',
    ]);
    expect(getAllowedActions(row({ status: 'Completed' }), ['project.view', 'processing.cancel'])).toEqual([
      'open',
    ]);
  });

  it('shows retry only for terminal failures with the retry hint', () => {
    const perms = ['project.view', 'processing.retry'];
    expect(getAllowedActions(row({ status: 'Failed' }), perms)).toEqual(['open', 'retry']);
    expect(getAllowedActions(row({ status: 'MediaRejected' }), perms)).toEqual(['open', 'retry']);
    expect(getAllowedActions(row({ status: 'Failed' }), ['project.view'])).toEqual(['open']);
    expect(getAllowedActions(row({ status: 'Processing' }), perms)).toEqual(['open']);
  });

  it('shows export only for completed rows with the export hint', () => {
    expect(getAllowedActions(row(), ['project.view', 'export.create'])).toEqual(['open', 'export']);
    expect(getAllowedActions(row(), ['project.view'])).toEqual(['open']);
    expect(getAllowedActions(row({ status: 'Failed' }), ['project.view', 'export.create'])).toEqual(['open']);
  });

  it('hides delete while a run is active (the server would 409)', () => {
    const perms = ['project.view', 'project.delete'];
    expect(getAllowedActions(row({ status: 'Processing' }), perms)).toEqual(['open']);
    expect(getAllowedActions(row({ status: 'Uploading' }), perms)).toEqual(['open']);
    expect(getAllowedActions(row({ status: 'Failed' }), perms)).toEqual(['open', 'delete']);
  });

  it('switches archive/unarchive on the archived flag', () => {
    expect(getAllowedActions(row(), ['project.edit'])).toEqual(['archive']);
    expect(getAllowedActions(row({ isArchived: true }), ['project.edit'])).toEqual(['unarchive']);
    expect(getAllowedActions(row(), [])).toEqual([]);
  });

  it('hides run controls on archived rows (the server would reject)', () => {
    const perms = ['project.view', 'processing.cancel', 'processing.retry', 'project.edit'];
    expect(getAllowedActions(row({ status: 'Processing', isArchived: true }), perms)).toEqual([
      'open',
      'unarchive',
    ]);
    expect(getAllowedActions(row({ status: 'Failed', isArchived: true }), perms)).toEqual([
      'open',
      'unarchive',
    ]);
  });

  it('degrades to open-only for unknown statuses (never crashes)', () => {
    expect(getAllowedActions(row({ status: 'TimeTravelling' }), ['project.view', 'project.delete'])).toEqual([
      'open',
      'delete',
    ]);
  });

  it('returns the full lifecycle set for a privileged completed row', () => {
    expect(
      getAllowedActions(row(), [
        'project.view',
        'export.create',
        'project.delete',
        'project.edit',
      ]),
    ).toEqual(['open', 'export', 'delete', 'archive']);
  });
});

describe('getProjectDisplayName', () => {
  it('falls back to Untitled for blank names', () => {
    expect(getProjectDisplayName({ name: 'Pilot' })).toBe('Pilot');
    expect(getProjectDisplayName({ name: '' })).toBe('Untitled project');
    expect(getProjectDisplayName({ name: '   ' })).toBe('Untitled project');
  });
});
