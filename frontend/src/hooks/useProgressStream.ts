import { useEffect, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import type { UseQueryResult } from '@tanstack/react-query';
import { ApiClient, apiClient, emitAuthExpired, getTokenOrUndefined, resolveBaseUrl } from '../api/client/index.js';
import type { ProgressResponse, SseEnvelope, SseEventType } from '../api/client/index.js';
import { normalizeError } from '../api/errors/index.js';
import type { AppError } from '../api/errors/index.js';
import { keysForEvent, queryKeys } from '../api/queryKeys/index.js';
import type { QueryKey, SseKeyIds } from '../api/queryKeys/index.js';
import { useIsAuthenticated } from '../features/auth/useSession.js';
import { tryGetEnv } from '../lib/env.js';
import { trackUnknownStatus } from '../telemetry/telemetry.js';

/**
 * Live progress via SSE with polling fallback (Task 026).
 *
 * Contract notes (Tasks 008/013/017):
 * - Stream is `GET /api/v1/projects/{id}/progress/stream` as
 *   `text/event-stream` of frozen `SseEnvelope` frames (14 event types,
 *   `schemaVersion: 1`, allowlisted payload). Frames are invalidation hints
 *   only; HTTP APIs remain the source of truth.
 * - Auth travels in the `Authorization` header only (never `?access_token=`).
 *   Resume uses the `Last-Event-ID` header; the server replays missed
 *   headers-only frames from its last-100 buffer and sets
 *   `replayTruncated: true` when the cursor fell out of window.
 * - Event IDs are opaque backend GUIDs (`Guid.NewGuid().ToString("N")`):
 *   cursor comparison is equality-based (dedupe) and never timestamp-based,
 *   so clock skew is irrelevant.
 *
 * Design:
 * - No local pipeline mirror: events only call
 *   `queryClient.invalidateQueries` via `queryKeyRegistry`. No stage state is
 *   cached outside the Query cache and no zustand store is touched here.
 * - Rapid bursts coalesce through a 500ms debounce.
 * - Reconnect uses exponential backoff + jitter capped at 30s and resumes
 *   from the last cursor. 404 stops without retry; 401 emits the Task 019
 *   expiry flow.
 * - `useProgressPoll` is the adaptive fallback: 3s visible/active, 20s
 *   hidden/background, stopped at terminal states. It takes over after
 *   `SSE_MAX_FAILURES_BEFORE_POLL` stream failures and yields back on
 *   recovery (caller wires `enabled` from `isFallbackActive`).
 * - Percents are display-only approximations: render via
 *   `formatApproximatePercent` plus `APPROXIMATE_PERCENT_NOTE`.
 */

export const SSE_BASE_DELAY_MS = 1000;

export const SSE_MAX_DELAY_MS = 30_000;

export const SSE_JITTER_MS = 1000;

export const SSE_MAX_FAILURES_BEFORE_POLL = 3;

export const INVALIDATION_DEBOUNCE_MS = 500;

export const POLL_ACTIVE_VISIBLE_MS = 3000;

export const POLL_HIDDEN_MS = 20_000;

export const APPROXIMATE_PERCENT_NOTE = 'Progress is approximate';

export const TERMINAL_PROGRESS_STATUSES: readonly string[] = ['Completed', 'Failed', 'Cancelled'];

/** SSE types whose hints also refresh the notification badge slot. */
const COMPLETION_SSE_TYPES: ReadonlySet<string> = new Set([
  'run.status_changed',
  'stage.completed',
  'stage.failed',
  'output.ready',
  'export.completed',
  'export.failed',
]);

/**
 * Payload keys that must never drive progress UI. Backend allowlist forbids
 * secrets/URLs/lease material; the client additionally treats
 * transcript/translation/media bodies as untrusted (ignored + warned).
 */
export const FORBIDDEN_SSE_PAYLOAD_KEYS: readonly string[] = [
  'signedUrl',
  'token',
  'secret',
  'apiKey',
  'connectionString',
  'internalPath',
  'rawPayload',
  'leaseToken',
  'transcript',
  'translation',
  'media',
  'audio',
  'video',
  'text',
  'body',
  'downloadUrl',
  'signedurl',
];

const FROZEN_SSE_TYPES: readonly string[] = [
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
];

/** True for the 14 frozen Task 013 event types. */
export function isKnownSseEventType(value: string): value is SseEventType {
  return FROZEN_SSE_TYPES.includes(value);
}

/** Case-insensitive substring match against the forbidden payload list. */
export function isForbiddenSsePayloadKey(key: string): boolean {
  const normalized = key.toLowerCase();
  for (const forbidden of FORBIDDEN_SSE_PAYLOAD_KEYS) {
    if (normalized.includes(forbidden.toLowerCase())) {
      return true;
    }
  }
  return false;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function toNonEmptyString(value: unknown): string | undefined {
  return typeof value === 'string' && value !== '' ? value : undefined;
}

/**
 * Validates an unknown SSE `data:` payload against the frozen envelope.
 * Returns the typed envelope or `null` when the frame must be ignored
 * (unknown type, wrong schema version, missing ids, or forbidden payload
 * keys including transcript/media bodies). Never throws and never leaks
 * payload content into logs/telemetry (only the event type travels).
 */
export function validateSseEnvelope(data: unknown): SseEnvelope | null {
  if (!isRecord(data)) {
    return null;
  }
  const eventType = toNonEmptyString(data['eventType']);
  if (eventType === undefined || !isKnownSseEventType(eventType)) {
    return null;
  }
  if (data['schemaVersion'] !== 1) {
    return null;
  }
  const eventId = toNonEmptyString(data['eventId']);
  const tenantId = toNonEmptyString(data['tenantId']);
  const correlationId = toNonEmptyString(data['correlationId']);
  const occurredAt = toNonEmptyString(data['occurredAt']);
  if (eventId === undefined || tenantId === undefined || correlationId === undefined || occurredAt === undefined) {
    return null;
  }
  const rawPayload = data['payload'];
  if (rawPayload !== undefined && !isRecord(rawPayload)) {
    return null;
  }
  const payload: Record<string, unknown> = isRecord(rawPayload) ? rawPayload : {};
  for (const key of Object.keys(payload)) {
    if (isForbiddenSsePayloadKey(key)) {
      return null;
    }
  }
  const projectId = data['projectId'];
  const processingRunId = data['processingRunId'];
  if (
    projectId !== undefined &&
    projectId !== null &&
    typeof projectId !== 'string'
  ) {
    return null;
  }
  if (
    processingRunId !== undefined &&
    processingRunId !== null &&
    typeof processingRunId !== 'string'
  ) {
    return null;
  }
  return {
    correlationId,
    eventId,
    eventType,
    occurredAt,
    payload,
    ...(typeof projectId === 'string' ? { projectId } : {}),
    ...(typeof processingRunId === 'string' ? { processingRunId } : {}),
    schemaVersion: 1,
    tenantId,
  } as SseEnvelope;
}

/**
 * Parses one raw SSE `id:/event:/data:` chunk into a validated envelope.
 * Returns `null` for heartbeats, unknown types, or invalid payloads (caller
 * warns + ignores). Never throws: `JSON.parse` failures yield `null`.
 */
export function parseProgressFrame(chunk: string): SseEnvelope | null {
  let frame: { id: string; event: string; data: unknown } | null = null;
  try {
    const parsed = ApiClient.parseFrame(chunk);
    if (parsed === null) {
      return null;
    }
    frame = { id: parsed.id, event: parsed.event, data: parsed.data as unknown };
  } catch {
    return null;
  }
  const data = frame.data;
  if (!isRecord(data)) {
    return null;
  }
  // Prefer the envelope's own eventType; fall back to the SSE event line.
  // A mismatch still resolves to the envelope type when known.
  const candidate = toNonEmptyString(data['eventType']) ?? (frame.event !== '' ? frame.event : undefined);
  if (candidate === undefined || !isKnownSseEventType(candidate)) {
    return null;
  }
  // Normalize the cursor: the `id:` line wins, then the envelope id.
  const cursor = frame.id !== '' ? frame.id : toNonEmptyString(data['eventId']);
  if (cursor === undefined) {
    return null;
  }
  const normalized: Record<string, unknown> = { ...data, eventType: candidate, eventId: cursor };
  return validateSseEnvelope(normalized);
}

/**
 * Exponential backoff with jitter for SSE reconnects.
 * `attempt` counts consecutive failures from 0. Base doubles per attempt
 * (`1000 * 2^attempt`), capped at 30s, plus `0..999ms` jitter derived from
 * `randomValue` (defaults to `Math.random()`; pass 0 in tests for determinism).
 */
export function computeSseBackoffMs(attempt: number, randomValue?: number): number {
  const safeAttempt = Number.isFinite(attempt) && attempt > 0 ? Math.floor(attempt) : 0;
  const capped = Math.min(SSE_MAX_DELAY_MS, SSE_BASE_DELAY_MS * 2 ** safeAttempt);
  const random = randomValue === undefined ? Math.random() : randomValue;
  const jitter = Number.isFinite(random) && random > 0 ? Math.floor(Math.min(1, Math.max(0, random)) * SSE_JITTER_MS) : 0;
  return Math.min(SSE_MAX_DELAY_MS, capped + jitter);
}

/** True for terminal run/progress statuses (case-insensitive). */
export function isTerminalProgressStatus(status: string | undefined | null): boolean {
  if (status === undefined || status === null || status === '') {
    return false;
  }
  return TERMINAL_PROGRESS_STATUSES.some((terminal) => terminal.toLowerCase() === status.toLowerCase());
}

export interface ProgressPollIntervalInput {
  readonly isVisible: boolean;
  readonly isTerminal: boolean;
}

/**
 * Adaptive poll cadence. Terminal stops (`false`); hidden/background uses the
 * slow cadence; visible + active uses the fast cadence. Pure for fake-timer tests.
 */
export function getProgressPollInterval(input: ProgressPollIntervalInput): number | false {
  if (input.isTerminal) {
    return false;
  }
  return input.isVisible ? POLL_ACTIVE_VISIBLE_MS : POLL_HIDDEN_MS;
}

/** True while the document tab is visible (defaults visible outside DOM/tests). */
export function isDocumentVisible(): boolean {
  if (typeof document === 'undefined') {
    return true;
  }
  try {
    if (typeof document.hidden === 'boolean') {
      return !document.hidden;
    }
    const state = (document as Document & { visibilityState?: string }).visibilityState;
    return state !== 'hidden';
  } catch {
    return true;
  }
}

/** True when the event should also refresh the notification badge slot. */
export function shouldInvalidateNotifications(eventType: SseEventType): boolean {
  return COMPLETION_SSE_TYPES.has(eventType);
}

/**
 * Extracts `SseKeyIds` from a validated envelope. `projectId` comes from the
 * envelope; `reviewId` is read from the allowlisted payload (`reviewId` only)
 * when present as a non-empty string. Never reads transcript/media bodies.
 */
export function extractSseIds(envelope: SseEnvelope): SseKeyIds {
  const projectId = typeof envelope.projectId === 'string' && envelope.projectId !== '' ? envelope.projectId : undefined;
  const payload = envelope.payload as Record<string, unknown>;
  const reviewRaw = payload['reviewId'];
  const reviewId = typeof reviewRaw === 'string' && reviewRaw !== '' ? reviewRaw : undefined;
  if (projectId !== undefined && reviewId !== undefined) {
    return { projectId, reviewId };
  }
  if (projectId !== undefined) {
    return { projectId };
  }
  if (reviewId !== undefined) {
    return { reviewId };
  }
  return {};
}

/**
 * Resolves the invalidation keys for one envelope: the registry keys plus the
 * notification badge keys for completion/failure types. Pure; performs no
 * invalidation itself.
 */
export function resolveInvalidations(eventType: SseEventType, ids?: SseKeyIds): readonly QueryKey[] {
  const base = keysForEvent(eventType, ids ?? {});
  if (!shouldInvalidateNotifications(eventType)) {
    return base;
  }
  const extra: QueryKey[] = [queryKeys.notifications.list(), queryKeys.notifications.unreadCount()];
  const seen = new Set(base.map((key) => JSON.stringify(key)));
  const merged: QueryKey[] = [...base];
  for (const key of extra) {
    if (!seen.has(JSON.stringify(key))) {
      merged.push(key);
    }
  }
  return merged;
}

/**
 * Formats a backend percent as an approximate display string (`~42%`).
 * Clamps to 0..100 and rounds; always includes the `~` prefix so raw
 * percents never render as exact.
 */
export function formatApproximatePercent(percent: number): string {
  if (!Number.isFinite(percent)) {
    return '~0%';
  }
  const clamped = Math.min(100, Math.max(0, Math.round(percent)));
  return `~${String(clamped)}%`;
}

/** Builds the stream URL for a project (header auth only, never query token). */
export function buildProgressStreamUrl(projectId: string): string {
  return `${resolveBaseUrl()}/api/v1/projects/${encodeURIComponent(projectId)}/progress/stream`;
}

/** Reads the `VITE_SSE_ENABLED` kill-switch (defaults true outside app guard/tests). */
export function readSseEnabled(): boolean {
  try {
    const result = tryGetEnv();
    if (result.ok) {
      return result.env.sseEnabled;
    }
    return true;
  } catch {
    return true;
  }
}

function warnUnknownEvent(eventType: string): void {
  try {
    trackUnknownStatus(eventType);
  } catch {
    // Telemetry must never break streaming.
  }
  try {
    console.warn(`[progress-stream] ignoring unknown event type: ${eventType}`);
  } catch {
    // Console unavailable: ignore.
  }
}

function warnInvalidPayload(eventType: string): void {
  try {
    trackUnknownStatus(`${eventType}:payload`);
  } catch {
    // Telemetry must never break streaming.
  }
  try {
    console.warn(`[progress-stream] ignoring event with invalid payload: ${eventType}`);
  } catch {
    // Console unavailable: ignore.
  }
}

/**
 * Opaque cursor tracker for SSE dedupe. Backend IDs are random GUIDs with no
 * lexical order, so staleness is equality-based (already-seen) and never
 * timestamp-based. `shouldProcess` returns false for duplicates/empty ids.
 */
export class SseCursorTracker {
  private readonly seen = new Set<string>();
  private cursor: string | undefined = undefined;

  public shouldProcess(eventId: string | undefined): boolean {
    if (eventId === undefined || eventId === '') {
      return false;
    }
    return !this.seen.has(eventId);
  }

  public markSeen(eventId: string): void {
    this.seen.add(eventId);
    this.cursor = eventId;
  }

  public hasSeen(eventId: string): boolean {
    return this.seen.has(eventId);
  }

  public get lastCursor(): string | undefined {
    return this.cursor;
  }

  public get seenCount(): number {
    return this.seen.size;
  }

  public reset(): void {
    this.seen.clear();
    this.cursor = undefined;
  }
}

/** Creates an isolated cursor tracker (one per stream). */
export function createCursorTracker(): SseCursorTracker {
  return new SseCursorTracker();
}

export type ProgressStreamState = 'idle' | 'connecting' | 'open' | 'backoff' | 'polling-fallback' | 'stopped';

export interface UseProgressStreamOptions {
  readonly enabled?: boolean;
  readonly maxFailuresBeforePoll?: number;
}

export interface UseProgressStreamResult {
  readonly streamState: ProgressStreamState;
  readonly lastCursor: string | undefined;
  readonly failureCount: number;
  readonly isFallbackActive: boolean;
}

/**
 * Authenticated SSE progress stream as invalidation hints.
 *
 * - Opens `GET .../progress/stream` with `Authorization: Bearer` (header
 *   only) + `Accept: text/event-stream` + `Last-Event-ID` resume.
 * - Parses each `id/event/data` chunk via the frozen envelope; unknown types
 *   and invalid payloads are warned + ignored, never crash.
 * - Dedupes by event id, updates the cursor, and debounces invalidations
 *   (500ms) through `queryKeyRegistry` (+ notifications on completion).
 * - Reconnects with exponential backoff + jitter (max 30s). 404 stops and
 *   invalidates the workspace; 401 emits `auth:expired` for Task 019.
 * - Hidden tabs close the stream (slow poll covers); visible tabs reconnect
 *   with the cursor. `replayTruncated: true` falls back to polling.
 */
export function useProgressStream(projectId: string, options?: UseProgressStreamOptions): UseProgressStreamResult {
  const queryClient = useQueryClient();
  const isAuthenticated = useIsAuthenticated();
  const maxFailures = options?.maxFailuresBeforePoll ?? SSE_MAX_FAILURES_BEFORE_POLL;
  const callerEnabled = options?.enabled ?? true;
  const enabled = callerEnabled && isAuthenticated && projectId !== '' && readSseEnabled();

  const [streamState, setStreamState] = useState<ProgressStreamState>('idle');
  const [lastCursor, setLastCursor] = useState<string | undefined>(undefined);
  const [failureCount, setFailureCount] = useState<number>(0);

  const trackerRef = useRef<SseCursorTracker | null>(null);
  if (trackerRef.current === null) {
    trackerRef.current = createCursorTracker();
  }
  const abortRef = useRef<AbortController | null>(null);
  const backoffTimerRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const debounceTimerRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const pendingKeysRef = useRef<Map<string, QueryKey>>(new Map());
  const connectAttemptRef = useRef<number>(0);
  const stoppedRef = useRef<boolean>(false);
  const stateRef = useRef<ProgressStreamState>('idle');
  stateRef.current = streamState;

  const isFallbackActive = failureCount >= maxFailures;

  useEffect(() => {
    if (isFallbackActive && stateRef.current !== 'stopped') {
      setStreamState((previous) => (previous === 'stopped' ? previous : 'polling-fallback'));
    }
  }, [isFallbackActive]);

  useEffect(() => {
    if (!enabled) {
      return;
    }
    stoppedRef.current = false;
    connectAttemptRef.current = 0;
    const pending = pendingKeysRef.current;

    const flushInvalidations = (): void => {
      if (pending.size === 0) {
        return;
      }
      const keys = [...pending.values()];
      pending.clear();
      for (const key of keys) {
        void queryClient.invalidateQueries({ queryKey: key });
      }
    };

    const scheduleInvalidations = (keys: readonly QueryKey[]): void => {
      for (const key of keys) {
        pending.set(JSON.stringify(key), key);
      }
      if (debounceTimerRef.current !== undefined) {
        return;
      }
      debounceTimerRef.current = setTimeout(() => {
        debounceTimerRef.current = undefined;
        flushInvalidations();
      }, INVALIDATION_DEBOUNCE_MS);
    };

    const handleEnvelope = (envelope: SseEnvelope): void => {
      const tracker = trackerRef.current;
      if (tracker === null) {
        return;
      }
      if (!tracker.shouldProcess(envelope.eventId)) {
        return;
      }
      tracker.markSeen(envelope.eventId);
      setLastCursor(envelope.eventId);
      const ids = extractSseIds(envelope);
      // Scope project-scoped events to this stream's project when the
      // envelope omits ids (header-only replay pointers carry them, but
      // be defensive: never invalidate the whole cache from one stream).
      const scoped: SseKeyIds =
        ids.projectId === undefined && envelope.projectId === undefined
          ? { projectId }
          : ids.projectId === undefined
            ? { projectId }
            : ids;
      scheduleInvalidations(resolveInvalidations(envelope.eventType, scoped));
    };

    const readStream = async (response: Response, signal: AbortSignal): Promise<void> => {
      const body = response.body;
      if (body === null) {
        return;
      }
      const reader = body.getReader();
      const decoder = new TextDecoder();
      let buffer = '';
      for (;;) {
        if (signal.aborted) {
          try {
            await reader.cancel();
          } catch {
            // Abort races: ignore.
          }
          return;
        }
        let chunk: ReadableStreamReadResult<Uint8Array>;
        try {
          chunk = await reader.read();
        } catch (error) {
          if (signal.aborted) {
            return;
          }
          throw error;
        }
        if (chunk.done) {
          if (buffer.trim() !== '') {
            const envelope = parseProgressFrame(buffer);
            if (envelope !== null) {
              handleEnvelope(envelope);
            }
          }
          return;
        }
        buffer += decoder.decode(chunk.value, { stream: true });
        let boundary = buffer.indexOf('\n\n');
        while (boundary >= 0) {
          const raw = buffer.slice(0, boundary);
          buffer = buffer.slice(boundary + 2);
          if (raw.trim() === '') {
            boundary = buffer.indexOf('\n\n');
            continue;
          }
          // Distinguish unknown-type frames (warn + ignore) from invalid
          // payload frames (warn + ignore) without leaking payload bodies.
          let eventHint = '';
          for (const line of raw.split('\n')) {
            if (line.startsWith('event:')) {
              eventHint = line.slice(6).trim();
              break;
            }
          }
          const envelope = parseProgressFrame(raw);
          if (envelope === null) {
            if (eventHint !== '' && !isKnownSseEventType(eventHint)) {
              warnUnknownEvent(eventHint);
            } else if (eventHint !== '') {
              warnInvalidPayload(eventHint);
            }
          } else {
            handleEnvelope(envelope);
          }
          boundary = buffer.indexOf('\n\n');
        }
      }
    };

    const connect = async (signal: AbortSignal): Promise<void> => {
      const tracker = trackerRef.current;
      const cursor = tracker?.lastCursor;
      const token = getTokenOrUndefined();
      if (token === undefined || token === '') {
        return;
      }
      const headers: Record<string, string> = {
        Accept: 'text/event-stream',
        Authorization: `Bearer ${token}`,
      };
      if (cursor !== undefined && cursor !== '') {
        headers['Last-Event-ID'] = cursor;
      }
      let response: Response;
      try {
        response = await fetch(buildProgressStreamUrl(projectId), { headers, signal });
      } catch (error) {
        if (signal.aborted) {
          return;
        }
        throw error;
      }
      if (signal.aborted) {
        return;
      }
      if (response.status === 401) {
        emitAuthExpired({ correlationId: response.headers.get('X-Correlation-ID') ?? '', status: 401 });
        const error = new Error('Progress stream unauthorized.');
        (error as { status?: number }).status = 401;
        throw error;
      }
      if (response.status === 404) {
        stoppedRef.current = true;
        setStreamState('stopped');
        void queryClient.invalidateQueries({ queryKey: queryKeys.workspace.detail(projectId) });
        return;
      }
      if (!response.ok) {
        const error = new Error(`Progress stream failed with status ${String(response.status)}.`);
        (error as { status?: number }).status = response.status;
        throw error;
      }
      if (response.headers.get('replayTruncated') === 'true') {
        void queryClient.invalidateQueries({ queryKey: queryKeys.workspace.detail(projectId) });
        void queryClient.invalidateQueries({ queryKey: queryKeys.progress.detail(projectId) });
      }
      setFailureCount(0);
      connectAttemptRef.current = 0;
      setStreamState('open');
      await readStream(response, signal);
      if (signal.aborted || stoppedRef.current) {
        return;
      }
      // Clean close (server ended stream): treat as a failure for backoff
      // accounting so a flapping stream eventually yields to polling.
      throw new Error('Progress stream closed.');
    };

    const scheduleReconnect = (): void => {
      if (stoppedRef.current) {
        return;
      }
      if (typeof document !== 'undefined' && document.hidden === true) {
        setStreamState((previous) => (previous === 'stopped' ? previous : 'polling-fallback'));
        return;
      }
      const attempt = connectAttemptRef.current;
      const delay = computeSseBackoffMs(attempt);
      connectAttemptRef.current = attempt + 1;
      setStreamState('backoff');
      if (backoffTimerRef.current !== undefined) {
        clearTimeout(backoffTimerRef.current);
      }
      backoffTimerRef.current = setTimeout(() => {
        backoffTimerRef.current = undefined;
        if (stoppedRef.current) {
          return;
        }
        void runOnce();
      }, delay);
    };

    const runOnce = async (): Promise<void> => {
      if (stoppedRef.current) {
        return;
      }
      const controller = new AbortController();
      abortRef.current = controller;
      setStreamState((previous) => (previous === 'open' ? previous : 'connecting'));
      try {
        await connect(controller.signal);
      } catch {
        if (controller.signal.aborted || stoppedRef.current) {
          return;
        }
        setFailureCount((previous) => previous + 1);
        scheduleReconnect();
      }
    };

    const onVisibility = (): void => {
      if (typeof document === 'undefined') {
        return;
      }
      if (document.hidden === true) {
        abortRef.current?.abort();
        abortRef.current = null;
        if (backoffTimerRef.current !== undefined) {
          clearTimeout(backoffTimerRef.current);
          backoffTimerRef.current = undefined;
        }
        if (stateRef.current !== 'stopped') {
          setStreamState('polling-fallback');
        }
        return;
      }
      if (stoppedRef.current) {
        return;
      }
      // Visible again: reconnect with the cursor.
      if (stateRef.current === 'open') {
        return;
      }
      void runOnce();
    };

    if (typeof document !== 'undefined') {
      document.addEventListener('visibilitychange', onVisibility);
    }
    void runOnce();

    return () => {
      stoppedRef.current = true;
      const abort = abortRef.current;
      abort?.abort();
      abortRef.current = null;
      const backoffTimer = backoffTimerRef.current;
      if (backoffTimer !== undefined) {
        clearTimeout(backoffTimer);
        backoffTimerRef.current = undefined;
      }
      const debounceTimer = debounceTimerRef.current;
      if (debounceTimer !== undefined) {
        clearTimeout(debounceTimer);
        debounceTimerRef.current = undefined;
      }
      pending.clear();
      if (typeof document !== 'undefined') {
        document.removeEventListener('visibilitychange', onVisibility);
      }
    };
  }, [enabled, projectId, queryClient]);

  return { streamState, lastCursor, failureCount, isFallbackActive };
}

export interface UseProgressPollOptions {
  readonly enabled?: boolean;
}

async function fetchProgressSnapshot(projectId: string): Promise<ProgressResponse> {
  return apiClient.getProgress({ path: { projectId } });
}

/**
 * Adaptive polling fallback for progress.
 *
 * - Queries `queryKeys.progress.detail(projectId)` (`GET .../progress`).
 * - Visible + active polls every 3s; hidden/background every 20s; terminal
 *   statuses stop (`false`). Gated on Task 019 session + non-empty id.
 * - Enable it from the stream's `isFallbackActive` so it takes over after N
 *   stream failures and yields back on recovery:
 *   `useProgressPoll(id, { enabled: stream.isFallbackActive })`.
 */
export function useProgressPoll(projectId: string, options?: UseProgressPollOptions): UseQueryResult<ProgressResponse, AppError> {
  const isAuthenticated = useIsAuthenticated();
  const callerEnabled = options?.enabled ?? true;
  const enabled = callerEnabled && isAuthenticated && projectId !== '';
  const [visible, setVisible] = useState<boolean>(() => isDocumentVisible());

  useEffect(() => {
    if (typeof document === 'undefined') {
      return;
    }
    const onVisibility = (): void => {
      setVisible(isDocumentVisible());
    };
    document.addEventListener('visibilitychange', onVisibility);
    return () => {
      document.removeEventListener('visibilitychange', onVisibility);
    };
  }, []);

  return useQuery<ProgressResponse, AppError>({
    queryKey: queryKeys.progress.detail(projectId),
    queryFn: async (): Promise<ProgressResponse> => {
      try {
        return await fetchProgressSnapshot(projectId);
      } catch (error) {
        throw normalizeError(error, { method: 'GET' });
      }
    },
    enabled,
    refetchInterval: (query) => {
      const terminal = isTerminalProgressStatus(query.state.data?.status);
      return getProgressPollInterval({ isVisible: visible, isTerminal: terminal });
    },
  });
}
