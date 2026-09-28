/**
 * Task 038 naming facade for end-to-end correlation over the Task 017
 * transport. The canonical implementation lives in `./httpClient.js`
 * (`X-Correlation-ID` per user action, echoed into thrown `ApiError`s); this
 * module re-exports it so Task 038 call sites have a stable path without
 * forking uuid generation or header wiring.
 */

export {
  AUTH_EXPIRED_EVENT,
  CORRELATION_HEADER,
  IDEMPOTENCY_HEADER,
  getLastCorrelationId,
  newCorrelationId,
  newCorrelationId as generateCorrelationId,
  newIdempotencyKey,
} from './httpClient.js';
import { CORRELATION_HEADER, getLastCorrelationId, newCorrelationId } from './httpClient.js';

/** Builds a correlation header pair, minting a fresh id when absent. */
export function buildCorrelationHeaders(correlationId?: string): Record<string, string> {
  return { [CORRELATION_HEADER]: correlationId ?? newCorrelationId() };
}

interface CorrelationCarrier {
  readonly correlationId?: unknown;
}

/** Extracts the correlation id from any thrown value (never throws). */
export function extractCorrelationId(error: unknown): string | undefined {
  try {
    if (typeof error !== 'object' || error === null) {
      return getLastCorrelationId();
    }
    const id = (error as CorrelationCarrier).correlationId;
    if (typeof id === 'string' && id !== '') {
      return id;
    }
    return getLastCorrelationId();
  } catch {
    return undefined;
  }
}
