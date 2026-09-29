// Task 040A: the harness smoke (R1/R2/R3/R4/R5).
//
// 040A instruction 3: "boots stack, seeds, runs one processing-start -> SSE ->
// workspace-read round trip, asserts artifacts, tears down; failing harness
// blocks 040B (fail-closed)."
//
// This is the gate for the whole cross-layer slice. It is not an endpoint
// contract test - per-route status codes belong to 006-013 (R5) - it proves the
// layers are wired to each other: a real browser bundle served over HTTP, a real
// API, a real PostgreSQL row, real object storage, a real SSE stream, and mock
// AI only.

import { expect, test } from '@playwright/test';

import {
  AI_MOCK_UNAVAILABLE,
  API_BASE_URL,
  ApiClient,
  EXPECTED_MOCK_CONFIDENCE,
  FRONTEND_BASE_URL,
  PROVIDER_UNAVAILABLE_CODES,
  SseClient,
  SEED,
  assertMockAiDelivered,
  assertMockPipelineConsistent,
  assertRunArtifacts,
  assertRunPersisted,
  classifyProviderFailure,
  expectedTranscriptText,
} from './harness/index.js';

const RUN_WAIT_MS = 90_000;

/**
 * Normalises any id shape the API mixes into one comparable form.
 *
 * Three shapes are in play and the seam has to bridge them: REST speaks public
 * ids (`prj_3333...`), the SSE event envelope carries internal GUIDs in the
 * 32-character N format with no dashes, and the seeder config uses the dashed D
 * format. Comparing normalised values keeps the assertion about identity rather
 * than about formatting.
 */
function sameId(left: string, right: string): boolean {
  const normalise = (value: string): string =>
    value.toLowerCase().replace(/^[a-z]+_/, '').replace(/-/g, '');
  return normalise(left) === normalise(right);
}

test.describe('@cross-layer-harness', () => {
  // The seeded project is promoted to MediaReady precisely so this start
  // succeeds; a 409 here means the rig's seed regressed, not that the endpoint
  // misbehaved.
  test('boots, seeds, and completes a processing-start -> SSE -> workspace round trip', async ({
    page,
  }, testInfo) => {
    const api = new ApiClient();

    // --- Layer 1: the frontend bundle is really being served. ---------------
    const response = await page.goto('/', { waitUntil: 'domcontentloaded' });
    expect(response?.status(), 'frontend root must be served by the rig').toBe(200);
    expect(new URL(page.url()).origin, 'the rig drives the app on the 127.0.0.1 origin').toBe(
      FRONTEND_BASE_URL,
    );

    // --- Layer 2: auth seam (a real token, never a fabricated one). ----------
    const pair = await api.login(SEED.tenantId, SEED.externalSubject);
    expect(pair.accessToken, 'login must mint an access token').toBeTruthy();

    const identity = await api.readIdentity(pair.accessToken);
    expect(identity.tenant.id).toBe(SEED.tenantId);
    expect(identity.user.id).toBe(SEED.userId);

    // --- Layer 3: the seeded project is visible and startable. ----------------
    const projects = await api.listProjects(pair.accessToken);
    const project = projects.items.find((candidate) => candidate.name === 'Cross Layer Pilot');
    expect(project, 'the seeded project must be listed').toBeDefined();
    expect(project!.status, 'the seeder promotes the project to MediaReady').toBe('MediaReady');

    // --- Layer 4: processing start, then the SSE seam, event-driven. ----------
    // Ordering is load-bearing: `GET /progress/stream` resolves the project's
    // active run and answers 404 when none exists, so the run must be started
    // before the subscription is opened. Opening the stream first and starting
    // afterwards is the intuitive order and it does not work.
    const started = await api.startProcessing(
      pair.accessToken,
      project!.id,
      `cross-layer-harness-${Date.now()}`,
    );
    expect(started.runId, 'processing start must be accepted').toBeTruthy();

    const stream = await SseClient.open(pair.accessToken, project!.id);
    try {
      // Anchored to a published event, not to a sleep: a stream that never
      // emits must fail here, loudly, instead of after a fixed delay.
      const progress = await stream.waitForEvent('stage.progress', RUN_WAIT_MS);
      const payload = JSON.parse(progress.data) as Record<string, unknown>;
      expect(sameId(String(payload['projectId']), project!.id)).toBe(true);
      expect(sameId(String(payload['processingRunId']), started.runId)).toBe(true);

      // --- Layer 5: the workspace read sees the same run. --------------------
      const workspace = await api.readWorkspace(pair.accessToken, project!.id);
      expect(workspace.project.id).toBe(project!.id);
      expect(workspace.project.status).not.toBe('MediaReady');
      expect(workspace.stage, 'the workspace must name a pipeline stage').toBeTruthy();

      // A 202 plus a workspace that reports progress nothing produced is the
      // hollow-green case. This checks cross-layer consistency, not pipeline
      // completion: the rig deliberately does not run the FFmpeg-backed media
      // workers, so requiring finished segments here would test the wrong layer.
      assertMockPipelineConsistent({ runStatus: started.status, workspace });

      // --- Layer 6: durable artifacts. ---------------------------------------
      await assertRunPersisted(project!.id);
      const artifacts = await assertRunArtifacts({
        api,
        token: pair.accessToken,
        projectId: project!.id,
        run: started,
        workspace,
      });
      expect(artifacts.projectRow.status).not.toBe('Created');
      testInfo.annotations.push({
        type: 'rig',
        description:
          `runStatus=${artifacts.run.status} projectStatus=${artifacts.projectRow.status} ` +
          `stage=${workspace.stage} reviewItems=${artifacts.reviewCount} ` +
          `exports=${artifacts.exportArtifactCount} notifications=${artifacts.notificationCount} ` +
          `events=${stream.eventTypes.join('|')}`,
      });
    } finally {
      // Teardown is unconditional: a leaked stream keeps the API connection open
      // and makes the next run's reset contend.
      await stream.close();
    }

    // --- Layer 7: the browser really is wired to the API. --------------------
    // This is the only assertion that involves CORS and the build-time
    // VITE_API_BASE_URL, and it is the one the rest of the rig cannot stand in
    // for: the layers above all run from Node, where the browser's origin rules
    // do not apply. A stale dist or a mismatched allow-list fails here only.
    await page.goto('/', { waitUntil: 'domcontentloaded' });

    // Chromium reports a CORS rejection as an opaque "TypeError: Failed to
    // fetch" with no status, so the underlying request failure is captured to
    // make the message say what actually went wrong.
    const failedRequests: string[] = [];
    page.on('requestfailed', (request) => {
      failedRequests.push(`${request.method()} ${request.url()} -> ${request.failure()?.errorText ?? 'unknown'}`);
    });

    const browserProbe = await page.evaluate(async (apiBase) => {
      try {
        const probe = await fetch(`${apiBase}/health/live`);
        return { ok: probe.ok, status: probe.status, detail: 'no error' };
      } catch (error) {
        return {
          ok: false,
          status: 0,
          detail: `${(error as Error).name}: ${(error as Error).message}`,
        };
      }
    }, API_BASE_URL);

    expect(
      browserProbe.ok,
      `the served bundle must reach ${API_BASE_URL} from origin ${new URL(page.url()).origin} ` +
        `(browser said: ${browserProbe.detail}; failed requests: ${failedRequests.join('; ') || 'none'}). ` +
        "Check that frontend/dist was built with --mode cross-layer, that the CSP in " +
        'frontend/index.html allows the API origin, and that the api service CORS ' +
        'allow-list contains the exact frontend origin.',
    ).toBe(true);
  });

  // R4: the mock-AI outage path must be named and fast, not a hang. This test
  // asserts the rig's own classification logic so the guarantee is pinned even
  // when the rig runs against a healthy provider.
  test('classifies a mock-AI outage with a named error instead of hanging', () => {
    expect(() =>
      assertMockAiDelivered({ runStatus: 'Failed', runDetail: { code: 'PROVIDER_TIMEOUT' }, stalledMs: 12 }),
    ).toThrowError(new RegExp(AI_MOCK_UNAVAILABLE));

    expect(() =>
      assertMockAiDelivered({ runStatus: 'Running', runDetail: {}, stalledMs: 60_000 }),
    ).toThrowError(new RegExp(AI_MOCK_UNAVAILABLE));

    // A healthy in-flight run must NOT raise: the rig fails fast on an outage,
    // not on ordinary latency.
    expect(() =>
      assertMockAiDelivered({ runStatus: 'Running', runDetail: {}, stalledMs: 250 }),
    ).not.toThrow();

    // The classification reaches a nested cause, and maps the product code onto
    // the rig sentinel rather than inventing its own vocabulary.
    for (const code of PROVIDER_UNAVAILABLE_CODES) {
      expect(classifyProviderFailure({ error: { code } })).toBe(code);
      expect(classifyProviderFailure({ stage: { detail: `failed with ${code}` } })).toBe(code);
    }
    expect(classifyProviderFailure({ error: { code: 'NOT_FOUND' } })).toBeUndefined();
  });

  // The deterministic expectations the rig publishes for 040B must match the
  // mock providers they mirror, or a 040B seam written against them asserts the
  // wrong thing.
  test('publishes mock-AI expectations that match the mock providers', () => {
    expect(expectedTranscriptText('artifact-1', 'en')).toBe('mock transcript seg artifact-1 [en]');
    expect(expectedTranscriptText('artifact-1', 'en')).toBe(expectedTranscriptText('artifact-1', 'en'));
    expect(EXPECTED_MOCK_CONFIDENCE).toBeGreaterThan(0.9);
  });
});
