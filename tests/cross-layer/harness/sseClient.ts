// Task 040A: event-driven SSE client.
//
// 040A instruction 2 requires "event-driven waits from 046, no sleeps" and R3
// requires the smoke to pass deterministically. A polling loop with a fixed
// sleep is the thing this replaces: it either wastes wall-clock time on a fast
// run or flakes on a slow one, and it hides a stream that never emits at all.
//
// Every wait here is anchored to a condition the API publishes - a named event,
// a status value, a terminal run state - with a deadline as the only backstop.
// The deadline is a failure report, not a retry policy.

import { API_BASE_URL } from './config.js';

export interface SseEvent {
  readonly id: string;
  readonly event: string;
  readonly data: string;
}

export class SseTimeoutError extends Error {
  readonly seen: readonly string[];

  constructor(message: string, seen: readonly string[]) {
    super(`${message}\nObserved event types: ${seen.length === 0 ? '<none>' : seen.join(', ')}`);
    this.name = 'SseTimeoutError';
    this.seen = seen;
  }
}

export class SseClient {
  private readonly controller: AbortController;
  private readonly events: SseEvent[] = [];
  private reader: ReadableStreamDefaultReader<Uint8Array> | undefined;
  private pump: Promise<void> | undefined;
  private readonly waiters: Array<() => void> = [];

  private constructor(
    private readonly response: Response,
    private readonly decoder: TextDecoder,
    controller: AbortController,
  ) {
    this.controller = controller;
  }

  /**
   * Opens the progress stream for `projectId`.
   *
   * GAP-001 (Plan B 9.11): bearer travels in the Authorization header only,
   * never via `?access_token=`. This client passes the header directly, which
   * keeps the token out of URLs and logs.
   */
  static async open(token: string, projectId: string): Promise<SseClient> {
    const controller = new AbortController();
    const response = await fetch(
      `${API_BASE_URL}/api/v1/projects/${projectId}/progress/stream`,
      {
        headers: {
          Authorization: `Bearer ${token}`,
          Accept: 'text/event-stream',
        },
        // The same controller `close()` aborts. A throwaway controller here
        // (as an earlier revision had) makes `close()` depend entirely on
        // `reader.cancel()`, which leaves the underlying socket to the garbage
        // collector and keeps the API's connection open between specs.
        signal: controller.signal,
      },
    );

    if (!response.ok || response.body === null) {
      controller.abort();
      throw new Error(
        `Progress stream did not open: ${response.status} ` +
          `${response.headers.get('content-type') ?? 'no content-type'}. ` +
          'The processing-start -> SSE seam is broken.',
      );
    }

    const client = new SseClient(response, new TextDecoder(), controller);
    client.reader = response.body.getReader();
    client.pump = client.consume();
    return client;
  }

  private async consume(): Promise<void> {
    let buffer = '';
    try {
      for (;;) {
        const { value, done } = await this.reader!.read();
        if (done) {
          break;
        }
        buffer += this.decoder.decode(value, { stream: true });

        // SSE frames are separated by a blank line; \r\n is the wire form and
        // \n\n is what a compliant client must also accept.
        let boundary = buffer.search(/\r?\n\r?\n/);
        while (boundary !== -1) {
          const frame = buffer.slice(0, boundary);
          buffer = buffer.slice(boundary).replace(/^\r?\n\r?\n/, '');
          const parsed = parseFrame(frame);
          if (parsed !== undefined) {
            this.events.push(parsed);
            this.release();
          }
          boundary = buffer.search(/\r?\n\r?\n/);
        }
      }
    } catch {
      // Abort during teardown is the normal exit path, not a failure.
    }
  }

  private release(): void {
    while (this.waiters.length > 0) {
      this.waiters.pop()?.();
    }
  }

  /** All events received so far, in order. */
  get received(): readonly SseEvent[] {
    return this.events;
  }

  get eventTypes(): readonly string[] {
    return [...new Set(this.events.map((event) => event.event))];
  }

  /**
   * Resolves when an event of `type` arrives, or rejects at `timeoutMs`.
   * Already-received events count, so calling this after the fact is safe.
   */
  async waitForEvent(type: string, timeoutMs: number): Promise<SseEvent> {
    const deadline = Date.now() + timeoutMs;
    for (;;) {
      const found = this.events.find((event) => event.event === type);
      if (found !== undefined) {
        return found;
      }
      if (Date.now() >= deadline) {
        throw new SseTimeoutError(
          `No '${type}' event within ${timeoutMs}ms on the progress stream.`,
          this.eventTypes,
        );
      }
      await this.waitForChange(Math.min(250, Math.max(0, deadline - Date.now())));
    }
  }

  /**
   * Resolves when any event whose `data` JSON satisfies `predicate` arrives.
   * The workspace seam needs this: the run reports progress through several
   * event types, and asserting one exact name would couple the rig to a
   * pipeline detail it does not own.
   */
  async waitForPayload(
    predicate: (payload: Record<string, unknown>) => boolean,
    timeoutMs: number,
    describe: string,
  ): Promise<SseEvent> {
    const matches = (event: SseEvent): boolean => {
      try {
        return predicate(JSON.parse(event.data) as Record<string, unknown>);
      } catch {
        return false;
      }
    };

    const deadline = Date.now() + timeoutMs;
    for (;;) {
      const found = this.events.find(matches);
      if (found !== undefined) {
        return found;
      }
      if (Date.now() >= deadline) {
        throw new SseTimeoutError(
          `No event satisfying ${describe} within ${timeoutMs}ms on the progress stream.`,
          this.eventTypes,
        );
      }
      await this.waitForChange(Math.min(250, Math.max(0, deadline - Date.now())));
    }
  }

  /** Waits for the next batch of events, bounded so a dead stream still fails. */
  private waitForChange(timeoutMs: number): Promise<void> {
    if (timeoutMs <= 0) {
      return Promise.resolve();
    }
    return new Promise((resolve) => {
      const timer = setTimeout(() => {
        const index = this.waiters.indexOf(onChange);
        if (index !== -1) {
          this.waiters.splice(index, 1);
        }
        resolve();
      }, timeoutMs);
      const onChange = (): void => {
        clearTimeout(timer);
        resolve();
      };
      this.waiters.push(onChange);
    });
  }

  async close(): Promise<void> {
    this.controller.abort();
    this.release();
    try {
      await this.reader?.cancel();
    } catch {
      // Already closed.
    }
    await this.pump;
  }
}

function parseFrame(frame: string): SseEvent | undefined {
  let id = '';
  let event = 'message';
  const data: string[] = [];

  for (const rawLine of frame.split(/\r?\n/)) {
    if (rawLine.length === 0 || rawLine.startsWith(':')) {
      continue;
    }
    const colon = rawLine.indexOf(':');
    const field = colon === -1 ? rawLine : rawLine.slice(0, colon);
    const value = colon === -1 ? '' : rawLine.slice(colon + 1).replace(/^ /, '');

    if (field === 'id') {
      id = value;
    } else if (field === 'event') {
      event = value;
    } else if (field === 'data') {
      data.push(value);
    }
  }

  if (data.length === 0) {
    return undefined;
  }
  return { id, event, data: data.join('\n') };
}
