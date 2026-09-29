// Task 040A: cross-layer rig surface.
//
// Every 040B seam spec imports from here, so no spec can quietly grow its own
// boot path (R2: "Seed/reset/auth/SSE helpers shared by all 040B specs, no
// per-spec bespoke boot").

export {
  API_BASE_URL,
  COMPOSE_FILE,
  FRONTEND_BASE_URL,
  PORTS,
  REQUIRED_RUNNING_SERVICES,
  REQUIRED_SERVICES,
  SEED,
  SEEDER_CONNECTION_STRING,
  SEEDER_PROJECT,
  RigPreflightError,
  assertRigPortsOpen,
  formatServiceMatrix,
  isPortListening,
} from './config.js';
export type { CrossLayerPorts } from './config.js';

export { ApiClient, ApiError } from './apiClient.js';
export type {
  ApiFailure,
  ProcessingRun,
  ProjectSummary,
  TokenPair,
  WorkspaceSnapshot,
} from './apiClient.js';

export { SeedError, buildSeeder, seedCrossLayerEnvironment } from './seed.js';
export type { SeedResult } from './seed.js';

export { SseClient, SseTimeoutError } from './sseClient.js';
export type { SseEvent } from './sseClient.js';

export {
  AI_MOCK_UNAVAILABLE,
  AiMockUnavailableError,
  EXPECTED_MOCK_CONFIDENCE,
  MOCK_AI_PROBE_TIMEOUT_MS,
  PROVIDER_UNAVAILABLE_CODES,
  assertMockAiDelivered,
  assertMockPipelineConsistent,
  classifyProviderFailure,
  expectedTranscriptText,
} from './mockAi.js';

export {
  ArtifactAssertionError,
  assertRunArtifacts,
  assertRunPersisted,
  countRows,
  readProjectRow,
} from './assertArtifacts.js';
export type { ProjectRow, RunArtifacts } from './assertArtifacts.js';
