// Task 040B: shared context for the seven seam specs.
//
// Each seam gets the same three things and nothing more: an API client, a
// signed-in token, and the seeded fixture ids. Centralising it is what keeps
// R2 true - "no per-spec bespoke boot" - because a spec that assembled its own
// context would be free to re-seed, re-login or pick its own tenant.
//
// Every seam also gets `sameId`, because three id shapes are in play (040A
// README) and each seam compares ids across layers.

import { ApiClient, SEED, readEnvironment } from '../harness/index.js';
import type { CrossLayerEnvironment } from '../harness/index.js';

export interface SeamContext {
  readonly api: ApiClient;
  readonly token: string;
  /** Public project id (`prj_...`), which is what every route takes. */
  readonly projectId: string;
  readonly environment: CrossLayerEnvironment;
  readonly tenantId: string;
}

/**
 * Normalises any id shape the API mixes into one comparable form: REST speaks
 * public ids (`prj_3333...`), the SSE envelope carries internal GUIDs in the
 * 32-character N format, and the seeder uses the dashed D format.
 */
export function sameId(left: string, right: string): boolean {
  const normalise = (value: string): string =>
    value.toLowerCase().replace(/^[a-z]+_/, '').replace(/-/g, '');
  return normalise(left) === normalise(right);
}

let cachedContext: Promise<SeamContext> | undefined;
let cachedPipelineContext: Promise<PipelineSeamContext> | undefined;

export interface PipelineSeamContext extends SeamContext {
  /**
   * The pipeline project's public id. A project has one run slot, and the 040A
   * harness smoke already starts a run on the pilot project - so a processing
   * seam that shares the pilot project is asserting against the smoke's
   * leftover, which is the 040B edge case "seam passes alone but fails in full
   * suite".
   */
  readonly pipelineProjectId: string;
}

/**
 * Signs in as the seeded owner and resolves the seeded project's public id.
 *
 * <para>
 * Memoised, and that is a requirement rather than an optimisation. The API rate
 * limits `POST /auth/login` to 5/min per IP, and a suite of seven seams that
 * logs in per test blows through it and then fails with `429` on a request that
 * has nothing to do with the seam under test. One login per Playwright worker is
 * also what R2 asks for: the rig is booted once, so the caller signs in once.
 * </para>
 *
 * <p>
 * Fails closed: a login that does not return a token, or a project that is not
 * listed, throws with the reason rather than letting a seam run against a rig
 * that was never seeded.
 * </p>
 */
export function openSeamContext(): Promise<SeamContext> {
  cachedContext ??= signInAndResolve();
  return cachedContext;
}

/** As `openSeamContext`, plus the pipeline project a run-starting seam needs. */
export function openPipelineSeamContext(): Promise<PipelineSeamContext> {
  cachedPipelineContext ??= openSeamContext().then(async (context) => ({
    ...context,
    pipelineProjectId: await context.api.resolveSeededPipelineProjectId(context.token),
  }));
  return cachedPipelineContext;
}

async function signInAndResolve(): Promise<SeamContext> {
  const environment = readEnvironment();
  const api = new ApiClient();

  const pair = await api.login(environment.tenantId, environment.externalSubject);
  if (pair.accessToken.trim().length === 0) {
    throw new Error('Login returned an empty access token; the rig is not usable.');
  }

  const projectId = await api.resolveSeededProjectId(pair.accessToken);

  return {
    api,
    token: pair.accessToken,
    projectId,
    environment,
    tenantId: environment.tenantId,
  };
}

/** The tenant the rig seeds, for the cross-tenant assertions (404, no leak). */
export const SEEDED_TENANT_ID = SEED.tenantId;

/**
 * A second, non-member tenant id used to assert that cross-tenant reads are
 * 404 and do not leak existence. It is never seeded, so nothing exists for it -
 * which is exactly the point: a request against it must be indistinguishable
 * from a request against an id that never existed.
 */
export const FOREIGN_TENANT_ID = '99999999-9999-9999-9999-999999999999';

/** A project id that was never created, for the same existence-leak assertion. */
export const UNKNOWN_PROJECT_ID = 'prj_00000000000000000000000000000000';
