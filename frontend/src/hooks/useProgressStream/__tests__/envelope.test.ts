import { describe, expect, it, vi } from 'vitest';
import {
  FORBIDDEN_SSE_PAYLOAD_KEYS,
  buildProgressStreamUrl,
  extractSseIds,
  isForbiddenSsePayloadKey,
  isKnownSseEventType,
  parseProgressFrame,
  resolveInvalidations,
  shouldInvalidateNotifications,
  validateSseEnvelope,
} from '../../useProgressStream.js';
import { queryKeys } from '../../../api/queryKeys/index.js';

function envelope(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    correlationId: 'corr_1',
    eventId: 'evt_1',
    eventType: 'stage.progress',
    occurredAt: '2024-01-16T12:00:00Z',
    payload: { projectId: 'prj_1', status: 'Running', percentApproximate: 42 },
    projectId: 'prj_1',
    processingRunId: 'run_1',
    schemaVersion: 1,
    tenantId: 'tenant_1',
    ...overrides,
  };
}

function sseChunk(eventType: string, data: Record<string, unknown>, id = 'evt_1'): string {
  return `id: ${id}\nevent: ${eventType}\ndata: ${JSON.stringify(data)}\n\n`;
}

describe('isKnownSseEventType', () => {
  it('accepts the frozen 14', () => {
    for (const known of [
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
    ]) {
      expect(isKnownSseEventType(known)).toBe(true);
    }
  });

  it('rejects unknown types', () => {
    expect(isKnownSseEventType('stage.unknown')).toBe(false);
    expect(isKnownSseEventType('')).toBe(false);
    expect(isKnownSseEventType('STAGE.PROGRESS')).toBe(false);
  });
});

describe('isForbiddenSsePayloadKey', () => {
  it('flags secrets and media bodies', () => {
    expect(isForbiddenSsePayloadKey('token')).toBe(true);
    expect(isForbiddenSsePayloadKey('signedUrl')).toBe(true);
    expect(isForbiddenSsePayloadKey('transcript')).toBe(true);
    expect(isForbiddenSsePayloadKey('translation')).toBe(true);
    expect(isForbiddenSsePayloadKey('media')).toBe(true);
    expect(isForbiddenSsePayloadKey('downloadUrl')).toBe(true);
    expect(isForbiddenSsePayloadKey('leaseToken')).toBe(true);
  });

  it('allows the progress allowlist', () => {
    expect(isForbiddenSsePayloadKey('projectId')).toBe(false);
    expect(isForbiddenSsePayloadKey('status')).toBe(false);
    expect(isForbiddenSsePayloadKey('percentApproximate')).toBe(false);
    expect(isForbiddenSsePayloadKey('completedUnits')).toBe(false);
    expect(isForbiddenSsePayloadKey('correlationId')).toBe(false);
  });

  it('exposes a non-empty forbidden list', () => {
    expect(FORBIDDEN_SSE_PAYLOAD_KEYS.length).toBeGreaterThan(0);
  });
});

describe('validateSseEnvelope', () => {
  it('accepts a valid envelope', () => {
    expect(validateSseEnvelope(envelope()))?.toMatchObject({ eventId: 'evt_1', eventType: 'stage.progress' });
  });

  it('rejects unknown event types', () => {
    expect(validateSseEnvelope(envelope({ eventType: 'stage.unknown' }))).toBeNull();
  });

  it('rejects wrong schema versions', () => {
    expect(validateSseEnvelope(envelope({ schemaVersion: 2 }))).toBeNull();
  });

  it('rejects missing ids', () => {
    expect(validateSseEnvelope(envelope({ eventId: '' }))).toBeNull();
    const missing = envelope();
    delete missing['tenantId'];
    expect(validateSseEnvelope(missing)).toBeNull();
  });

  it('rejects forbidden payload keys without leaking bodies', () => {
    expect(validateSseEnvelope(envelope({ payload: { token: 'abc' } }))).toBeNull();
    expect(validateSseEnvelope(envelope({ payload: { transcript: 'hello' } }))).toBeNull();
    expect(validateSseEnvelope(envelope({ payload: { media: 'bytes' } }))).toBeNull();
    expect(validateSseEnvelope(envelope({ payload: { signedUrl: 'https://x' } }))).toBeNull();
  });

  it('accepts header-only replay pointers (no payload)', () => {
    const pointer = envelope({ payload: undefined });
    delete pointer['payload'];
    const validated = validateSseEnvelope(pointer);
    expect(validated)?.toMatchObject({ eventId: 'evt_1' });
  });
});

describe('parseProgressFrame', () => {
  it('parses a valid chunk', () => {
    const data = envelope();
    const parsed = parseProgressFrame(sseChunk('stage.progress', data, 'evt_1'));
    expect(parsed)?.toMatchObject({ eventId: 'evt_1', eventType: 'stage.progress' });
  });

  it('returns null for heartbeats and bad JSON', () => {
    expect(parseProgressFrame(': heartbeat\n\n')).toBeNull();
    expect(parseProgressFrame('id: a\nevent: stage.progress\ndata: not-json\n\n')).toBeNull();
    expect(parseProgressFrame('')).toBeNull();
  });

  it('returns null for unknown types without throwing', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    try {
      const data = envelope({ eventType: 'future.type' });
      expect(parseProgressFrame(sseChunk('future.type', data, 'evt_9'))).toBeNull();
    } finally {
      warn.mockRestore();
    }
  });
});

describe('extractSseIds and resolveInvalidations', () => {
  it('extracts project and review ids without bodies', () => {
    const validated = validateSseEnvelope(envelope({ payload: { reviewId: 'rev_1' } }));
    expect(validated).not.toBeNull();
    if (validated !== null) {
      expect(extractSseIds(validated)).toEqual({ projectId: 'prj_1', reviewId: 'rev_1' });
    }
  });

  it('maps run status to progress and workspace', () => {
    const keys = resolveInvalidations('run.status_changed', { projectId: 'prj_1' });
    expect(keys).toContainEqual(queryKeys.progress.detail('prj_1'));
    expect(keys).toContainEqual(queryKeys.workspace.detail('prj_1'));
  });

  it('adds notifications for completion and failure types', () => {
    for (const terminal of ['run.status_changed', 'stage.completed', 'stage.failed', 'output.ready'] as const) {
      expect(shouldInvalidateNotifications(terminal)).toBe(true);
      const keys = resolveInvalidations(terminal, { projectId: 'prj_1' });
      expect(keys).toContainEqual(queryKeys.notifications.unreadCount());
      expect(keys).toContainEqual(queryKeys.notifications.list());
    }
  });

  it('leaves hot-path progress without badge churn', () => {
    expect(shouldInvalidateNotifications('stage.progress')).toBe(false);
    const keys = resolveInvalidations('stage.progress', { projectId: 'prj_1' });
    expect(keys).toContainEqual(queryKeys.progress.detail('prj_1'));
    expect(keys).not.toContainEqual(queryKeys.notifications.unreadCount());
  });

  it('builds stream URLs without tokens', () => {
    const url = buildProgressStreamUrl('prj_1');
    expect(url).toContain('/api/v1/projects/prj_1/progress/stream');
    expect(url).not.toContain('token');
    expect(url).not.toContain('access_token');
  });
});
