import { useMutation, useQueryClient } from '@tanstack/react-query';
import type { QueryClient, UseMutationResult } from '@tanstack/react-query';
import { apiClient, newIdempotencyKey } from '../../api/client/index.js';
import { normalizeError } from '../../api/errors/index.js';
import type { AppError } from '../../api/errors/index.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { buildDispositionPayload, dispositionEndpointFor, isReviewConflict } from './types.js';
import type { DispositionAction } from './types.js';

/**
 * Idempotent, versioned review dispositions (Task 031).
 *
 * Every call sends `Idempotency-Key` (client-generated per intent, reused on
 * retry) + `expectedVersion` + reason. Resolve-with-edit embeds the manual
 * edit text inline (`editText`). Mapping follows Task 011 hardened paths:
 * approve→resolve, reject→dismiss, requeue→reopen, resolve-with-edit→
 * resolve-with-edit. Legacy approve/reject/requeue stay untouched.
 *
 * Key lifecycle: `dispositionKeyFor(reviewId, action)` is stable until the
 * intent succeeds; failures (including 409/412) keep the key so the explicit
 * retry reuses it (single audit row via backend replay). Success rotates the
 * key so the next distinct intent gets a fresh one.
 */

const keyStore = new Map<string, string>();

function storeKey(reviewId: string, action: DispositionAction): string {
  return `${reviewId}:${action}`;
}

/** Stable idempotency key per intent; creates on first use. */
export function dispositionKeyFor(reviewId: string, action: DispositionAction): string {
  const key = storeKey(reviewId, action);
  const existing = keyStore.get(key);
  if (existing !== undefined) {
    return existing;
  }
  const created = newIdempotencyKey();
  keyStore.set(key, created);
  return created;
}

/** Rotates the key after a successful intent (next intent gets a fresh key). */
export function rotateDispositionKey(reviewId: string, action: DispositionAction): void {
  keyStore.delete(storeKey(reviewId, action));
}

/** Test-only: clears all stable keys. */
export function resetDispositionKeysForTests(): void {
  keyStore.clear();
}

export interface DispositionVariables {
  readonly action: DispositionAction;
  readonly expectedVersion: number;
  readonly reason: string;
  readonly editText?: string;
}

export interface DispositionResult {
  readonly reviewId: string;
  readonly status: string;
  readonly version: number;
  readonly manualVersionId: string | undefined;
  readonly versionKind: string | undefined;
}

function optimisticStatusFor(action: DispositionAction): string {
  switch (action) {
    case 'approve':
      return 'Approved';
    case 'reject':
      return 'dismiss';
    case 'requeue':
      return 'Open';
    case 'resolve-with-edit':
      return 'Approved';
  }
}

function optimisticStatusLabel(action: DispositionAction): string {
  switch (action) {
    case 'approve':
      return 'Approved';
    case 'reject':
      return 'Rejected';
    case 'requeue':
      return 'Open';
    case 'resolve-with-edit':
      return 'Approved';
  }
}

void optimisticStatusFor;

/** Low-level hardened call (owns the idempotency header, normalizes errors). */
export async function executeDisposition(
  reviewId: string,
  variables: DispositionVariables,
  options?: { readonly idempotencyKey?: string },
): Promise<DispositionResult> {
  const key = options?.idempotencyKey ?? dispositionKeyFor(reviewId, variables.action);
  const payload = buildDispositionPayload(variables.action, variables.expectedVersion, variables.reason, variables.editText);
  const endpoint = dispositionEndpointFor(variables.action);
  void endpoint;
  try {
    let response: unknown;
    const params = { path: { reviewId } };
    const requestOptions = { idempotencyKey: key };
    switch (variables.action) {
      case 'approve':
        response = await apiClient.resolveReview(params, payload, requestOptions);
        break;
      case 'reject':
        response = await apiClient.dismissReview(params, payload, requestOptions);
        break;
      case 'requeue':
        response = await apiClient.reopenReview(params, payload, requestOptions);
        break;
      case 'resolve-with-edit':
        response = await apiClient.resolveReviewWithEdit(params, payload, requestOptions);
        break;
    }
    const record = response as unknown as Record<string, unknown>;
    const status = typeof record['status'] === 'string' ? (record['status'] as string) : optimisticStatusLabel(variables.action);
    const version =
      typeof record['version'] === 'number' && Number.isFinite(record['version'])
        ? Math.floor(record['version'] as number)
        : variables.expectedVersion + 1;
    const resolvedId = typeof record['reviewId'] === 'string' ? (record['reviewId'] as string) : reviewId;
    const manualVersionId =
      typeof record['manualVersionId'] === 'string' && record['manualVersionId'] !== ''
        ? (record['manualVersionId'] as string)
        : undefined;
    const versionKind =
      typeof record['versionKind'] === 'string' && record['versionKind'] !== '' ? (record['versionKind'] as string) : undefined;
    rotateDispositionKey(reviewId, variables.action);
    return { reviewId: resolvedId, status, version, manualVersionId, versionKind };
  } catch (error) {
    throw normalizeError(error, { method: 'POST' });
  }
}

export interface UseDispositionOptions {
  readonly projectId: string;
  readonly reviewId: string;
}

/**
 * Single disposition mutation with optimistic status + rollback.
 * 409/412 rolls back, invalidates (refetch), and keeps the idempotency key
 * so the caller retries with the same key and preserved reason text.
 */
export function useDisposition({
  projectId,
  reviewId,
}: UseDispositionOptions): UseMutationResult<DispositionResult, AppError, DispositionVariables> {
  const queryClient = useQueryClient();
  return useMutation<DispositionResult, AppError, DispositionVariables>({
    mutationFn: async (variables): Promise<DispositionResult> => executeDisposition(reviewId, variables),
    onMutate: async (variables) => {
      await queryClient.cancelQueries({ queryKey: queryKeys.reviewContext.detail(reviewId) });
      const previous = queryClient.getQueryData(queryKeys.reviewContext.detail(reviewId));
      queryClient.setQueryData(queryKeys.reviewContext.detail(reviewId), (old: unknown) => {
        if (typeof old !== 'object' || old === null) {
          return old;
        }
        const record = old as Record<string, unknown>;
        const item = record['item'];
        if (typeof item !== 'object' || item === null) {
          return old;
        }
        return {
          ...record,
          item: { ...(item as Record<string, unknown>), status: optimisticStatusLabel(variables.action) },
        };
      });
      return { previous };
    },
    onError: async (error, _variables, context) => {
      const snapshot = (context as { previous?: unknown } | undefined)?.previous;
      if (snapshot !== undefined) {
        queryClient.setQueryData(queryKeys.reviewContext.detail(reviewId), snapshot);
      }
      if (isReviewConflict(error)) {
        await invalidateDisposition(queryClient, projectId, reviewId);
      }
    },
    onSuccess: async () => {
      await invalidateDisposition(queryClient, projectId, reviewId);
    },
  });
}

/** Invalidates the queue + context after a disposition attempt. */
export async function invalidateDisposition(
  queryClient: QueryClient,
  projectId: string,
  reviewId: string,
): Promise<void> {
  await queryClient.invalidateQueries({ queryKey: queryKeys.review.list(projectId) });
  await queryClient.invalidateQueries({ queryKey: queryKeys.reviewContext.detail(reviewId) });
  await queryClient.invalidateQueries({ queryKey: queryKeys.review.detail(reviewId) });
}
