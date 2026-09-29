// Task 040B seam 2: processing start -> SSE events -> workspace refetch as the
// source of truth.
//
// Owning tasks: 024/026 + 008.
//
// The claim under test is that the browser does not treat its own optimistic
// state as truth: the stream tells it *that* something happened, and the
// workspace read tells it *what is actually stored*. A seam that only checked
// "an event arrived" would pass with a stream that emits nothing but heartbeats
// and a workspace that never reflects the run.
//
// R3: the SSE envelope is compared against the database, not just against the
// start response, so a stream that reports a run the API never persisted fails.

import { expect, test } from '@playwright/test';

import {
  SseClient,
  SseTimeoutError,
  assertMockPipelineConsistent,
  assertRunArtifacts,
  countRows,
  readProjectRow,
} from '../harness/index.js';
import { openPipelineSeamContext, sameId } from './seamContext.js';

/** Bounded, not a sleep: the deadline is a failure report. */
const RUN_WAIT_MS = 120_000;

/**
 * Serial, sharing one started run.
 *
 * The seeded project has exactly one run slot: a start pins an active run and a
 * second start is 409 by design. Three independent tests each starting a run
 * would therefore be testing each other's leftovers - which is precisely the
 * 040B edge case "seam passes alone but fails in full suite". The fix is not to
 * work around the 409; it is to start once and let the dependent assertions
 * observe that run.
 */
test.describe.serial('@cross-layer processing-sse', () => {
  let started: { runId: string; projectId: string; status: string };
  let token: string;
  let projectId: string;

  test.beforeAll(async () => {
    const context = await openPipelineSeamContext();
    token = context.token;
    projectId = context.pipelineProjectId;
    started = await context.api.startProcessing(token, projectId, `seam-processing-${Date.now()}`);
    expect(started.runId, 'the start must be accepted').toBeTruthy();
  });

  test('a started run is reported on the stream and is the state the workspace serves', async ({
    page,
  }, testInfo) => {
    const { api } = await openPipelineSeamContext();

    // The frontend is the origin of the interaction even though the API calls
    // come from the harness: loading the bundle first proves the browser can
    // reach the same API the seam then drives.
    await page.goto('/', { waitUntil: 'domcontentloaded' });

    // --- SSE: event-driven, never a fixed sleep -----------------------------
    // The stream resolves the project's *active* run, so it is opened after the
    // start. Opening it first returns 404 (040A README).
    const stream = await SseClient.open(token, projectId);
    let workspace;
    try {
      const progress = await stream.waitForEvent('stage.progress', RUN_WAIT_MS);
      const envelope = JSON.parse(progress.data) as Record<string, unknown>;

      // The envelope's ids are internal GUIDs in 32-char N form while the REST
      // surface speaks public ids, so identity is compared normalised.
      expect(
        sameId(String(envelope['projectId']), projectId),
        'the event must be about the started project',
      ).toBe(true);
      expect(
        sameId(String(envelope['processingRunId']), started.runId),
        'the event must be about the started run',
      ).toBe(true);
      expect(envelope['eventId'], 'every event carries an id for replay/ordering').toBeTruthy();
      expect(
        envelope['schemaVersion'],
        'the envelope is versioned so a client can detect a shape change',
      ).toBeTruthy();

      // --- workspace refetch is the source of truth -------------------------
      // The point of the seam: the stream says progress happened, and the
      // workspace is where the client must read what actually exists.
      workspace = await api.readWorkspace(token, projectId);
      expect(workspace.project.id).toBe(projectId);

      // A workspace that still reports the pre-start state would mean the
      // stream's claim and the stored state disagree.
      expect(
        workspace.project.status,
        'the workspace must reflect the transition the stream announced',
      ).not.toBe('MediaReady');
      expect(workspace.stage, 'the workspace must name a pipeline stage').toBeTruthy();

      // The run the workspace reports must be the run that was started.
      if (workspace.run !== null) {
        expect(
          sameId(workspace.run.id, started.runId),
          'the workspace must report the started run',
        ).toBe(true);
      }

      assertMockPipelineConsistent({ runStatus: started.status, workspace });

      // --- R3: the run is durably recorded ----------------------------------
      await assertRunArtifacts({
        api,
        token,
        projectId,
        run: started,
        workspace,
      });

      // The database agrees with both the start response and the stream.
      const projectRow = await readProjectRow(projectId);
      expect(
        sameId(projectRow.activeRunId ?? '', started.runId),
        'the stored project must point at the started run',
      ).toBe(true);

      const runs = await countRows('processing_runs', projectId);
      expect(runs, 'the run must be persisted').toBeGreaterThanOrEqual(1);

      testInfo.annotations.push({
        type: 'rig',
        description:
          `runId=${started.runId} runStatus=${started.status} ` +
          `projectStatus=${projectRow.status} stage=${workspace.stage} ` +
          `events=${stream.eventTypes.join('|')} runsInDb=${runs}`,
      });
    } finally {
      // Unconditional: a leaked stream holds the API connection open and makes
      // the next seam's reset contend.
      await stream.close();
    }
  });

  test('a second start while a run is active is refused with the active run named', async () => {
    const { api } = await openPipelineSeamContext();

    // The failure half: the guard is a named refusal, not a second run.
    const second = await api.requestRaw({
      method: 'POST',
      path: `/api/v1/projects/${projectId}/processing`,
      token,
      body: {},
      headers: { 'Idempotency-Key': `seam-processing-b-${Date.now()}` },
    });

    expect(second.status, `a concurrent start must be refused, got ${second.status}`).toBe(409);
    const code = (second.body as { error?: { code?: string } })?.error?.code;
    expect(code, 'the refusal must be a named code').toBe('RUN_ALREADY_ACTIVE');

    // The refusal must name the run that holds the project, so a client can
    // attach to it rather than guess. The message carries the internal GUID in
    // dashed form, so the comparison is made dash-insensitively.
    const message = (second.body as { error?: { message?: string } })?.error?.message ?? '';
    const dashed = (value: string): string => value.toLowerCase().replace(/^[a-z]+_/, '').replace(/-/g, '');
    expect(
      dashed(message).includes(dashed(started.runId)),
      `the refusal must name the active run ('${started.runId}'), got: ${message}`,
    ).toBe(true);

    // And it must not have created a second run: the seeded anchor plus the one
    // active run started in beforeAll.
    const runs = await countRows('processing_runs', projectId);
    expect(
      runs,
      'a refused start must not persist a run (seeded anchor + the active run)',
    ).toBe(2);
  });

  test('an unauthenticated stream request is refused and leaks no run detail', async () => {
    const { api } = await openPipelineSeamContext();

    const anonymous = await api.requestRaw({
      method: 'GET',
      path: `/api/v1/projects/${projectId}/progress/stream`,
    });

    expect(anonymous.status, 'an unauthenticated stream read must be 401').toBe(401);
    const body = JSON.stringify(anonymous.body).toLowerCase().replace(/-/g, '');
    expect(
      body,
      'the refusal must not name the run that exists',
    ).not.toContain(started.runId.toLowerCase().replace(/^run_/, ''));
  });
});
