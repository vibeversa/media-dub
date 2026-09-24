import { useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import type { UseMutationResult } from '@tanstack/react-query';
import { newIdempotencyKey } from './client/index.js';
import { normalizeError } from './errors/index.js';
import type { AppError } from './errors/index.js';

// Task 017 barrel: feature code reaches the generated client, query keys,
// and error normalization through here (or `client` directly). Deep imports
// of `api/generated` remain banned by `no-restricted-imports`.
export * from './client/index.js';
export * from './errors/index.js';
export * from './queryKeys/index.js';

export interface AppMutationContext {
  /** Stable for the attempt; server dedupes double-submits on this value. */
  readonly idempotencyKey: string;
}

export interface UseAppMutationArgs<TData, TVariables> {
  /**
   * Receives the caller variables plus a mutation context. Send
   * `ctx.idempotencyKey` as the `Idempotency-Key` header (generated-client
   * `RequestOptions.idempotencyKey`) so retries reuse the same key.
   */
  readonly mutationFn: (variables: TVariables, ctx: AppMutationContext) => Promise<TData>;
  /**
   * Caller-owned key (e.g. resumed upload). When absent, one is generated
   * per attempt and rotated after settle, so rapid double-submits share a
   * key (one effective request) while later distinct submits differ.
   */
  readonly idempotencyKey?: string;
}

/**
 * Mutation wrapper: attaches an idempotency key and normalizes every failure
 * to `AppError` (mutations never auto-retry; the UI retries explicitly with
 * the same key). Returns TanStack's `UseMutationResult` typed with
 * `AppError` as the error type.
 */
export function useAppMutation<TData, TVariables = void>(
  args: UseAppMutationArgs<TData, TVariables>,
): UseMutationResult<TData, AppError, TVariables> {
  const callerKey = args.idempotencyKey;
  const [attemptKey, setAttemptKey] = useState<string>(() => callerKey ?? newIdempotencyKey());
  const activeKey = callerKey ?? attemptKey;

  return useMutation<TData, AppError, TVariables>({
    mutationFn: async (variables: TVariables): Promise<TData> => {
      try {
        return await args.mutationFn(variables, { idempotencyKey: activeKey });
      } catch (error) {
        throw normalizeError(error, { method: 'POST' });
      }
    },
    onSettled: (): void => {
      if (callerKey === undefined) {
        setAttemptKey(newIdempotencyKey());
      }
    },
  });
}
