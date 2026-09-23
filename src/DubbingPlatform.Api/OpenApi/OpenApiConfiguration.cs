using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace DubbingPlatform.Api.OpenApi;

/// <summary>
/// Enriches the <c>v1</c> OpenAPI document with the platform conventions:
/// JWT bearer security scheme, global <c>Idempotency-Key</c> header on mutations,
/// and document metadata. Error-envelope (<c>ErrorResponse</c>) and pagination
/// (<c>PaginatedResult</c>) schemas are emitted from controller
/// <c>ProducesResponseType</c> references; this transformer only adds the
/// cross-cutting security and header contracts. Auth
/// (<c>/api/v1/auth/*</c>) and identity (<c>/api/v1/me*</c>) routes are
/// excluded from <c>Idempotency-Key</c>: login/refresh mint fresh secrets per
/// call by design (replay must not return the same secret), and preference
/// PUT is naturally idempotent with last-writer-wins per key.
/// </summary>
public static class OpenApiConfiguration
{
    /// <summary>
    /// Registers the <c>v1</c> document with the enricher.
    /// </summary>
    public static void AddDubbingOpenApi(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOpenApi("v1", options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "Dubbing Platform API";
                document.Info.Version = "v1";
                document.Info.Description = "Tenant-scoped dubbing pipeline. Auth: JWT bearer (tid/sub/roles) or ?access_token= on SSE /stream (same policy, never logged). Mutations accept Idempotency-Key. Errors use { error: { code, message, correlationId, details } } with codes including LANGUAGE_IMMUTABLE, SETTINGS_LOCKED_ACTIVE_RUN, PROJECT_NOT_FOUND, PROJECT_HAS_ACTIVE_RUN, SETTINGS_VERSION_CONFLICT, IDEMPOTENCY_KEY_REQUIRED, IDEMPOTENCY_KEY_REUSED, PROJECT_ARCHIVED, RUN_ALREADY_TERMINAL, RUN_ALREADY_ACTIVE, CONFIG_CHANGED_SINCE_RUN, SELECTION_CONFLICT, VERSION_NOT_FOUND, VERSION_SEGMENT_MISMATCH, SEGMENT_TEXT_EMPTY, SEGMENT_RETRY_ACTIVE, VOICE_INCOMPATIBLE, VOICE_CONSENT_REQUIRED, PREVIEW_QUOTA_EXCEEDED, VOICE_NOT_FOUND, PREVIEW_TEXT_INVALID, REVIEW_VERSION_CONFLICT, REVIEW_REASON_REQUIRED, REVIEW_ALREADY_RESOLVED, REVIEW_NOT_RESOLVED, REVIEW_EDIT_EMPTY, EXPORT_INCOMPLETE, OUTPUT_INCOMPLETE, URL_EXPIRED. Project lists use { items, page, pageSize, total, sort, sortDir, hasMore, clamped }; dashboard summary is GET /api/v1/dashboard/summary with seven sections; processing start returns 202 (replay 200 + Idempotent-Replayed:true) and run lists use { items, page, pageSize, total, hasMore }; workspace aggregate is GET /api/v1/projects/{projectId}/workspace (single call, ten sections); progress exposes percentApproximate (display-only) and SSE is GET .../progress/stream (text/event-stream, Last-Event-ID accepted, event freeze in Task 013); segments support GET .../segments (filters speakerId/reviewStatus/qualityFlag/syncIssue/text/startMs/endMs, default 50/max 200, sort startMs) plus detail with versions and POST .../segments/{id}/retry|transcript-selection|translation-selection|transcript-edits|translation-edits (stale → 409 SELECTION_CONFLICT with { currentSelectionVersion, currentVersionIds }); speakers support GET .../speakers (segmentCount + assignedVoice) plus detail, GET .../speakers/{id}/available-voices (compatible-only + excludedCount/reasons), PUT .../speakers/{id}/voice-assignment { voiceId, reason? } (incompatible → 422 VOICE_INCOMPATIBLE, no consent → 403 VOICE_CONSENT_REQUIRED, same → 200 changed:false), and POST|GET .../voice-previews (202 first, 200 duplicate, quota → 429 PREVIEW_QUOTA_EXCEEDED, text → 400 PREVIEW_TEXT_INVALID); reviews support GET .../reviews/{id}/context (eleven sections: item, project, run, segment, versions, voice, audio, sync, qc, actions/permissions, history) plus POST .../reviews/{id}/resolve|dismiss|reopen|resolve-with-edit { expectedVersion, reason, editText? } + Idempotency-Key (stale → 409 REVIEW_VERSION_CONFLICT with { currentVersion }, missing reason → 400 REVIEW_REASON_REQUIRED, replay → 200 + Idempotent-Replayed:true, resolve-with-edit creates a manual version); output is GET .../output { state Ready|Generating|Failed|Partial|Unavailable, generationState (alias), completeness{ready,total}, items{video,audio,subtitles[],transcript,translation,timeline,speakers,qc} each with state+generationState, warnings[], updatedAt } (Unavailable + NO_RUNS_YET when no runs; signed URLs ≤15min only, never storage keys) plus GET .../output/download; exports support POST .../exports { format, profile?, allowPartial? } + Idempotency-Key 7d (202, replay single row; incomplete without allowPartial → 409 EXPORT_INCOMPLETE/OUTPUT_INCOMPLETE with partial offer) plus GET .../exports[/{id}] and GET .../exports/{id}/download (302 to ≤15min signed URL; not ready → 409 EXPORT_NOT_READY, expired → 410 URL_EXPIRED); notifications support GET /api/v1/notifications (?unreadOnly, newest-first page-based, expired excluded) + GET .../unread-count + POST .../{id}/read (idempotent) + POST .../read-all { markedCount, marked } (Task 013 emits notification.created SSE as a hint only, this API is source of truth; deep links resourceType/resourceId/projectId route 034 with fallback to projectId then list).";

                document.Components ??= new OpenApiComponents();
                if (document.Components.SecuritySchemes is null)
                {
                    document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
                }

                if (!document.Components.SecuritySchemes.ContainsKey("Bearer"))
                {
                    document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.Http,
                        Scheme = "bearer",
                        BearerFormat = "JWT",
                        Description = "JWT bearer with tid (tenant), sub (user), roles[] claims.",
                    };
                }

                if (document.Security is null)
                {
                    document.Security = new List<OpenApiSecurityRequirement>();
                }

                if (document.Security.Count == 0)
                {
                    var requirement = new OpenApiSecurityRequirement();
                    var reference = new OpenApiSecuritySchemeReference("Bearer", document);
                    requirement.Add(reference, []);
                    document.Security.Add(requirement);
                }

                if (document.Paths is not null)
                {
                    foreach (var pathEntry in document.Paths)
                    {
                        var path = pathEntry.Value;
                        if (path?.Operations is null)
                        {
                            continue;
                        }

                        if (IsIdempotencyExempt(pathEntry.Key))
                        {
                            continue;
                        }

                        foreach (var entry in path.Operations)
                        {
                            var method = entry.Key.ToString();
                            if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            var operation = entry.Value;
                            if (operation is null)
                            {
                                continue;
                            }

                            if (operation.Parameters is null)
                            {
                                operation.Parameters = new List<IOpenApiParameter>();
                            }

                            var hasKey = false;
                            foreach (var parameter in operation.Parameters)
                            {
                                if (parameter is not null
                                    && string.Equals(parameter.Name, "Idempotency-Key", StringComparison.OrdinalIgnoreCase))
                                {
                                    hasKey = true;
                                    break;
                                }
                            }

                            if (!hasKey)
                            {
                                operation.Parameters.Add(new OpenApiParameter
                                {
                                    Name = "Idempotency-Key",
                                    In = ParameterLocation.Header,
                                    Required = false,
                                    Description = "Idempotency key for safe retries. Same key+body replays; same key+different body returns 409 CONFLICT.",
                                    Schema = new OpenApiSchema
                                    {
                                        Type = JsonSchemaType.String,
                                    },
                                });
                            }
                        }
                    }
                }

                EnsureSchema(document, "ErrorResponse");
                EnsureSchema(document, "SseEnvelope");
                EnsureSchema(document, "AdminUsageResponse");
                EnsureSchema(document, "AdminQuotasResponse");
                EnsureSchema(document, "PaginatedResult");
                EnsureSchema(document, "ProjectListResponse");
                EnsureSchema(document, "DashboardSummaryResponse");
                EnsureSchema(document, "ProcessingRunListResponse");
                EnsureSchema(document, "WorkspaceDto");
                EnsureSchema(document, "ProgressResponse");
                EnsureSchema(document, "SegmentDetailResponse");
                EnsureSchema(document, "SegmentSummaryResponse");
                EnsureSchema(document, "SegmentMutationResponse");
                EnsureSchema(document, "SpeakerSummaryResponse");
                EnsureSchema(document, "SpeakerDetailResponse");
                EnsureSchema(document, "AvailableVoicesResponse");
                EnsureSchema(document, "VoiceAssignmentResponse");
                EnsureSchema(document, "VoicePreviewResponse");
                EnsureSchema(document, "VoicePreviewDetailResponse");
                EnsureSchema(document, "ReviewContextResponse");
                EnsureSchema(document, "ReviewMutationRequest");
                EnsureSchema(document, "ReviewMutationResponse");
                EnsureSchema(document, "OutputResponse");
                EnsureSchema(document, "ExportResponse");
                EnsureSchema(document, "NotificationResponse");

                return Task.CompletedTask;
            });
        });
    }

    /// <summary>
    /// Auth and identity routes never take Idempotency-Key (see class doc).
    /// </summary>
    private static bool IsIdempotencyExempt(string path)
    {
        return path.StartsWith("/api/v1/auth", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/v1/me", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureSchema(OpenApiDocument document, string name)
    {
        if (document.Components is null)
        {
            document.Components = new OpenApiComponents();
        }

        if (document.Components.Schemas is null)
        {
            document.Components.Schemas = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
        }

        if (!document.Components.Schemas.ContainsKey(name))
        {
            var description = name switch
            {
                "ErrorResponse" => "Structured error envelope: { error: { code, message, correlationId, details } } (Task 013 frozen table: validation 400, auth 401 TOKEN_EXPIRED/TOKEN_REUSED/INVALID_CREDENTIALS, forbidden 403, not-found 404 incl. cross-tenant, conflict 409 SELECTION_CONFLICT/REVIEW_VERSION_CONFLICT/SETTINGS_LOCKED_ACTIVE_RUN/RUN_ALREADY_ACTIVE/PREVIEW_STATE_CONFLICT, quota 429, provider 502/504 inc. PREVIEW_PROVIDER_TIMEOUT, unknown 500 INTERNAL_ERROR generic; ADMIN_ROUTE_UNKNOWN/DIAGNOSTICS_FORBIDDEN ride as markers on NOT_FOUND/FORBIDDEN).",
                "ProjectListResponse" => "Project list envelope: { items, page, pageSize, total, sort, sortDir, hasMore, clamped }.",
                "DashboardSummaryResponse" => "Dashboard summary: { projectCounts, recentOutputs[<=5], storage, cost, quota, warnings[], backlog }.",
                "ProcessingRunListResponse" => "Processing run list: { items[{ runId, status, retryOfRunId? }], page, pageSize, total, hasMore } (202 start, 200 replay + Idempotent-Replayed:true).",
                "WorkspaceDto" => "Workspace aggregate: { project, media, run, phase, stage, progress{percentApproximate}, review, warnings[], output, cost, activity[<=10], permissions }.",
                "ProgressResponse" => "Progress: { percentApproximate (display-only), currentStage, ... } plus frozen SSE text/event-stream (Task 013 SseEnvelope, stage.progress hints, Last-Event-ID resume with last-100 headers-only replay and replayTruncated:true beyond window).",
                "SegmentDetailResponse" => "Segment detail: { id, selectionVersion, transcriptVersions[], translationVersions[], reviewStatus?, qualityCodes[], syncStatus?, outputStale }.",
                "SegmentSummaryResponse" => "Segment list row: { id, startMs, selectionVersion, reviewStatus?, qualityCodes[], syncStatus? } (filters speakerId/reviewStatus/qualityFlag/syncIssue/text/startMs/endMs, default 50/max 200).",
                "SegmentMutationResponse" => "Segment mutation: { segmentId, selectionVersion, selectedVersionIds?, newVersionId?, outputStale, warningCode? } (stale → 409 SELECTION_CONFLICT with { currentSelectionVersion, currentVersionIds }).",
                "SpeakerSummaryResponse" => "Speaker list row: { id, speakerKey, segmentCount, assignedVoice? }.",
                "SpeakerDetailResponse" => "Speaker detail: { id, speakerKey, displayName, firstAppearanceMs, lastAppearanceMs, segmentCount, assignedVoice? }.",
                "AvailableVoicesResponse" => "Compatible-only voices: { voices[], excludedCount, excluded[{ voiceId, reasons }] } (excluded never selectable; direct assign → 422 VOICE_INCOMPATIBLE).",
                "VoiceAssignmentResponse" => "Voice assignment: { speakerId, voiceProfileId, voiceId, changed, outputStale, warningCode?, unusedSpeaker } (same → 200 changed:false; no consent → 403 VOICE_CONSENT_REQUIRED).",
                "VoicePreviewResponse" => "Voice preview create: { previewId (vpv_), status, isDuplicate } (202 first, 200 duplicate; quota → 429 PREVIEW_QUOTA_EXCEEDED).",
                "VoicePreviewDetailResponse" => "Voice preview detail: { previewId, status, artifactId?, downloadUrl? (15-min presigned, never internal path) }.",
                "ReviewContextResponse" => "Review context: { item{id,type,severity,status,version}, project, run{id,status,configHash}, segment?, versions{transcript[],translation[],selectedIds,selectionVersion,truncated}, voice{speakerId,voiceId,consentState}, audio{previewArtifactId?,signedUrl:null}, sync{offsetMs,driftFlag}, qc{issues[],evidenceArtifactIds[]}, actions{allowed[]}, permissions{canResolve,canEdit}, history[], truncated }.",
                "ReviewMutationRequest" => "Review mutation: { expectedVersion, reason (required, max 500), editText? (resolve-with-edit only) } + Idempotency-Key (replay → 200 + Idempotent-Replayed:true).",
                "ReviewMutationResponse" => "Review mutation result: { reviewId, status, version, manualVersionId?, versionKind? } (stale → 409 REVIEW_VERSION_CONFLICT with { currentVersion }).",
                "OutputResponse" => "Output aggregate: { state Ready|Generating|Failed|Partial|Unavailable, generationState (alias of state, Task 012A R1), reason? (NO_RUNS_YET), completeness{ready,total}, progressApproximate? (Generating), errorCode? (Failed), items{video?,audio?,subtitles[],transcript?,translation?,timeline?,speakers?,qc{summary,issuesUrl?}} each with state+generationState, warnings[], updatedAt } (signed URLs ≤15min only, never storage keys).",
                "ExportResponse" => "Export job: { id (exp_), projectId, format, status, isPartial, createdAt, completenessJson? } (idempotency 7d; incomplete without allowPartial → 409 EXPORT_INCOMPLETE/OUTPUT_INCOMPLETE; download 302 to ≤15min signed URL, 409 EXPORT_NOT_READY, 410 URL_EXPIRED).",
                "NotificationResponse" => "Notification row: { id (ntf_), type, severity, title, body (summary only), resourceType (ProcessingRun|ReviewItem|ExportJob|MediaAsset|DubbingProject|Tenant), resourceId, projectId? (prj_, null for tenant quota/policy), readAt?, createdAt, expiresAt? } (newest-first page-based, expired excluded; read/read-all idempotent; deep-link fallback: linked row → projectId → list; notification.created SSE (013) is hint only).",
                "SseEnvelope" => "Frozen SSE envelope (Task 013): { eventId, schemaVersion:1, eventType (14: project.status_changed|run.status_changed|stage.started|stage.progress|stage.completed|stage.failed|stage.review_required|review.created|review.resolved|export.created|export.completed|export.failed|notification.created|output.ready), tenantId, projectId?, processingRunId?, occurredAt, payload, correlationId } (hint only; allowlist IDs/statuses/percents/counts/codes/timestamps, never secrets/tokens/URLs/paths/raw payloads/lease data/bodies; 64KB drops counted; Last-Event-ID last-100 headers-only replay, replayTruncated:true beyond window).",
                "AdminUsageResponse" => "Admin usage: { correlationId, storageUsedBytes, storageQuotaBytes, monthCostUsd, projectsTodayRemaining, activeRuns, pendingReviews, totalProjects } (elevated admin.manage|diagnostics.view).",
                "AdminQuotasResponse" => "Admin quotas: frozen Quota limits { maxActiveProjects, maxProjectsPerDay, maxCostPerProject, maxCostPerSegment, maxSegmentCount, maxStorageBytes, maxConcurrentStagesPerTenant } (elevated admin.manage|diagnostics.view).",
                _ => "Pagination envelope: { items, page, pageSize, total, hasMore }.",
            };
            document.Components.Schemas[name] = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Description = description,
            };
        }
    }
}
