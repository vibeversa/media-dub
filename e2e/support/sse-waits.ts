// Task 046: event-driven waits. No fixed sleeps.
//
// WHY THIS FILE IS NOT `waitForTimeout`
// -------------------------------------
// A sleep is a bet that the thing you wanted has happened by now. It is wrong in
// both directions at once: too short and it flakes on a slow runner, too long and
// it wastes wall-clock on every run while still being able to flake. Worse, it
// cannot distinguish "not yet" from "never" - a stream that emits nothing at all
// produces the same green test as one that emits promptly.
//
// So every wait here is anchored to a condition the system publishes - a named
// event, a status value, a rendered element - and the deadline is a FAILURE
// REPORT, not a retry policy. When a wait times out the error names the
// condition, the deadline, and everything that WAS observed, which is the
// difference between a diagnosis and a timeout.
//
// TWO SIDES OF THE SAME PROBLEM
// -----------------------------
// `waitForSseEvent` reads the API's stream directly (Node side) and is what a
// cross-layer seam uses. `waitForUiSettled` watches the browser's DOM instead and
// is what a feature spec uses. They share this file because they answer the same
// question in different places, and a repository with one wait helper per
// directory grows three implementations of "wait until this stops changing".

import type { Page } from '@playwright/test';

import { API_BASE_URL, DEFAULT_WAIT_TIMEOUT_MS } from './config.js';

/** One SSE frame. */
export interface SseEvent {
  readonly id: string;
  readonly event: string;
  readonly data: string;
}

/** Thrown when an event-driven wait does not complete. */
export class SseWaitTimeoutError extends Error {
  readonly seen: readonly string[];

  constructor(message: string, seen: readonly string[]) {
    super(
      `${message}\n` +
        `Observed event types: ${seen.length === 0 ? '<none - the stream never emitted>' : seen.join(', ')}`,
    );
    this.name = 'SseWaitTimeoutError';
    this.seen = seen;
  }
}

/** Thrown when the stream does not open at all. */
export class SseOpenError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'SseOpenError';
  }
}

/** An open progress stream. */
export class HarnessSseClient {
  private readonly waiters: Array<() => void> = [];
  private readonly events: SseEvent[] = [];
  private readonly decoder = new TextDecoder();
  private reader: ReadableStreamDefaultReader<Uint8Array> | undefined;
  private pump: Promise<void> | undefined;

  private constructor(
    private readonly response: Response,
    private readonly controller: AbortController,
  ) {}

  /**
   * Opens `GET /api/v1/projects/{projectId}/progress/stream`.
   *
   * The bearer goes in a header, not in `?access_token=`. `EventSource` cannot set
   * headers, which is why the API accepts the query form - but a token in a URL
   * reaches every access log and every proxy log on the way, and this client does
   * not have that limitation.
   *
   * @param token - The access token.
   * @param projectId - The project whose stream to open.
   */
  static async open(token: string, projectId: string): Promise<HarnessSseClient> {
    const controller = new AbortController();
    const response = await fetch(
      `${API_BASE_URL}/api/v1/projects/${projectId}/progress/stream`,
      {
        headers: { Authorization: `Bearer ${token}`, Accept: 'text/event-stream' },
        // The same controller `close()` aborts. A throwaway controller here makes
        // `close()` depend entirely on `reader.cancel()`, which leaves the
        // underlying socket to the garbage collector and keeps the API's
        // connection open between specs.
        signal: controller.signal,
      },
    );

    if (!response.ok || response.body === null) {
      controller.abort();
      throw new SseOpenError(
        `Progress stream did not open: HTTP ${response.status}, ` +
          `content-type ${response.headers.get('content-type') ?? '<none>'}. ` +
          'The processing-start -> SSE seam is broken.',
      );
    }

    const client = new HarnessSseClient(response, controller);
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

        // SSE frames are separated by a blank line. `\r\n` is the wire form and
        // `\n\n` is what a compliant client must also accept.
        let boundary = buffer.search(/\r?\n\r?\n/);
        while (boundary !== -1) {
          const frame = buffer.slice(0, boundary);
          buffer = buffer.slice(boundary).replace(/^\r?\n\r?\n/, '');
          const parsed = parseSseFrame(frame);
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

  /** Every event received so far, in order. */
  get received(): readonly SseEvent[] {
    return this.events;
  }

  /** The distinct event types received so far. */
  get eventTypes(): readonly string[] {
    return [...new Set(this.events.map((event) => event.event))];
  }

  /**
   * Resolves when an event of `type` arrives, or rejects at `timeoutMs`.
   *
   * Already-received events count, so calling this after the fact is safe - which
   * matters because a spec that opens the stream and then does setup work would
   * otherwise miss an event that arrived during the setup.
   *
   * @param type - The SSE event name.
   * @param timeoutMs - Deadline.
   */
  async waitForEvent(type: string, timeoutMs = DEFAULT_WAIT_TIMEOUT_MS): Promise<SseEvent> {
    return this.waitFor(
      (event) => event.event === type,
      timeoutMs,
      `No '${type}' event within ${timeoutMs}ms on the progress stream.`,
    );
  }

  /**
   * Resolves when any event whose `data` satisfies `predicate` arrives.
   *
   * The workspace seam needs this: a run reports progress through several event
   * types, and asserting one exact name would couple the spec to a pipeline
   * detail it does not own.
   *
   * @param predicate - Applied to each event's parsed `data`.
   * @param timeoutMs - Deadline.
   * @param describe - What the predicate means, for the failure message.
   */
  async waitForPayload(
    predicate: (payload: Record<string, unknown>) => boolean,
    describe: string,
    timeoutMs = DEFAULT_WAIT_TIMEOUT_MS,
  ): Promise<SseEvent> {
    return this.waitFor(
      (event) => {
        try {
          return predicate(JSON.parse(event.data) as Record<string, unknown>);
        } catch {
          return false;
        }
      },
      timeoutMs,
      `No event satisfying ${describe} within ${timeoutMs}ms on the progress stream.`,
    );
  }

  private async waitFor(
    matches: (event: SseEvent) => boolean,
    timeoutMs: number,
    message: string,
  ): Promise<SseEvent> {
    const deadline = Date.now() + timeoutMs;
    for (;;) {
      const found = this.events.find(matches);
      if (found !== undefined) {
        return found;
      }
      if (Date.now() >= deadline) {
        throw new SseWaitTimeoutError(message, this.eventTypes);
      }
      await this.waitForChange(Math.min(250, Math.max(0, deadline - Date.now())));
    }
  }

  /** Wakes on the next event, bounded so a dead stream still fails. */
  private waitForChange(timeoutMs: number): Promise<void> {
    if (timeoutMs <= 0) {
      return Promise.resolve();
    }
    return new Promise((resolve) => {
      const onChange = (): void => {
        clearTimeout(timer);
        resolve();
      };
      const timer = setTimeout(() => {
        const index = this.waiters.indexOf(onChange);
        if (index !== -1) {
          this.waiters.splice(index, 1);
        }
        resolve();
      }, timeoutMs);
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

/** How long the rendered element set must be unchanged before a spec proceeds. */
export const CONTENT_QUIET_MS = 600;

/** Ceiling on the stability wait, so a permanently-churning screen fails loudly. */
export const CONTENT_STABILITY_TIMEOUT_MS = 20_000;

/**
 * Waits until the rendered element set stops changing.
 *
 * The workspace mounts its root element and then fills cost, config and activity
 * panels asynchronously, so a capture taken on mount is a coin flip - and a coin
 * flip in a *visual* baseline is the worst possible outcome, because it is green
 * and meaningless. Polling a fingerprint of visible testids and their text
 * lengths catches both a panel appearing and its text streaming in.
 *
 * Fails loudly rather than returning on a churning screen: a baseline of a page
 * that is still changing will fail at a random moment later and be blamed on
 * something else.
 *
 * @param page - The page to watch.
 * @param quietMs - How long the fingerprint must be unchanged.
 * @param timeoutMs - Ceiling on the whole wait.
 */
export async function waitForUiSettled(
  page: Page,
  quietMs = CONTENT_QUIET_MS,
  timeoutMs = CONTENT_STABILITY_TIMEOUT_MS,
): Promise<void> {
  await page.evaluate(() => document.fonts.ready.then(() => undefined));

  const deadline = Date.now() + timeoutMs;
  let previous = await contentFingerprint(page);
  let stableSince = Date.now();

  for (;;) {
    // A short poll interval, not a fixed sleep: the point is to notice the change
    // promptly, and the loop exits on the observed condition rather than on a
    // number of iterations.
    await page.waitForTimeout(120);
    const current = await contentFingerprint(page);

    if (current === previous) {
      if (Date.now() - stableSince >= quietMs) {
        return;
      }
    } else {
      previous = current;
      stableSince = Date.now();
    }

    if (Date.now() > deadline) {
      throw new Error(
        'The screen never stopped changing, so nothing downstream of this wait can be trusted. ' +
          `url=${page.url()}. Waited ${timeoutMs}ms for the rendered element set to be stable ` +
          `for ${quietMs}ms. Something on this screen polls or animates continuously; either ` +
          'fix it or give this screen its own quiet window.',
      );
    }
  }
}

/**
 * Waits for an element to appear and to have finished rendering.
 *
 * The two-step shape matters: `waitFor` proves the node exists, which is not the
 * same as the screen being ready, and a spec that only waited for the node would
 * screenshot a half-populated screen.
 *
 * @param page - The page.
 * @param testId - The `data-testid` to wait for.
 */
export async function waitForScreen(page: Page, testId: string): Promise<void> {
  await page.getByTestId(testId).first().waitFor({ state: 'visible', timeout: 30_000 });
  await waitForUiSettled(page);
}

/**
 * A cheap fingerprint of what is currently rendered.
 *
 * Visible testids plus their text lengths. Lengths rather than the text itself,
 * so a date that formats differently between runs does not read as "changed".
 */
async function contentFingerprint(page: Page): Promise<string> {
  return page.evaluate(() => {
    const nodes = [...document.querySelectorAll('[data-testid]')];
    const visible = nodes.filter((node) => {
      const style = window.getComputedStyle(node);
      return style.visibility !== 'hidden' && style.display !== 'none';
    });
    return `${visible.length}:${visible
      .map((node) => `${node.getAttribute('data-testid')}=${(node.textContent ?? '').length}`)
      .sort()
      .join('|')}`;
  });
}

/**
 * Parses one SSE frame.
 *
 * Exported because the parser is the one piece of this file with a
 * falsifiable contract that needs no network: a spec that cannot open a stream
 * still needs to be able to prove the frame format is read correctly, and
 * exporting it is cheaper than a fixture server.
 *
 * @param frame - The raw text between two blank lines.
 * @returns The event, or `undefined` when the frame carries no data.
 */
export function parseSseFrame(frame: string): SseEvent | undefined {
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