// Smoke import for the generated API client (Task 014).
// Task 017 wires query keys and error normalization; this file only proves
// the generated output compiles under strict TS and exposes the frozen
// contracts (DTOs, 14 SSE types, error codes, pagination/error envelopes).
import {
  ApiClient,
  ApiError,
  type AdminQuotasResponse,
  type AdminUsageResponse,
  type ErrorCode,
  type ErrorResponse,
  type ListProjectsParams,
  type OutputResponse,
  type Project,
  type ProjectListResponse,
  type ReviewMutationRequest,
  type SegmentSelectionRequest,
  type SseEnvelope,
  type SseEventType,
} from './generated/index.js';

const SSE_TYPES: SseEventType[] = [
  'project.status_changed',
  'run.status_changed',
  'stage.started',
  'stage.progress',
  'stage.completed',
  'stage.failed',
  'stage.review_required',
  'review.created',
  'review.resolved',
  'export.created',
  'export.completed',
  'export.failed',
  'notification.created',
  'output.ready',
];

const ERROR_CODES: ErrorCode[] = [
  'VALIDATION_FAILED',
  'SELECTION_CONFLICT',
  'REVIEW_VERSION_CONFLICT',
  'EXPORT_INCOMPLETE',
  'URL_EXPIRED',
];

export function createClient(baseUrl: string, getToken: () => string | undefined): ApiClient {
  return new ApiClient({ baseUrl, getToken });
}

export async function loadProjectPage(client: ApiClient, page: number): Promise<ProjectListResponse> {
  const params: ListProjectsParams = { path: {}, query: { page, pageSize: 20 } };
  const response: ProjectListResponse = await client.listProjects(params);
  const first: Project | undefined = response.items[0];
  void first;
  return response;
}

export function describeEnvelope(envelope: SseEnvelope): string {
  return `${envelope.eventType}:${envelope.schemaVersion}:${envelope.correlationId}`;
}

export function selectionBody(expectedVersion: number): SegmentSelectionRequest {
  return { expectedVersion, selectedVersionIds: ['ver_01JABCDEF'] };
}

export function mutationBody(expectedVersion: number): ReviewMutationRequest {
  return { expectedVersion, reason: 'Verified against picture.' };
}

export function isApiError(error: unknown): error is ApiError {
  return error instanceof ApiError;
}

export function errorCodeOf(response: ErrorResponse): ErrorCode {
  return response.error.code;
}

export function usageSummary(usage: AdminUsageResponse, quotas: AdminQuotasResponse): string {
  return `${usage.activeRuns}/${quotas.maxActiveProjects}`;
}

export function outputStateOf(output: OutputResponse): string {
  return `${output.state}/${output.generationState}`;
}

void SSE_TYPES;
void ERROR_CODES;
