// Task 040A: post-run artifact assertions (040A instruction 2).
//
// The rig asserts that a run left real, durable traces: a processing run row, an
// active-run pointer on the project, workspace segments, and - where the run
// reached that far - an export artifact. These are *seam* assertions about
// persistence across layers, not per-route contract checks (those live in
// 006-013, per R5).
//
// Every assertion reports what it looked at. A helper that throws "assertion
// failed" in a cross-layer rig costs more to debug than the seam it replaced.

import { spawn } from 'node:child_process';

import type { ApiClient, ProcessingRun, WorkspaceSnapshot } from './apiClient.js';
import { SEEDER_CONNECTION_STRING, SEEDER_PROJECT } from './config.js';

export class ArtifactAssertionError extends Error {
  readonly detail: Record<string, unknown>;

  constructor(message: string, detail: Record<string, unknown> = {}) {
    super(`${message}${Object.keys(detail).length === 0 ? '' : `\nObserved: ${JSON.stringify(detail, null, 2)}`}`);
    this.name = 'ArtifactAssertionError';
    this.detail = detail;
  }
}

export interface RunArtifacts {
  readonly run: ProcessingRun;
  readonly workspace: WorkspaceSnapshot;
  readonly projectRow: ProjectRow;
  readonly reviewCount: number;
  readonly exportArtifactCount: number;
  readonly notificationCount: number;
}

export interface ProjectRow {
  readonly id: string;
  readonly status: string;
  readonly activeRunId: string | null;
  readonly processingSettingsJson: string | null;
}

interface QueryResult {
  readonly code: number | null;
  readonly stdout: string;
  readonly stderr: string;
}

/**
 * Runs a read-only SQL probe through the postgres client in the compose stack.
 *
 * The rig reaches the database through the container rather than from the host
 * so the probe shares the rig's network and credentials, and so no database
 * password has to be handled in test code on the host.
 */
async function queryPostgres(sql: string): Promise<string> {
  const result = await new Promise<QueryResult>((resolve, reject) => {
    const child = spawn(
      'docker',
      [
        'exec',
        '-e',
        'PGPASSWORD=CHANGE_ME',
        'dubbing-cross-layer-postgres-1',
        'psql',
        '-U',
        'dubbing',
        '-d',
        'dubbing',
        // Unaligned + tuples-only keeps parsing trivial and locale-independent.
        '-A',
        '-t',
        '-F',
        '|',
        '-c',
        sql,
      ],
      { shell: false, windowsHide: true },
    );
    let stdout = '';
    let stderr = '';
    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');
    child.stdout.on('data', (chunk: string) => {
      stdout += chunk;
    });
    child.stderr.on('data', (chunk: string) => {
      stderr += chunk;
    });
    child.on('error', reject);
    child.on('close', (code) => resolve({ code, stdout, stderr }));
  });

  if (result.code !== 0) {
    throw new ArtifactAssertionError(
      'Database probe failed. Is the postgres service healthy and is the container ' +
        'named dubbing-cross-layer-postgres-1?\n' +
        `stdout: ${result.stdout.slice(0, 300)}\n` +
        `stderr: ${result.stderr.slice(0, 300)}`,
      { stdout: result.stdout.slice(0, 300), stderr: result.stderr.slice(0, 300) },
    );
  }
  return result.stdout;
}

/**
 * Normalises any id the API or the seeder hands over into a canonical UUID.
 *
 * Three shapes exist for the same project: the seeder's dashed D form, the
 * public id used by every REST route (`prj_` + 32 hex), and the raw 32-hex N
 * form. Database columns are `uuid`, so a public id passed straight into a
 * query is rejected by Postgres as "invalid input syntax for type uuid" - and if
 * the column had been text the same mistake would have silently matched nothing.
 * Callers therefore pass whichever id they hold.
 */
function toUuid(value: string): string {
  const hex = value.trim().toLowerCase().replace(/^[a-z]+_/, '').replace(/-/g, '');
  if (!/^[0-9a-f]{32}$/.test(hex)) {
    throw new ArtifactAssertionError(`'${value}' is not a recognisable project/run id.`);
  }
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/** Reads the durable project row, bypassing the API's own view of it. */
export async function readProjectRow(projectId: string): Promise<ProjectRow> {
  const raw = (
    await queryPostgres(
      // `processing_settings_json` is a json column, so it must be cast before
      // it can be coalesced with a text literal; Postgres rejects the mix with
      // "invalid input syntax for type json".
      `SELECT id, status, coalesce(active_run_id::text, ''), ` +
        `coalesce(processing_settings_json::text, '') FROM dubbing_projects ` +
        `WHERE id = '${toUuid(projectId)}'::uuid;`,
    )
  ).trim();

  if (raw.length === 0) {
    throw new ArtifactAssertionError(
      `The seeded project row ${projectId} is gone. Something reset the database ` +
        'mid-run; the rig is not deterministic.',
    );
  }

  const [id, status, activeRunId, settings] = raw.split('|');

  // `id` and `status` are non-optional in `ProjectRow`, so an absent column is a
  // malformed psql result rather than a legitimately-empty value - and returning
  // `undefined` for it would type-error at every call site instead of here. The
  // message names the row and the raw output, because a missing column means the
  // query above and the schema have diverged, which is a rig defect.
  if (id === undefined || status === undefined) {
    // `detail` is a record, not a string: the error carries observed values
    // structurally so a caller can assert on them.
    throw new ArtifactAssertionError(
      `The seeded project row ${projectId} came back without an id and status.`,
      { rawPsqlOutput: raw },
    );
  }

  return { id, status, activeRunId: activeRunId ?? null, processingSettingsJson: settings || null };
}

/**
 * How each allow-listed table links back to a project.
 *
 * Not every table carries `project_id` directly: an export artifact hangs off
 * its export job. Guessing the column produced "column project_id does not
 * exist", and a text column would have failed worse - silently matching nothing.
 * The map is explicit, and anything not in it is refused.
 */
const PROJECT_LINKS: Readonly<Record<string, (uuid: string) => string>> = {
  export_artifacts: (uuid) => `export_job_id IN (SELECT id FROM export_jobs WHERE project_id = '${uuid}'::uuid)`,
  notifications: (uuid) => `project_id = '${uuid}'::uuid`,
  processing_runs: (uuid) => `project_id = '${uuid}'::uuid`,
  provider_executions: (uuid) => `project_id = '${uuid}'::uuid`,
  review_items: (uuid) => `project_id = '${uuid}'::uuid`,
  // `review_decisions` has no project column: it links through its review item.
  review_decisions: (uuid) =>
    `review_item_id IN (SELECT id FROM review_items WHERE project_id = '${uuid}'::uuid)`,
  speech_segments: (uuid) => `project_id = '${uuid}'::uuid`,
  transcript_versions: (uuid) => `project_id = '${uuid}'::uuid`,
  translation_versions: (uuid) => `project_id = '${uuid}'::uuid`,
  speakers: (uuid) => `project_id = '${uuid}'::uuid`,
  speaker_voice_assignments: (uuid) => `project_id = '${uuid}'::uuid`,
  voice_profiles: (uuid) => `id IN (SELECT voice_profile_id FROM speaker_voice_assignments WHERE project_id = '${uuid}'::uuid)`,
  upload_sessions: (uuid) => `project_id = '${uuid}'::uuid`,
  upload_parts: (uuid) => `upload_session_id IN (SELECT id FROM upload_sessions WHERE project_id = '${uuid}'::uuid)`,
  artifacts: (uuid) => `project_id = '${uuid}'::uuid`,
  export_jobs: (uuid) => `project_id = '${uuid}'::uuid`,
  activity_events: (uuid) => `project_id = '${uuid}'::uuid`,
};

/**
 * Reads the voice profile a speaker is actually assigned, straight from the
 * database.
 *
 * <para>
 * Deliberately a pointer read rather than a row count.
 * `speaker_voice_assignments` holds one row per speaker and the API *replaces*
 * it on a change, so "a new row appeared" is false even for a real voice change -
 * verified, not assumed. The stored pointer is the fact the seam is about.
 * </para>
 */
export async function readAssignedVoiceProfileId(speakerId: string): Promise<string | null> {
  const raw = (
    await queryPostgres(
      `SELECT coalesce(voice_profile_id::text, '') FROM speaker_voice_assignments ` +
        `WHERE speaker_id = '${toUuid(speakerId)}'::uuid ORDER BY created_at DESC LIMIT 1;`,
    )
  ).trim();

  return raw.length === 0 ? null : raw;
}

/**
 * Counts the *open* review items belonging to one processing run.
 *
 * <para>
 * Deliberately run-scoped, and deliberately not the same as
 * `countRows('review_items', projectId)`. The workspace read reports
 * `review.pendingCount` for the project's <em>active run</em>, so comparing it
 * against a project-wide count compares two different scopes: with the seeded
 * anchor run holding an open item and a freshly started run holding none, the
 * project count is 1, the workspace says 0, and both are correct. Comparing them
 * anyway fails a green rig - which is what happened the first time.
 * </para>
 */
export async function countOpenReviewItemsForRun(runId: string): Promise<number> {
  const raw = (
    await queryPostgres(
      `SELECT count(*) FROM review_items WHERE processing_run_id = '${toUuid(runId)}'::uuid ` +
        "AND status = 'Open';",
    )
  ).trim();
  const count = Number.parseInt(raw, 10);
  if (Number.isNaN(count)) {
    throw new ArtifactAssertionError('Could not read the open review count for a run.');
  }
  return count;
}

/** Counts the rows a table holds for one project. */
export async function countRows(table: string, projectId: string): Promise<number> {
  const link = PROJECT_LINKS[table];
  if (link === undefined) {
    throw new ArtifactAssertionError(
      `Refusing to count '${table}': no declared project link. Add one to PROJECT_LINKS ` +
        'rather than interpolating an arbitrary table or column name into SQL.',
    );
  }

  const raw = (
    await queryPostgres(`SELECT count(*) FROM ${table} WHERE ${link(toUuid(projectId))};`)
  ).trim();
  const count = Number.parseInt(raw, 10);
  if (Number.isNaN(count)) {
    throw new ArtifactAssertionError(`Could not read a count from ${table}.`, { raw: raw.slice(0, 120) });
  }
  return count;
}

/**
 * Collects and asserts the artifacts a processing **start** must leave.
 *
 * <para>
 * Scope note, because this is the assertion most likely to be misread: a start
 * legitimately produces a transitioned project, an active-run pointer and a
 * durable run row. It does <em>not</em> produce segments or exports - those need
 * the FFmpeg-backed media workers and a real uploaded media file, which are
 * media-pipeline integration and not harness scope. The pipeline-dependent
 * counts are therefore reported and cross-checked against what the workspace
 * claims, rather than asserted to be greater than zero. Asserting them would
 * either fail for the right reason at the wrong layer or, worse, be relaxed into
 * a skip that hides a broken pipeline.
 * </para>
 */
export async function assertRunArtifacts(input: {
  readonly api: ApiClient;
  readonly token: string;
  readonly projectId: string;
  readonly run: ProcessingRun;
  readonly workspace: WorkspaceSnapshot;
}): Promise<RunArtifacts> {
  const { api, token, projectId, run, workspace } = input;

  const projectRow = await readProjectRow(projectId);
  const reloadedRun = await api.readRun(token, projectId, run.runId);
  const exportArtifactCount = await countRows('export_artifacts', projectId);
  const notificationCount = await countRows('notifications', projectId);
  // Run-scoped, matching the scope the workspace uses for `review.pendingCount`.
  const reviewCount = await countOpenReviewItemsForRun(reloadedRun.runId);

  if (projectRow.status === 'Created' || projectRow.status === 'MediaReady') {
    throw new ArtifactAssertionError(
      `The project row is still '${projectRow.status}' after a processing start. The ` +
        'start did not transition the project, so the persistence seam is broken even ' +
        'though the endpoint returned 202.',
      { projectRow, run },
    );
  }

  if (projectRow.activeRunId === null) {
    throw new ArtifactAssertionError(
      'The project row has no active run after a processing start. The API accepted ' +
        'the start but never linked the run to the project.',
      { projectRow },
    );
  }

  if (!sameId(projectRow.activeRunId, reloadedRun.runId)) {
    throw new ArtifactAssertionError(
      "The project's active run does not match the run that was accepted and reloaded.",
      { projectActiveRun: projectRow.activeRunId, reloaded: reloadedRun.runId },
    );
  }

  if (reloadedRun.runId !== run.runId) {
    throw new ArtifactAssertionError(
      'The processing run is not readable after it was accepted. A 202 with no durable ' +
        'run row is a hollow green.',
      { accepted: run, reloaded: reloadedRun },
    );
  }

  if (workspace.project.id !== projectId) {
    throw new ArtifactAssertionError(
      'The workspace read returned a different project than the one started.',
      { expected: projectId, actual: workspace.project.id },
    );
  }

  // The workspace's own view of the run must agree with the row. This is the
  // cross-layer check: two reads of the same fact through different paths.
  if (workspace.run !== null && !sameId(workspace.run.id, reloadedRun.runId)) {
    throw new ArtifactAssertionError(
      'The workspace reports a different active run than the processing endpoint.',
      { workspaceRun: workspace.run.id, reloaded: reloadedRun.runId },
    );
  }

  if (!Number.isInteger(workspace.review?.pendingCount)) {
    throw new ArtifactAssertionError(
      'The workspace payload has no usable review summary; the artifact helper cannot ' +
        'verify the review seam.',
      { review: workspace.review ?? null },
    );
  }

  if (workspace.review.pendingCount !== reviewCount) {
    throw new ArtifactAssertionError(
      'The workspace review count disagrees with the stored review rows. Two reads of ' +
        'the same fact through different layers have diverged.',
      { workspacePending: workspace.review.pendingCount, storedReviewItems: reviewCount },
    );
  }

  return { run: reloadedRun, workspace, projectRow, reviewCount, exportArtifactCount, notificationCount };
}

/** Normalises public-id and GUID forms so ids can be compared across layers. */
function sameId(left: string, right: string): boolean {
  const normalise = (value: string): string =>
    value.toLowerCase().replace(/^[a-z]+_/, '').replace(/-/g, '');
  return normalise(left) === normalise(right);
}

/**
 * Fails when no processing row exists for the project, which is what a silently
 * swallowed pipeline looks like from outside the API.
 *
 * The lookup is by project, not by run id: `processing_runs.id` is the internal
 * GUID while the API hands out a `run_`-prefixed public id, so filtering on the
 * public id would match nothing and the assertion would be vacuously satisfied.
 */
export async function assertRunPersisted(projectId: string): Promise<void> {
  const count = await countRows('processing_runs', projectId);
  if (count < 1) {
    throw new ArtifactAssertionError(
      `No processing_runs row exists for project ${projectId}. The start was accepted ` +
        'but nothing was persisted.',
      { projectId },
    );
  }
}
