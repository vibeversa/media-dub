/* auto-generated — do not edit; run make generate-api */

// Bundle: openapi v1. Do not hand-edit; regenerate with make generate-api.

/** Frozen Quota limits. */
export interface AdminQuotasResponse {
  readonly "correlationId": string;
  readonly "maxActiveProjects": number;
  readonly "maxConcurrentStagesPerTenant": number;
  readonly "maxCostPerProject": number;
  readonly "maxCostPerSegment": number;
  readonly "maxProjectsPerDay": number;
  readonly "maxSegmentCount": number;
  readonly "maxStorageBytes": number;
}

/** Tenant usage aggregate (counts and bytes only). */
export interface AdminUsageResponse {
  readonly "activeRuns": number;
  readonly "correlationId": string;
  readonly "monthCostUsd": number;
  readonly "pendingReviews": number;
  readonly "projectsTodayRemaining": number;
  readonly "storageQuotaBytes": number;
  readonly "storageUsedBytes": number;
  readonly "totalProjects": number;
}

export interface AuthLoginRequest {
  readonly "email": string;
  readonly "password": string;
  readonly "tenantSlug": string;
}

export interface AuthLoginResponse {
  readonly "accessToken": string;
  readonly "expiresInSeconds": number;
  readonly "refreshToken": string;
  readonly "tenantId": string;
  readonly "tokenType": string;
  readonly "userId": string;
}

export interface AuthRefreshRequest {
  readonly "refreshToken": string;
}

/** Compatible-only voices plus exclusion reasons. */
export interface AvailableVoicesResponse {
  readonly "excluded"?: ({
  readonly "reasons"?: (string)[];
  readonly "voiceId"?: string;
})[];
  readonly "excludedCount"?: number;
  readonly "voices"?: (Record<string, unknown>)[];
}

/** Seven-section summary: projectCounts, recentOutputs[<=5], storage, cost, quota, warnings[], backlog. */
export interface DashboardSummary {
  readonly "backlog": {
  readonly "pendingReviews"?: number;
  readonly "runningJobs"?: number;
};
  readonly "cost": {
  readonly "currency"?: string;
  readonly "monthToDate"?: number;
};
  readonly "projectCounts": {
  readonly "active"?: number;
  readonly "archived"?: number;
  readonly "total"?: number;
};
  readonly "quota": {
  readonly "remaining"?: number;
  readonly "resetsAt"?: string;
};
  readonly "recentOutputs": (Record<string, unknown>)[];
  readonly "storage": {
  readonly "quotaBytes"?: number;
  readonly "usedBytes"?: number;
};
  readonly "warnings": (Record<string, unknown>)[];
}

/** DLQ summary: depth, oldest-entry age, top-10 reason breakdown. */
export type DlqSummary = {
  readonly "correlationId"?: string;
  readonly "depth"?: number;
  readonly "oldestEnqueuedAt"?: string | null;
  readonly "oldestEntryAge"?: string | null;
  readonly "topReasons"?: ({
  readonly "code"?: string;
  readonly "count"?: number;
})[];
};

/** Frozen public error-code catalog (65 codes). Status mapping: VALIDATION_FAILED 400, UNAUTHORIZED/INVALID_CREDENTIALS/TOKEN_EXPIRED/TOKEN_REUSED 401, FORBIDDEN/CONSENT_REQUIRED/POLICY_DENIED/USER_DISABLED/VOICE_CONSENT_REQUIRED 403, NOT_FOUND/PROJECT_NOT_FOUND/VERSION_NOT_FOUND/VOICE_NOT_FOUND/ARTIFACT_UNAVAILABLE 404, MEDIA_UNSUPPORTED 415, MEDIA_CORRUPT/QC_BLOCKED/ARTIFACT_CHECKSUM_MISMATCH/VOICE_INCOMPATIBLE/IDEMPOTENCY_KEY_REUSED 422, PROVIDER_RATE_LIMITED/PROVIDER_QUOTA_EXHAUSTED/QUOTA_EXCEEDED/RATE_LIMITED 429, RESOURCE_EXHAUSTED/STORAGE_UNAVAILABLE 503, PROVIDER_TIMEOUT 504, PROVIDER_INVALID_RESPONSE/PROVIDER_FAILED 502, PROVIDER_CONFIGURATION_ERROR/PIPELINE_INVARIANT_VIOLATION/INTERNAL_ERROR 500, PREFERENCE_VALUE_TOO_LARGE 413, URL_EXPIRED 410, all other *_CONFLICT/*_ACTIVE/*_LOCKED/SELECTION_CONFLICT/LEASE_LOST/MANUAL_REVIEW_REQUIRED/EXPORT_NOT_READY/EXPORT_INCOMPLETE/OUTPUT_INCOMPLETE/SEGMENT_RETRY_ACTIVE/REVIEW_*_RESOLVED 409, UPLOAD_INCOMPLETE/LANGUAGE_IMMUTABLE/VERSION_SEGMENT_MISMATCH/SEGMENT_TEXT_EMPTY/PREVIEW_TEXT_INVALID/REVIEW_REASON_REQUIRED/REVIEW_EDIT_EMPTY/PREFERENCE_KEY_UNKNOWN/IDEMPOTENCY_KEY_REQUIRED/DUPLICATE_MEDIA 400-or-409 per catalog, TENANT_REQUIRED 401. */
export type ErrorCode = "VALIDATION_FAILED" | "UNAUTHORIZED" | "FORBIDDEN" | "NOT_FOUND" | "CONFLICT" | "MEDIA_UNSUPPORTED" | "MEDIA_CORRUPT" | "UPLOAD_INCOMPLETE" | "DUPLICATE_MEDIA" | "PROVIDER_CONFIGURATION_ERROR" | "PROVIDER_RATE_LIMITED" | "PROVIDER_TIMEOUT" | "PROVIDER_INVALID_RESPONSE" | "PROVIDER_QUOTA_EXHAUSTED" | "PROVIDER_FAILED" | "QC_BLOCKED" | "QUOTA_EXCEEDED" | "RATE_LIMITED" | "RESOURCE_EXHAUSTED" | "ARTIFACT_UNAVAILABLE" | "ARTIFACT_CHECKSUM_MISMATCH" | "LEASE_LOST" | "PIPELINE_INVARIANT_VIOLATION" | "MANUAL_REVIEW_REQUIRED" | "EXPORT_NOT_READY" | "STORAGE_UNAVAILABLE" | "CONSENT_REQUIRED" | "POLICY_DENIED" | "INTERNAL_ERROR" | "INVALID_CREDENTIALS" | "TOKEN_EXPIRED" | "TOKEN_REUSED" | "USER_DISABLED" | "PREFERENCE_KEY_UNKNOWN" | "PREFERENCE_VALUE_TOO_LARGE" | "TENANT_REQUIRED" | "LANGUAGE_IMMUTABLE" | "SETTINGS_LOCKED_ACTIVE_RUN" | "PROJECT_NOT_FOUND" | "PROJECT_HAS_ACTIVE_RUN" | "SETTINGS_VERSION_CONFLICT" | "IDEMPOTENCY_KEY_REQUIRED" | "IDEMPOTENCY_KEY_REUSED" | "PROJECT_ARCHIVED" | "RUN_ALREADY_TERMINAL" | "RUN_ALREADY_ACTIVE" | "CONFIG_CHANGED_SINCE_RUN" | "SELECTION_CONFLICT" | "VERSION_NOT_FOUND" | "VERSION_SEGMENT_MISMATCH" | "SEGMENT_TEXT_EMPTY" | "SEGMENT_RETRY_ACTIVE" | "VOICE_INCOMPATIBLE" | "VOICE_CONSENT_REQUIRED" | "PREVIEW_QUOTA_EXCEEDED" | "VOICE_NOT_FOUND" | "PREVIEW_TEXT_INVALID" | "REVIEW_VERSION_CONFLICT" | "REVIEW_REASON_REQUIRED" | "REVIEW_ALREADY_RESOLVED" | "REVIEW_NOT_RESOLVED" | "REVIEW_EDIT_EMPTY" | "EXPORT_INCOMPLETE" | "OUTPUT_INCOMPLETE" | "URL_EXPIRED";

/** Uniform error envelope: { error: { code, message, correlationId, details } }. details carries field errors only; 500s carry a generic message. */
export interface ErrorResponse {
  readonly "error": {
  readonly "code": ErrorCode;
  readonly "correlationId": string;
  readonly "details": Record<string, unknown>;
  readonly "message": string;
};
}

export type Export = {
  readonly "completenessJson"?: string | null;
  readonly "createdAt"?: string;
  readonly "format"?: ExportFormat;
  readonly "id"?: string;
  readonly "isPartial"?: boolean;
  readonly "projectId"?: string;
  readonly "status"?: string;
};

export interface ExportCreateRequest {
  readonly "allowPartial"?: boolean;
  readonly "format": ExportFormat;
  readonly "profile"?: string;
}

export type ExportFormat = "Srt" | "Vtt" | "Mp4" | "Wav" | "Mp3";

export interface MeResponse {
  readonly "permissions": (string)[];
  readonly "roles"?: (string)[];
  readonly "tenantId": string;
  readonly "userId": string;
}

/** Notification row: short summaries only, never bodies, URLs, or secrets. */
export type Notification = {
  readonly "body"?: string;
  readonly "createdAt"?: string;
  readonly "expiresAt"?: string | null;
  readonly "id"?: string;
  readonly "projectId"?: string | null;
  readonly "readAt"?: string | null;
  readonly "resourceId"?: string;
  readonly "resourceType"?: "ProcessingRun" | "ReviewItem" | "ExportJob" | "MediaAsset" | "DubbingProject" | "Tenant";
  readonly "severity"?: string;
  readonly "title"?: string;
  readonly "type"?: string;
};

export interface NotificationListResponse {
  readonly "hasMore": boolean;
  readonly "items": (Notification)[];
  readonly "page": number;
  readonly "pageSize": number;
  readonly "total": number;
}

/** Cursor page of orphan artifacts (ids, sizes, hashes, timestamps only). */
export type OrphanPage = {
  readonly "cursor"?: string | null;
  readonly "hasMore"?: boolean;
  readonly "items"?: (Record<string, unknown>)[];
};

/** Output aggregate with per-asset state+generationState aliases. */
export type OutputResponse = {
  readonly "completeness"?: {
  readonly "ready"?: number;
  readonly "total"?: number;
};
  readonly "errorCode"?: string | null;
  readonly "generationState"?: OutputState;
  readonly "items"?: Record<string, unknown>;
  readonly "progressApproximate"?: number | null;
  readonly "reason"?: string | null;
  readonly "state"?: OutputState;
  readonly "updatedAt"?: string;
  readonly "warnings"?: (Record<string, unknown>)[];
};

export type OutputState = "Ready" | "Generating" | "Failed" | "Partial" | "Unavailable";

/** Page-based list envelope: { items, page, pageSize, total, hasMore }. hasMore is page * pageSize < total. */
export interface PaginatedResult {
  readonly "hasMore": boolean;
  readonly "items": (Record<string, unknown>)[];
  readonly "page": number;
  readonly "pageSize": number;
  readonly "total": number;
}

/** Identity preference map (last-writer-wins per key). */
export type PreferencesResponse = Record<string, string>;

export type ProcessingRun = {
  readonly "configHash"?: string;
  readonly "createdAt"?: string;
  readonly "retryOfRunId"?: string | null;
  readonly "runId": string;
  readonly "status": string;
};

export interface ProcessingRunListResponse {
  readonly "hasMore": boolean;
  readonly "items": (ProcessingRun)[];
  readonly "page": number;
  readonly "pageSize": number;
  readonly "total": number;
}

export interface ProcessingStartRequest {
  readonly "configHash"?: string;
}

export type ProgressResponse = {
  readonly "completedUnits"?: number;
  readonly "currentStage"?: string | null;
  readonly "expectedUnits"?: number;
  readonly "failedUnits"?: number;
  readonly "percentApproximate": number;
  readonly "phase"?: string;
  readonly "projectId": string;
  readonly "runId"?: string | null;
  readonly "status": string;
};

export interface Project {
  readonly "configHash"?: string;
  readonly "createdAt"?: string;
  readonly "id": string;
  readonly "isArchived"?: boolean;
  readonly "name": string;
  readonly "settingsVersion": number;
  readonly "sourceLanguage"?: string;
  readonly "status": string;
  readonly "targetLanguage"?: string;
  readonly "updatedAt"?: string;
}

export interface ProjectCreateRequest {
  readonly "name"?: string;
  readonly "sourceLanguage": string;
  readonly "targetLanguage": string;
}

/** Project list envelope with sort metadata. */
export interface ProjectListResponse {
  readonly "clamped": boolean;
  readonly "hasMore": boolean;
  readonly "items": (Project)[];
  readonly "page": number;
  readonly "pageSize": number;
  readonly "sort": string;
  readonly "sortDir": "asc" | "desc";
  readonly "total": number;
}

export interface ProjectPatchRequest {
  readonly "name"?: string;
  readonly "processingSettings"?: Record<string, unknown>;
  readonly "settingsVersion"?: number;
}

/** Per-provider health snapshot (secret-free). */
export type ProviderHealth = {
  readonly "activeRoutes"?: (string)[];
  readonly "circuitBreakerState"?: "Open" | "Closed";
  readonly "correlationId"?: string;
  readonly "errorRate"?: number;
  readonly "lastSuccessAt"?: string | null;
  readonly "latencyMsP95"?: number | null;
  readonly "provider"?: string;
  readonly "status"?: "Unknown" | "Healthy" | "Degraded" | "Down";
};

/** Capability-to-provider route (names only). */
export interface ProviderRoute {
  readonly "capability"?: string;
  readonly "correlationId"?: string;
  readonly "enabled"?: boolean;
  readonly "priority"?: number;
  readonly "provider"?: string;
}

/** Pending-message depth for one queue (counts only). */
export interface QueueDepth {
  readonly "correlationId"?: string;
  readonly "depth"?: number;
  readonly "queue"?: string;
}

/** Review backlog aggregation (counts and ids only). */
export type ReviewBacklog = {
  readonly "bySeverity"?: Record<string, number>;
  readonly "byStatus"?: Record<string, number>;
  readonly "correlationId"?: string;
  readonly "oldestWaitingAt"?: string | null;
  readonly "perProject"?: (Record<string, unknown>)[];
  readonly "totalOpen"?: number;
};

/** Eleven sections: item, project, run, segment, versions, voice, audio, sync, qc, actions/permissions, history. */
export type ReviewContextResponse = {
  readonly "actions"?: (string)[];
  readonly "audio"?: Record<string, unknown> | null;
  readonly "history"?: (Record<string, unknown>)[];
  readonly "item"?: Record<string, unknown>;
  readonly "permissions"?: Record<string, unknown>;
  readonly "project"?: Project;
  readonly "qc"?: Record<string, unknown>;
  readonly "run"?: Record<string, unknown> | null;
  readonly "segment"?: Record<string, unknown> | null;
  readonly "sync"?: Record<string, unknown> | null;
  readonly "truncated"?: boolean;
  readonly "versions"?: Record<string, unknown>;
  readonly "voice"?: Record<string, unknown> | null;
};

export interface ReviewMutationRequest {
  readonly "editText"?: string;
  readonly "expectedVersion": number;
  readonly "reason": string;
}

export type ReviewMutationResponse = {
  readonly "manualVersionId"?: string | null;
  readonly "reviewId"?: string;
  readonly "status"?: ReviewStatus;
  readonly "version"?: number;
  readonly "versionKind"?: string | null;
};

export type ReviewStatus = "Open" | "Approved" | "Rejected";

/** Segment detail: id, selectionVersion, transcript/translation versions, review status, quality codes, sync status, outputStale. */
export interface SegmentDetailResponse {
  readonly "id"?: string;
  readonly "outputStale"?: boolean;
  readonly "qualityCodes"?: (string)[];
  readonly "reviewStatus"?: ReviewStatus;
  readonly "selectionVersion"?: number;
  readonly "transcriptVersions"?: (Record<string, unknown>)[];
  readonly "translationVersions"?: (Record<string, unknown>)[];
}

export interface SegmentEditRequest {
  readonly "expectedVersion": number;
  readonly "text": string;
}

export interface SegmentListResponse {
  readonly "hasMore": boolean;
  readonly "items": (Record<string, unknown>)[];
  readonly "page": number;
  readonly "pageSize": number;
  readonly "total": number;
}

export type SegmentMutationResponse = {
  readonly "newVersionId"?: string | null;
  readonly "outputStale"?: boolean;
  readonly "segmentId"?: string;
  readonly "selectedVersionIds"?: (string)[];
  readonly "selectionVersion"?: number;
  readonly "warningCode"?: string | null;
};

export interface SegmentSelectionRequest {
  readonly "expectedVersion": number;
  readonly "selectedVersionIds": (string)[];
}

export type SpeakerDetailResponse = {
  readonly "assignedVoice"?: Record<string, unknown> | null;
  readonly "displayName"?: string | null;
  readonly "id"?: string;
  readonly "segmentCount"?: number;
  readonly "speakerKey"?: string;
};

export interface SpeakerListResponse {
  readonly "hasMore": boolean;
  readonly "items": (Record<string, unknown>)[];
  readonly "page": number;
  readonly "pageSize": number;
  readonly "total": number;
}

/** Frozen SSE envelope: { eventId, schemaVersion:1, eventType, tenantId, projectId?, processingRunId?, occurredAt, payload, correlationId }. Payload allowlist: IDs, statuses, percents, counts, codes, timestamps only. */
export type SseEnvelope = {
  readonly "correlationId": string;
  readonly "eventId": string;
  readonly "eventType": SseEventType;
  readonly "occurredAt": string;
  readonly "payload": Record<string, unknown>;
  readonly "processingRunId"?: string | null;
  readonly "projectId"?: string | null;
  readonly "schemaVersion": 1;
  readonly "tenantId": string;
};

/** Frozen SSE event-type set (exactly these 14; additions require updating the Task 013 contract first). */
export type SseEventType = "project.status_changed" | "run.status_changed" | "stage.started" | "stage.progress" | "stage.completed" | "stage.failed" | "stage.review_required" | "review.created" | "review.resolved" | "export.created" | "export.completed" | "export.failed" | "notification.created" | "output.ready";

export interface UnreadCountResponse {
  readonly "unreadCount"?: number;
}

export interface UploadInitiateRequest {
  readonly "contentType": string;
  readonly "fileName": string;
  readonly "partCount": number;
  readonly "sizeBytes": number;
}

export interface UploadPartAuth {
  readonly "expiresAt"?: string;
  readonly "partNumber"?: number;
  readonly "url"?: string;
}

export interface UploadSession {
  readonly "id": string;
  readonly "partUrls"?: (string)[];
  readonly "receivedParts"?: (number)[];
  readonly "status": string;
}

export type VoiceAssignmentRequest = {
  readonly "reason"?: string | null;
  readonly "voiceId": string;
};

export type VoiceAssignmentResponse = {
  readonly "changed"?: boolean;
  readonly "outputStale"?: boolean;
  readonly "speakerId"?: string;
  readonly "voiceId"?: string;
  readonly "voiceProfileId"?: string;
  readonly "warningCode"?: string | null;
};

export type VoicePreviewDetailResponse = {
  readonly "artifactId"?: string | null;
  readonly "downloadUrl"?: string | null;
  readonly "previewId"?: string;
  readonly "status"?: VoicePreviewStatus;
};

export interface VoicePreviewRequest {
  readonly "text": string;
  readonly "voiceId": string;
}

export interface VoicePreviewResponse {
  readonly "isDuplicate"?: boolean;
  readonly "previewId"?: string;
  readonly "status"?: VoicePreviewStatus;
}

export type VoicePreviewStatus = "Queued" | "Running" | "Completed" | "Failed" | "Cancelled";

/** Workspace aggregate: project, media, run, phase, stage, progress, review, warnings, output, cost, activity, permissions. */
export interface Workspace {
  readonly "permissions"?: (string)[];
  readonly "progress"?: ProgressResponse;
  readonly "project"?: Project;
  readonly "warnings"?: (Record<string, unknown>)[];
}

export interface AuthLogoutResponse {
  readonly "loggedOut"?: boolean;
}

export interface AuthorizeUploadPartRequest {
  readonly "partNumber": number;
}

export interface DownloadOutputResponse {
  readonly "downloadUrl"?: string;
  readonly "expiresAt"?: string;
}

export interface GetAdminDlqSummaryResponse {
  readonly "guidance"?: string;
  readonly "metrics"?: (string)[];
  readonly "queues"?: (string)[];
  readonly "slo"?: string;
}

export interface GetAdminProviderExecutionResponse {
  readonly "capability"?: string;
  readonly "id"?: string;
  readonly "latencyMs"?: number;
  readonly "outcome"?: string;
  readonly "provider"?: string;
}

export interface GetAdminStageResponse {
  readonly "attempt"?: number;
  readonly "id"?: string;
  readonly "leaseExpiresAt"?: string;
  readonly "leaseOwner"?: string;
  readonly "stageType"?: string;
  readonly "status"?: string;
}

export interface GetAdminStatusResponse {
  readonly "status"?: string;
  readonly "time"?: string;
}

export interface GetWorkspaceQualityResponse {
  readonly "blockedCount"?: number;
  readonly "codes"?: (string)[];
  readonly "failedCount"?: number;
}

export interface MarkAllNotificationsReadResponse {
  readonly "marked"?: number;
  readonly "markedCount"?: number;
}

export interface MarkNotificationReadResponse {
  readonly "marked"?: boolean;
}
