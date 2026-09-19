# Implementation Plan B — Optimized  
## Frontend, Product Workflows, and Platform Completion

This document is the optimized replacement for `implementation_plan-B.md`.

It completes the platform implementation plan for all product-facing, frontend, UX, notification, read-model, and remaining operational capabilities required on top of the already implemented backend/media platform defined by `implementation_plan-A.md`.

`PLAN A` is already implemented and remains architecturally authoritative for:

- modular monolith API
- worker separation by workload class
- PostgreSQL as system of record
- RabbitMQ / MassTransit durable orchestration
- Redis only for cache, throttling, rate limiting, and ephemeral coordination
- scoped DAG execution
- `ProcessingRun` as authoritative execution lifecycle
- stage execution leasing and fencing
- immutable artifacts and content-addressed storage
- provider capability routing
- tenant isolation
- manual review as a first-class workflow
- exports, retention, deletion, observability, security, and deployment model

This plan does **not** redesign the backend.

It adds, clarifies, and minimally extends the implemented platform so that the full product can be delivered end-to-end:

```text
Login / Tenant Context
  ↓
Project Creation
  ↓
Resumable Upload
  ↓
Media Validation
  ↓
Explicit Processing Start
  ↓
Live Progress
  ↓
Transcript / Translation / Voice Review
  ↓
Manual Review Resolution
  ↓
Quality Control
  ↓
Final Output
  ↓
Exports / Downloads
  ↓
Notifications / Activity / Administration
```

---

## 1. Purpose and Scope

This plan covers the remaining implementation surface required to make the platform a complete production product:

- product UX architecture
- design system
- frontend application architecture
- authentication and session UX
- dashboard and project list
- project creation
- resumable upload UX
- project workspace
- live progress
- transcript workspace
- translation workspace
- voice assignment workspace
- media player and timeline workspace
- manual review studio
- quality control interface
- output and export experience
- notifications
- activity and audit UX
- cost, quota, and usage UX
- admin and operator frontend
- frontend security
- frontend observability
- frontend testing
- frontend deployment
- required backend read models
- required backend extensions for editing, preferences, notifications, activity, voice preview, and media previews
- cross-system contract management
- final verification and completion criteria

This is a production-grade plan.

It is not an MVP reduction.

---

## 2. Relationship to Implemented PLAN A

### 2.1 Authority

`PLAN A` remains authoritative for all backend architecture decisions.

Where this document introduces product-facing capabilities, it does so by adding the smallest required backend extensions.

Where a contradiction exists between frontend convenience and backend correctness, backend correctness wins.

### 2.2 Amendment Model

This document amends `PLAN A` only where required.

Amendments are limited to:

1. new read models required by the frontend
2. new durable product entities required for notifications, activity, user preferences, and product metadata
3. explicit version-aware editing APIs
4. explicit voice assignment and voice preview APIs
5. explicit media preview artifacts for waveform and UI playback performance
6. SSE progress stream contract
7. authentication/session endpoints required by the web application
8. admin/diagnostics read endpoints required by operator UI

No orchestration model, storage model, tenant model, provider model, or artifact model is replaced.

### 2.3 Compatibility Rule

All backend extensions must be delivered through expand/contract migrations and must remain compatible with already deployed API and worker versions during rollout.

---

## 3. Canonical Terminology

Use these terms consistently across backend, frontend, tests, documentation, and UI copy.

| Canonical Term | Meaning |
|---|---|
| `Tenant` | Isolated customer workspace |
| `TenantUser` | User identity bound to a tenant |
| `ProjectStatus` | Product-level projection of the active run and review state |
| `ProcessingRun` | Authoritative pipeline execution lifecycle |
| `StageExecution` | Durable unit of work execution |
| `SpeechSegment` | Canonical segment produced by segmentation |
| `TranscriptVersion` | Immutable transcript content version |
| `TranslationVersion` | Immutable translation content version |
| `VoiceProfile` | Voice definition used for generation |
| `SpeakerVoiceAssignment` | Speaker-to-voice mapping |
| `GeneratedAudioArtifact` | TTS output artifact |
| `SyncResult` | Timing optimization result |
| `QualityResult` | QC finding entry |
| `ReviewItem` | Manual review work item |
| `OutputAsset` | Final render output |
| `Artifact` | Logical immutable artifact record |
| `ContentObject` | Immutable content-addressed bytes |
| `ProviderExecution` | Trace record for provider invocation |
| `Notification` | Durable in-app user notification |
| `ActivityEvent` | Durable user-visible activity projection |
| `UserPreference` | Stored user UI/product preference |

The frontend must not introduce alternate authoritative names for these concepts.

---

## 4. Binding Architectural Principles

These principles are binding for all remaining implementation work.

### 4.1 Backend Authority

The backend owns truth for:

- project lifecycle
- processing lifecycle
- stage state
- review state
- artifact state
- output readiness
- export readiness
- cost and quota state
- authorization
- notification persistence
- activity history

The frontend never owns workflow state.

### 4.2 Frontend Role

The frontend is a presentation, interaction, and read-model consumer layer.

It must not become:

- a second orchestration engine
- a pipeline state database
- a provider routing engine
- a cost authority
- a media processing engine
- a security boundary

### 4.3 Server State vs Client State

Server state must be handled through TanStack Query or equivalent server-state tooling.

Client state may be used only for:

- modal state
- sidebar state
- editor drafts
- player position
- timeline viewport
- temporary filters
- upload UI retry state

Critical domain state must never be stored as client-authoritative state.

### 4.4 Progressive Disclosure

Ordinary users should see:

- current status
- blockers
- required actions
- output readiness

Advanced users may access:

- stage diagnostics
- provider execution details
- attempt history
- artifact references
- QC evidence
- cost details
- correlation IDs

Raw infrastructure concepts must not be exposed by default.

### 4.5 No Fake Progress

Progress must be derived from backend state.

The UI must not show estimated completion times unless a real backend model exists.

A percentage indicator is allowed only as an approximate completion signal, not as a guaranteed ETA.

### 4.6 Versioned Editing

Editing transcript or translation content must create a new manual version or use the approved review-resolution flow.

The UI must never silently overwrite immutable pipeline artifacts.

### 4.7 Explicit Async UX

Every long-running operation must expose explicit UX states:

- pending
- running
- success
- failure
- retryable failure
- review required
- cancelled
- expired / stale

### 4.8 Least Exposure

The frontend must not expose:

- provider secrets
- storage internals
- queue names
- worker names
- lease tokens
- internal exception traces
- raw provider payloads

except in authorized advanced diagnostics areas.

### 4.9 Tenant Isolation Remains End-to-End

Tenant isolation must be enforced at:

- API
- repository
- message consumers
- storage keys
- signed URLs
- Redis keys
- frontend query scoping
- telemetry tags where present

Frontend tenant filtering is UX only.

---

## 5. Assumptions

The following assumptions are binding unless changed by explicit architecture decision.

### 5.1 Implemented Backend

The following are already implemented per `PLAN A`:

- .NET 10 modular monolith API
- worker services by workload class
- PostgreSQL 16 schema and migrations
- RabbitMQ + MassTransit orchestration
- EF Core transactional outbox/inbox
- Redis for ephemeral coordination
- S3-compatible object storage
- artifact and content object model
- provider capability routing
- cost, quota, and rate limiting foundation
- review, export, audit, retention, deletion capabilities
- Kubernetes deployment model

### 5.2 Frontend Baseline

The frontend uses:

- React
- TypeScript
- Vite
- React Router
- TanStack Query
- Zustand or equivalent lightweight client-state store
- Tailwind CSS or equivalent token-based styling
- OpenAPI-generated API client
- Playwright for E2E
- Vitest for unit/component testing
- MSW or equivalent for API mocking
- Storybook or equivalent for component development

### 5.3 Authentication Baseline

Authentication is provided by:

- an OIDC-compatible identity provider, or
- an equivalent platform-issued JWT/session mechanism

The API continues to validate JWT bearer principals.

The web application may use secure browser session mechanisms as long as backend authorization remains JWT/principal-based internally.

### 5.4 Storage and Media

Object storage supports:

- presigned URLs
- range requests
- multipart upload
- CORS restrictions
- lifecycle management

Media preview artifacts may be generated server-side.

### 5.5 Real-Time Transport

Server-to-browser live updates use SSE by default.

WebSocket is not required unless a future bidirectional use case appears.

---

## 6. Architecture Decisions

These decisions are binding.

### 6.1 No Separate BFF Microservice

The API remains the only backend consumer surface for the frontend.

Required frontend ergonomics are implemented as API read models, not as a separate frontend backend.

### 6.2 No Frontend Workflow Engine

The frontend does not compute pipeline progression, stage completion, or final output readiness.

It only renders backend projections.

### 6.3 SSE Is the Default Real-Time Mechanism

Progress and notification hints are delivered through SSE.

The frontend treats events as invalidation hints, not as authoritative replacements for server queries.

If SSE is unavailable, adaptive polling is used.

### 6.4 Read Models Are Added Where Needed

The API adds a small number of read-oriented endpoints to prevent N+1 frontend calls.

These endpoints are projections over the authoritative backend state.

### 6.5 Editing Uses Version-Aware Mutations

All user-facing content edits must be version-aware and conflict-safe.

No silent lost updates are allowed.

### 6.6 Media UI Uses Preview Artifacts

Waveforms, scrubbing, and timeline rendering must use lightweight preview artifacts where available.

The UI must not download archival-quality source media solely for visualization.

### 6.7 Notifications Are Durable

Notifications are persisted server-side.

They must survive browser reload and be delivered through a durable projection, not only transient events.

### 6.8 Product Analytics Is Separate from Operational Telemetry

Product analytics may be used for workflow funnel measurement, but must not capture sensitive content, media bytes, tokens, signed URLs, or provider secrets.

### 6.9 Feature Flags Are Configuration-Driven

Feature flags are used for rollout and safe deployment.

They do not replace authorization.

Dynamic database-backed feature flags are not required unless operational rollout demands them.

---

## 7. Implemented Backend Baseline Preserved from PLAN A

The following implemented backend capabilities remain unchanged and binding.

### 7.1 Execution Architecture

- modular monolith API
- separate workers by workload class
- PostgreSQL as authoritative system of record
- RabbitMQ as durable transport
- Redis for cache/rate limiting/ephemeral coordination only
- MassTransit saga orchestration
- scoped DAG pipeline
- explicit fan-out/fan-in barriers
- stage execution leases and fencing tokens
- delayed lease timeout plus sweeper reconciliation
- durable cancellation and retry semantics

### 7.2 Artifact Architecture

- immutable artifacts
- content-addressed bytes through `ContentObject`
- relational artifact lineage
- atomic artifact publication with stage completion
- tenant-scoped deduplication only
- orphan blob reconciliation
- retention-aware logical deletion and physical deletion

### 7.3 Provider Architecture

- capability-based provider selection
- tenant privacy policy participation
- provider health awareness
- provider execution recording
- provider failure versus quality failure separation
- mock providers for local development and CI
- Azure/OpenAI/Google/local inference provider families

### 7.4 Product Backend Capabilities

- resumable multipart uploads
- media validation
- explicit processing start
- durable progress
- review workflow
- export jobs
- cost reservations and quotas
- rate limiting
- auditability
- security and tenant isolation
- observability
- Kubernetes deployment

These capabilities are not redefined here.

They are consumed by the frontend and extended only where explicitly stated in this plan.

---

## 8. Required Backend Extensions

Only the following backend extensions are required by this plan.

They must be implemented as amendments to the existing backend, not as a parallel system.

---

### 8.1 Identity, Tenant Users, and Preferences

#### 8.1.1 `TenantUser`

Add tenant-bound user records if not already present.

Fields:

- `Id` (`usr_` public ID)
- `TenantId`
- `ExternalSubject`
- `Email`
- `DisplayName`
- `Status`
- `CreatedAt`
- `UpdatedAt`

Constraints:

- unique `(TenantId, ExternalSubject)`
- tenant-scoped indexes
- RLS enabled

Purpose:

- resolve `/me`
- persist preferences
- assign project membership
- attribute review decisions and activity

#### 8.1.2 `UserPreference`

Fields:

- `TenantId`
- `UserId`
- `Key`
- `ValueJson`
- `UpdatedAt`

Constraints:

- primary key `(TenantId, UserId, Key)`
- tenant-scoped

Supported keys:

- `locale`
- `timezone`
- `theme`
- `defaultProjectFilters`
- `timelineZoom`
- `notificationPreferences`

Do not store sensitive data in preferences.

---

### 8.2 Project Metadata and Membership

#### 8.2.1 `DubbingProject` Extensions

Extend the existing `DubbingProject` entity with:

- `Name`
- `Description`
- `OwnerUserId`
- `CreatedByUserId`
- `UpdatedByUserId`
- `IsArchived`
- `ArchivedAt`
- `SettingsVersion`
- `ProcessingSettingsJson`

`ProjectStatus` remains unchanged.

Archival is a product organization state and must not alter processing semantics.

#### 8.2.2 `ProjectProcessingSettings`

Store processing settings as versioned JSON with schema version.

Supported fields:

- `schemaVersion`
- `sourceSeparationPolicy`
- `outputProfile`
- `timingStrictness`
- `voicePolicy`
- `reviewThreshold`
- `glossary`
- `styleInstructions`

Rules:

- target language remains immutable after project creation
- settings changes are allowed only when no conflicting active run exists
- settings changes must update configuration hash
- settings changes must be audited
- settings changes after completion do not retroactively modify immutable outputs

Example shape:

```json
{
  "schemaVersion": 1,
  "sourceSeparationPolicy": "Auto",
  "outputProfile": "Web",
  "timingStrictness": "Standard",
  "voicePolicy": "Automatic",
  "reviewThreshold": "Default",
  "glossary": [
    {
      "sourceTerm": "foo",
      "targetTerm": "bar",
      "notes": "product term"
    }
  ],
  "styleInstructions": "Neutral documentary tone."
}
```

#### 8.2.3 `ProjectMembership`

Fields:

- `Id` (`mbr_` public ID)
- `TenantId`
- `ProjectId`
- `UserId`
- `Role`
- `GrantedByUserId`
- `CreatedAt`

Roles:

- `ProjectOwner`
- `ProjectEditor`
- `Reviewer`
- `ProjectViewer`

Constraints:

- unique `(ProjectId, UserId)`
- tenant-scoped index
- RLS enabled

Purpose:

- project-level authorization
- notification recipient resolution
- activity attribution

Tenant-wide roles remain supported and authoritative where already implemented.

---

### 8.3 Notifications

#### 8.3.1 `Notification`

Fields:

- `Id` (`ntf_` public ID)
- `TenantId`
- `RecipientUserId`
- `ProjectId` nullable
- `Type`
- `Severity`
- `Title`
- `Body`
- `ResourceType`
- `ResourceId`
- `SourceEventId`
- `ReadAt`
- `CreatedAt`
- `ExpiresAt` nullable

Constraints:

- unique `(TenantId, RecipientUserId, SourceEventId)` where source event is deduplicable
- index `(TenantId, RecipientUserId, ReadAt, CreatedAt)`
- RLS enabled

Notification types:

- `ProcessingCompleted`
- `ProcessingFailed`
- `ManualReviewRequired`
- `ReviewResolved`
- `ExportCompleted`
- `ExportFailed`
- `UploadRejected`
- `QuotaWarning`
- `ProviderPolicyWarning`

Rules:

- notifications are durable
- notifications must survive reload
- notifications are generated from backend events/outbox projections
- notifications must not contain sensitive content beyond short human-readable summaries
- in-app delivery is required
- email/webhook are future channel extensions only

---

### 8.4 Activity Projection

#### 8.4.1 `ActivityEvent`

Fields:

- `Id` (`act_` public ID)
- `TenantId`
- `ProjectId` nullable
- `ProcessingRunId` nullable
- `Type`
- `ActorType`
- `ActorUserId` nullable
- `Summary`
- `Severity`
- `CorrelationId`
- `OccurredAt`
- `SchemaVersion`
- `MetadataJson`

Constraints:

- index `(TenantId, ProjectId, OccurredAt)`
- append-only
- RLS enabled

Purpose:

- project activity feed
- user-visible history
- operator audit support

Relationship to `AuditEvent`:

- `AuditEvent` remains the authoritative append-only security audit record
- `ActivityEvent` is a user-facing projection
- security-relevant actions should appear in both where appropriate

---

### 8.5 Selection and Editing Concurrency

To support transcript and translation review/editing without lost updates, extend segment selection state with explicit concurrency control.

#### 8.5.1 Required Selection State

For each `SpeechSegment`, expose and persist:

- selected transcript version ID
- selected translation version ID
- selected generated audio artifact ID where applicable
- selection version counter
- selection updated timestamp

If the current schema stores selection only through flags on version rows, add a selection projection or concurrency counter sufficient to support:

- expected-version checks
- conflict detection
- auditability
- dependency invalidation

Rules:

- content versions remain immutable
- selection changes are explicit operations
- selection changes must record actor and reason where provided
- selection changes must invalidate dependent stages where applicable

---

### 8.6 Voice Preview

Add support for on-demand voice previews.

#### 8.6.1 `VoicePreviewJob`

Fields:

- `Id` (`vpv_` public ID)
- `TenantId`
- `ProjectId`
- `SpeakerId`
- `VoiceProfileId`
- `RequestedText`
- `Status`
- `ProviderExecutionId` nullable
- `ArtifactId` nullable
- `FailureCategory` nullable
- `CreatedAt`
- `CompletedAt` nullable
- `ExpiresAt` nullable

Statuses:

- `Pending`
- `Running`
- `Completed`
- `Failed`
- `Cancelled`

Rules:

- previews are durable jobs
- previews are subject to quota and rate limits
- previews must record provider execution
- preview artifacts are separate from final generated audio
- cloning previews require valid consent
- voice preview failure must not fail the main pipeline

---

### 8.7 Media Preview Artifacts

To support waveform and timeline performance, add preview artifact support.

Required artifact types:

- `MediaPreviewAudio`
- `WaveformPeaks`
- `VideoPreview` optional
- `VoicePreviewAudio`
- `QcEvidenceArtifact` where needed

Rules:

- preview artifacts are immutable artifacts backed by `ContentObject`
- preview artifacts are tenant-scoped
- preview artifacts must be generated during media analysis or audio preparation where applicable
- waveform peak data should be generated at multiple zoom-friendly resolutions
- UI must prefer previews over archival assets
- preview generation must preserve source media byte-for-byte

---

### 8.8 Provider Health and Diagnostics Read Support

Expose provider health and diagnostics read models for admin/operator UI.

Required read surfaces:

- provider health summary
- provider route summary
- queue depth summary
- DLQ summary
- stale lease summary
- orphan reconciliation summary
- review backlog summary
- worker health summary

Rules:

- diagnostics endpoints must require elevated authorization
- diagnostics responses must exclude secrets
- diagnostics responses must include correlation IDs where useful
- diagnostics are read-only by default
- privileged mutations require reason, confirmation, and audit

---

### 8.9 Backend Extension Validation

The backend extensions are complete only when:

- migrations apply cleanly with expand/contract compatibility
- RLS applies to new tenant-scoped tables
- new endpoints honor tenant isolation
- new mutations respect idempotency where state-changing
- new read models return consistent projections
- notifications persist and deduplicate correctly
- activity feed is paginated and tenant-scoped
- selection edits reject stale writes
- voice previews respect quota and consent
- preview artifacts are generated and downloadable through signed URLs only

---

## 9. API Architecture and Required Endpoints

The existing API from `PLAN A` remains authoritative.

This section defines the additional API surface required by the frontend.

All new endpoints must follow the existing conventions:

- `/api/v1/...`
- prefixed public IDs
- tenant authorization
- structured error envelope
- correlation ID propagation
- pagination where list-based
- idempotency for mutating operations
- OpenAPI documentation

---

### 9.1 Authentication and Current User

#### Required Endpoints

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/v1/auth/login` | Embedded login if platform-issued sessions are used |
| POST | `/api/v1/auth/refresh` | Refresh session/token |
| POST | `/api/v1/auth/logout` | End session |
| GET | `/api/v1/me` | Current user, tenant, roles, permissions |
| GET | `/api/v1/me/preferences` | User preferences |
| PUT | `/api/v1/me/preferences` | Update user preferences |

If an external OIDC provider is used, login/refresh/logout may be handled by the provider flow, but the frontend still requires `/me` and preferences endpoints.

#### `/me` Response Must Include

- user ID
- tenant ID
- display name
- email
- roles
- permissions
- locale
- timezone
- feature flags relevant to UI
- session expiry metadata where applicable

#### Permission Model

Expose frontend permission strings such as:

- `project.view`
- `project.edit`
- `project.delete`
- `processing.start`
- `processing.cancel`
- `processing.retry`
- `review.view`
- `review.resolve`
- `export.create`
- `export.download`
- `admin.manage`
- `diagnostics.view`

Frontend permissions are UX hints only.

Backend authorization remains final.

---

### 9.2 Dashboard and Projects

#### Required Endpoints

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/dashboard/summary` | Aggregated dashboard read model |
| GET | `/api/v1/projects` | Project list with filters and pagination |
| POST | `/api/v1/projects` | Create project |
| GET | `/api/v1/projects/{projectId}` | Project detail |
| PATCH | `/api/v1/projects/{projectId}` | Update name/description/archive state/settings where allowed |
| POST | `/api/v1/projects/{projectId}/archive` | Archive project |
| POST | `/api/v1/projects/{projectId}/unarchive` | Unarchive project |
| DELETE | `/api/v1/projects/{projectId}` | Logical delete |

#### Dashboard Summary Must Include

- active project count
- awaiting review count
- failed count
- completed count
- recent outputs
- storage usage
- estimated cost summary
- quota pressure
- provider warnings
- review backlog count

#### Project List Must Support

- status filter
- language filter
- review-required filter
- archived filter
- created/updated date filter
- pagination
- stable sorting

Do not return full tenant datasets to the browser.

---

### 9.3 Uploads and Media

Existing upload endpoints from `PLAN A` remain.

Additional frontend requirements:

- upload status must distinguish transfer completion from media validation completion
- duplicate detection must return actionable error/detail
- part state must remain object-storage-authoritative
- client-supplied SHA-256 may be used for early duplicate hint but server validation remains authoritative

No new upload architecture is required.

---

### 9.4 Processing, Progress, and Workspace

#### Required Endpoints

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/v1/projects/{projectId}/processing` | Start processing |
| GET | `/api/v1/projects/{projectId}/processing` | Processing state |
| POST | `/api/v1/projects/{projectId}/cancel` | Cancel run |
| POST | `/api/v1/projects/{projectId}/retry` | Retry run/stage where supported |
| GET | `/api/v1/projects/{projectId}/progress` | Progress read model |
| GET | `/api/v1/projects/{projectId}/progress/stream` | SSE progress stream |
| GET | `/api/v1/projects/{projectId}/workspace` | Aggregated workspace read model |
| GET | `/api/v1/projects/{projectId}/activity` | Paginated project activity |
| GET | `/api/v1/projects/{projectId}/output` | Output summary |
| GET | `/api/v1/projects/{projectId}/quality` | QC summary and issues |

#### Workspace Read Model Must Include

- project metadata
- media summary
- active run summary
- pipeline phase projection
- current stage summary
- progress summary
- review summary
- warning summary
- output summary
- usage/cost summary
- latest activity preview
- permissions relevant to the project

This endpoint exists to avoid N+1 frontend loading.

---

### 9.5 Segments, Transcripts, Translations

#### Required Endpoints

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/projects/{projectId}/segments` | Segment list with filters |
| GET | `/api/v1/projects/{projectId}/segments/{segmentId}` | Segment detail |
| POST | `/api/v1/projects/{projectId}/segments/{segmentId}/retry` | Retry segment-dependent work |
| POST | `/api/v1/projects/{projectId}/segments/{segmentId}/transcript-selection` | Select transcript version |
| POST | `/api/v1/projects/{projectId}/segments/{segmentId}/translation-selection` | Select translation version |
| POST | `/api/v1/projects/{projectId}/segments/{segmentId}/transcript-edits` | Create manual transcript version |
| POST | `/api/v1/projects/{projectId}/segments/{segmentId}/translation-edits` | Create manual translation version |

#### Segment List Must Support

- pagination
- speaker filter
- review state filter
- quality state filter
- sync state filter
- text search
- time range filter

#### Segment Detail Must Include

- timing metadata
- speaker
- source transcript versions
- selected transcript
- translation versions
- selected translation
- generated audio summary
- sync result
- quality results
- review state
- version metadata
- permissions

#### Editing Rules

- transcript/translation content versions remain immutable
- manual edits create new versions
- selection changes use expected version
- stale writes return conflict
- dependent stage invalidation must be explicit and auditable
- if final output already exists, UI must mark it as stale until reprocessing completes

---

### 9.6 Speakers and Voice Assignment

#### Required Endpoints

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/projects/{projectId}/speakers` | Speaker list |
| GET | `/api/v1/projects/{projectId}/speakers/{speakerId}` | Speaker detail |
| GET | `/api/v1/projects/{projectId}/speakers/{speakerId}/available-voices` | Compatible voices |
| PUT | `/api/v1/projects/{projectId}/speakers/{speakerId}/voice-assignment` | Assign/replace voice |
| POST | `/api/v1/projects/{projectId}/voice-previews` | Request voice preview |
| GET | `/api/v1/projects/{projectId}/voice-previews/{previewId}` | Preview status/result |

#### Voice Assignment Rules

- same speaker must receive stable voice across segments unless explicitly changed
- voice change must invalidate dependent voice generation/timing work where applicable
- voice cloning requires valid consent
- consent revocation blocks new cloning usage
- incompatible voices must be blocked server-side
- frontend may show only compatible voices, but backend validation remains authoritative

---

### 9.7 Reviews

Existing review endpoints remain.

Additional required endpoint:

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/reviews/{reviewId}/context` | Full review context read model |

#### Review Context Must Include

- review item
- project summary
- run summary
- segment
- speaker
- transcript versions
- translation versions
- voice assignment
- generated audio references
- sync result
- quality results
- available actions
- actor permissions
- previous decisions

#### Review Mutation Requirements

Review approve/reject/requeue/resolve endpoints must support:

- idempotency key
- expected review version or equivalent concurrency token
- reason text where appropriate
- audit capture
- conflict handling for stale review state

`ResolvedWithEdit` must create a new manual version where applicable.

---

### 9.8 Exports and Output

Existing export endpoints remain.

The output summary endpoint must expose:

- final video asset if available
- audio-only asset if available
- subtitle exports
- transcript export
- translation export
- timeline JSON export
- speaker metadata export
- quality report export
- partial export state
- export generation state
- signed URL generation capability

Rules:

- all downloads use signed URLs
- signed URLs must be short-lived
- partial exports must include completeness metadata
- exports must not expose internal storage paths

---

### 9.9 Notifications

#### Required Endpoints

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/notifications` | Notification list |
| GET | `/api/v1/notifications/unread-count` | Unread count |
| POST | `/api/v1/notifications/{notificationId}/read` | Mark one read |
| POST | `/api/v1/notifications/read-all` | Mark all read |

#### Rules

- notifications are tenant/user-scoped
- unread count must be lightweight
- notification links must route to relevant project/review/export
- notification payloads must not leak sensitive content

---

### 9.10 Administration and Diagnostics

#### Required Read Endpoints

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/admin/usage` | Tenant usage summary |
| GET | `/api/v1/admin/quotas` | Quota states |
| GET | `/api/v1/admin/provider-health` | Provider health summary |
| GET | `/api/v1/admin/provider-routes` | Effective route summaries |
| GET | `/api/v1/admin/diagnostics/queues` | Queue depth summary |
| GET | `/api/v1/admin/diagnostics/dlq` | DLQ summary |
| GET | `/api/v1/admin/diagnostics/leases` | Stale lease summary |
| GET | `/api/v1/admin/diagnostics/orphans` | Orphan reconciliation summary |
| GET | `/api/v1/admin/diagnostics/review-backlog` | Review backlog summary |

#### Rules

- admin endpoints require elevated roles
- diagnostics endpoints must not expose secrets
- destructive admin actions require reason and audit
- provider secrets are never entered through ordinary UI screens

---

### 9.11 SSE Event Contract

#### Endpoint

```http
GET /api/v1/projects/{projectId}/progress/stream
```

#### Transport Rules

- use authenticated streaming fetch by default
- native `EventSource` may be used only if secure cookie-based session authentication is enabled
- tokens must never be placed in query strings
- stream must support reconnect cursor or last event identifier where implemented
- event delivery is at-least-once and may duplicate
- frontend must treat events as invalidation triggers, not as sole source of truth

#### Event Envelope

```json
{
  "eventId": "evt_...",
  "schemaVersion": 1,
  "eventType": "stage.completed",
  "tenantId": "tenant_...",
  "projectId": "prj_...",
  "processingRunId": "run_...",
  "occurredAt": "2026-01-01T00:00:00Z",
  "payload": {}
}
```

#### Required Event Types

- `project.status_changed`
- `run.status_changed`
- `stage.started`
- `stage.progress`
- `stage.completed`
- `stage.failed`
- `stage.review_required`
- `review.created`
- `review.resolved`
- `export.created`
- `export.completed`
- `export.failed`
- `notification.created`
- `output.ready`

#### Payload Rules

Event payloads must include only:

- identifiers
- status/state summaries
- safe counters
- safe user-facing state

Event payloads must not include:

- secrets
- signed URLs
- raw provider responses
- full transcript/translation bodies except where explicitly safe and necessary
- internal lease or fencing data

---

### 9.12 Error Contract and Frontend Mapping

The backend error envelope remains:

```json
{
  "code": "PROVIDER_TIMEOUT",
  "message": "...",
  "correlationId": "...",
  "details": {}
}
```

The frontend must map backend error codes to user-facing categories.

| Backend Code Class | Frontend Category | Recovery UX |
|---|---|---|
| validation errors | `ValidationError` | inline correction |
| unauthorized | `AuthenticationError` | re-authenticate |
| forbidden | `AuthorizationError` | explain permission limit |
| conflict | `ConflictError` | refresh/resolve stale state |
| quota/rate errors | `QuotaOrRateLimitError` | wait/contact admin |
| provider transient errors | `TemporaryProcessingFailure` | automatic retry/wait |
| media errors | `MediaError` | replace media/contact support |
| review required | `ReviewRequired` | open review |
| internal errors | `UnknownError` | safe failure message |

Raw backend errors must not be shown to ordinary users.

---

## 10. Frontend Architecture

The frontend must be implemented as a standalone application in a separate repository or top-level `frontend/` folder.

It must not contain business logic that belongs to the backend.

---

### 10.1 Technology Baseline

Use:

- React
- TypeScript strict mode
- Vite
- React Router
- TanStack Query
- Zustand or equivalent lightweight client-state store
- Tailwind CSS or equivalent token-based styling system
- OpenAPI-generated TypeScript client
- Playwright
- Vitest
- MSW
- Storybook

---

### 10.2 Project Structure

Recommended structure:

```text
frontend/
  src/
    app/
      App.tsx
      router.tsx
      providers/
      layouts/
    api/
      generated/
      client/
      errors/
      queryKeys/
    auth/
      components/
      hooks/
      services/
    features/
      dashboard/
      projects/
      uploads/
      processing/
      transcript/
      translation/
      voices/
      timeline/
      review/
      quality/
      exports/
      notifications/
      settings/
      admin/
    components/
      ui/
      media/
      forms/
      data-display/
      feedback/
    stores/
      uiStore.ts
      editorStore.ts
      playerStore.ts
    hooks/
      useProject.ts
      useProcessing.ts
      useProgressStream.ts
      useReview.ts
      useMedia.ts
    lib/
      dates/
      formatting/
      permissions/
      status/
      validation/
    styles/
      tokens.css
      globals.css
    i18n/
    telemetry/
    types/
```

Feature modules should own their screens, hooks, and feature-specific components.

Shared components must remain domain-agnostic where possible.

---

### 10.3 State Management Rules

#### Server State

Use TanStack Query for:

- projects
- project workspace
- progress
- segments
- reviews
- exports
- notifications
- admin read models

#### Client State

Use client store only for:

- navigation state
- modal state
- editor drafts
- player position
- timeline viewport
- temporary filters
- upload retry metadata

#### Query Keys

Use a query key factory.

Examples:

```ts
['dashboard', tenantId]
['projects', tenantId, filters]
['project', projectId]
['project-workspace', projectId]
['project-progress', projectId]
['project-segments', projectId, filters]
['segment', projectId, segmentId]
['project-speakers', projectId]
['review', reviewId]
['review-context', reviewId]
['exports', projectId]
['notifications', userId, filters]
['admin-provider-health', tenantId]
```

Do not construct ad hoc query keys throughout components.

---

### 10.4 API Client Requirements

The API client must:

- attach access tokens securely
- propagate correlation IDs
- normalize errors
- support idempotency keys for mutations
- retry only safe idempotent GET requests
- expose typed request/response models from OpenAPI
- avoid exposing raw transport errors directly to UI

The frontend build must fail when generated API types are stale.

Required command:

```bash
make generate-api
```

or equivalent.

CI must verify synchronization between OpenAPI and generated client.

---

### 10.5 Real-Time Client

Implement a progress stream client with:

- authenticated fetch streaming by default
- exponential backoff reconnect
- stale event tolerance
- duplicate event tolerance
- cursor-based catch-up where supported
- invalidation of relevant queries on event receipt
- adaptive polling fallback when SSE is unavailable

Polling guidance:

- active processing: 2–5 seconds
- background active project: 15–30 seconds
- completed/inactive: stop polling

The real-time client must not maintain a local authoritative pipeline database.

---

### 10.6 Upload Client

The upload client must support:

- drag and drop
- file picker
- multipart upload through presigned part URLs
- pause/resume
- cancel/abort
- retry failed part
- network recovery
- browser refresh recovery
- duplicate detection feedback
- local upload metadata persistence

Local persistence may include:

- projectId
- uploadId
- file name
- file size
- file fingerprint
- target language
- upload status

Do not store media bytes in application state.

Upload progress must distinguish:

- uploading
- uploaded
- server validation
- media analysis
- ready
- rejected

---

### 10.7 Media and Timeline Engine

The frontend media layer must support:

- HTML media playback
- signed URL playback
- seek by segment
- playback speed
- volume control
- fullscreen
- picture-in-picture where supported
- waveform rendering from peak data
- timeline lanes
- segment selection
- overlap visualization
- timing warning markers
- issue navigation

Performance requirements:

- virtualize large segment lists
- use canvas for waveform/timeline where appropriate
- memoize expensive derived timeline data
- avoid rendering off-screen segments
- avoid downloading archival media for UI visualization

The frontend must not recalculate authoritative timing or pipeline state.

---

### 10.8 Forms and Validation

Every form must define:

- initial state
- dirty state
- validation rules
- submitting state
- server validation error handling
- success handling
- cancel/reset behavior
- unsaved-change protection

Frontend validation handles only UX-level constraints.

Backend remains authoritative for:

- permissions
- quotas
- provider compatibility
- state transitions
- consent validity
- resource availability

---

### 10.9 Internationalization

The frontend must be localization-ready.

Requirements:

- translation keys instead of hard-coded strings
- locale-aware dates and numbers
- timezone-aware timestamp display
- pluralization support
- RTL-compatible layout direction
- default locale configurable through user preference

---

### 10.10 Accessibility

Target WCAG 2.2 AA.

Mandatory:

- keyboard navigation
- visible focus states
- semantic HTML
- accessible labels
- accessible dialogs
- accessible form errors
- sufficient contrast
- reduced motion support
- accessible media controls
- no color-only status signaling
- accessible live region updates where appropriate

Critical live events must not spam screen readers.

---

### 10.11 Performance

Required frontend performance practices:

- route-level code splitting
- lazy loading of advanced modules
- virtualized lists
- preview media usage
- debounced timeline interactions
- memoization of derived state
- minimal component trees in timeline-heavy areas
- no unnecessary large artifact downloads

Performance targets must be measured against representative fixtures, not idealized hardware.

---

## 11. UX Architecture and Design System

The product must emphasize outcomes, not infrastructure.

Users should understand:

- what they uploaded
- what is happening
- what needs attention
- what quality issues exist
- what can be reviewed or changed
- when output is ready

Users should not need to understand:

- MassTransit
- RabbitMQ
- leases
- stage execution IDs
- provider fallback internals
- content objects
- artifact lineage internals

---

### 11.1 Information Architecture

Primary navigation:

- Dashboard
- Projects
- Review Queue
- Notifications
- Settings

Conditional navigation:

- Admin
- Diagnostics
- Provider Health
- Usage

Project navigation:

- Overview
- Media
- Transcript
- Translation
- Voices
- Timeline
- Quality
- Exports
- Activity

Navigation should adapt to project state.

Examples:

- `Uploading` → Media-focused
- `Processing` → Overview + Progress
- `ManualReviewRequired` → Review-oriented
- `Completed` → Timeline + Quality + Exports

---

### 11.2 Design Tokens

Define tokens for:

- color
- spacing
- typography
- radius
- shadow
- elevation
- icon size
- control height
- motion timing
- focus style
- status color
- semantic color

Semantic status tokens:

- success
- warning
- error
- info
- neutral
- processing
- review
- cancelled

Do not encode business state through arbitrary colors.

---

### 11.3 Core Components

Required primitives:

- Button
- IconButton
- Input
- Textarea
- Select
- Combobox
- Checkbox
- Radio
- Switch
- Slider
- Modal
- Drawer
- Popover
- Tooltip
- Tabs
- Accordion
- Table
- DataGrid
- Pagination
- Badge
- StatusBadge
- ProgressBar
- ProgressRing
- Skeleton
- Alert
- Toast
- EmptyState
- ErrorState
- ConfirmDialog
- CommandMenu
- Breadcrumbs
- Card
- Panel
- SplitPane
- ResizablePanel

Product components:

- ProjectStatusBadge
- PipelineStepper
- StageProgress
- MediaUploader
- UploadProgress
- MediaPlayer
- Waveform
- Timeline
- SegmentRow
- SpeakerBadge
- VoiceSelector
- TranscriptEditor
- TranslationEditor
- ReviewCard
- ReviewQueue
- QualitySummary
- QualityIssue
- ExportCard
- ArtifactPanel
- ProviderExecutionPanel
- CostSummary
- AuditTimeline

---

### 11.4 Required UX States

Every important screen must implement relevant versions of:

- Loading
- Skeleton Loading
- Empty
- Ready
- Refreshing
- Partial Data
- Submitting
- Success
- Recoverable Failure
- Blocking Failure
- Unauthorized
- Forbidden
- Not Found
- Offline / Network Failure
- Cancelled
- Processing
- Manual Review

A screen is not complete until its relevant states are implemented.

---

### 11.5 Responsive Behavior

Breakpoints:

- Desktop: `>= 1280px`
- Tablet: `768px` to `1279px`
- Mobile: `< 768px`

Desktop receives the full workspace experience.

Tablet supports:

- monitoring
- review
- playback
- export
- basic administration

Mobile supports:

- dashboard
- project status
- notifications
- simple approvals
- output/download

The full timeline editor is desktop-first.

On smaller screens, timeline may degrade to list-based inspection.

---

### 11.6 Error UX Rules

Every recoverable error must specify one recovery action:

- retry
- resume
- replace input
- review
- contact administrator
- wait

Never show raw stack traces to ordinary users.

Technical details may be shown only in advanced diagnostics.

---

## 12. Product Workflows

This section defines the required product workflows and their frontend/backend integration.

---

### 12.1 Authentication and Session

#### UX Requirements

Support:

- login
- logout
- session restore
- session expiration
- refresh
- unauthorized handling
- forbidden handling

The application must resolve current user and tenant before rendering the main app shell.

#### Session Rules

- access tokens should be kept in memory where possible
- refresh tokens must not be stored in `localStorage`
- if cookies are used, they must be secure, HttpOnly, and SameSite-protected
- silent refresh should restore session on reload
- failed refresh should route to login while preserving intended destination where safe

#### State Mapping

`/me` provides:

- current user
- current tenant
- roles
- permissions
- preferences
- feature flags

---

### 12.2 Dashboard

#### UX Requirements

Show:

- active projects
- projects awaiting review
- completed projects
- failed projects
- recent outputs
- storage usage
- estimated cost
- quota pressure
- provider warnings
- review backlog

#### API Mapping

- `GET /api/v1/dashboard/summary`
- `GET /api/v1/projects` for drill-down

#### States

- loading
- empty tenant
- partial data
- quota warning
- error

---

### 12.3 Project List

#### UX Requirements

Columns:

- project name
- source media summary
- target language
- status
- progress
- review state
- created
- last activity

Actions:

- open
- cancel
- retry
- export
- delete
- archive/unarchive

Only valid actions should be visible.

#### Filters

- status
- language
- review required
- archived
- owner
- date range

Pagination is server-side.

---

### 12.4 Project Creation

#### Primary Flow

```text
Create Project
  ↓
Select Target Language
  ↓
Configure Basic Settings
  ↓
Upload Media
  ↓
Review Settings
  ↓
Start Processing
```

Do not ask users for provider, queue, worker, or model during initial creation.

#### Advanced Settings

Progressive disclosure may expose:

- source separation policy
- output profile
- timing strictness
- voice policy
- glossary
- style instructions
- review threshold

#### Rules

- target language is immutable after creation
- project name is required
- settings must be validated client-side and server-side
- configuration hash must include settings

---

### 12.5 Resumable Upload

#### UX Requirements

Support:

- drag and drop
- file picker
- progress percentage
- uploaded/total bytes
- pause/resume
- cancel
- retry failed part
- duplicate detection
- unsupported media explanation
- corrupt media explanation
- browser restart recovery

#### State Sequence

```text
Created
  ↓
Uploading
  ↓
Uploaded
  ↓
Server Validation
  ↓
Media Analysis
  ↓
Ready / Rejected
```

Do not show “Upload complete” as final success until server validation is complete.

#### Duplicate Handling

If backend returns duplicate media:

- explain that media already exists
- offer use existing media where supported
- offer upload different file

---

### 12.6 Processing Start

#### UX Requirements

Before start:

- show preflight estimate where available
- show quota state
- show consent warnings if voice cloning is involved
- require explicit confirmation for costly operations where configured

After start:

- navigate to project workspace
- show live progress
- disable conflicting actions while run is active

#### API Mapping

- `POST /api/v1/projects/{projectId}/processing`
- `GET /api/v1/projects/{projectId}/workspace`
- `GET /api/v1/projects/{projectId}/progress/stream`

---

### 12.7 Project Workspace

#### Header

- project name
- source → target language
- status badge
- primary actions

#### Main Area

- pipeline progress projection
- current stage
- review required summary
- warnings
- output summary

#### Secondary Area

- media details
- configuration summary
- run information
- usage/cost
- recent activity

#### Pipeline Visualization

Use a simplified product-facing projection:

```text
Media
  ↓
Analysis
  ↓
Speech
  ↓
Transcript
  ↓
Translation
  ↓
Voices
  ↓
Timing
  ↓
Mix
  ↓
Quality
  ↓
Output
```

This is a UI projection only.

If stages run in parallel, display parallel processing explicitly instead of forcing fake serial order.

---

### 12.8 Live Progress

#### UX Requirements

Show:

- current phase
- current stage summary
- completed units
- failed units
- retrying units
- review units
- skipped units
- warnings
- approximate percentage

Do not show guaranteed ETA.

#### Real-Time Behavior

On SSE event:

- invalidate affected queries
- refetch authoritative state
- update notification badge where relevant

Fallback:

- adaptive polling when SSE is unavailable

---

### 12.9 Transcript Workspace

#### Layout

Desktop:

```text
┌───────────────────────────────────────────────┐
│ Project / Segment Navigation                 │
├───────────────────────┬───────────────────────┤
│ Media Player          │ Transcript            │
│                       │                       │
│ Waveform              │ Segment 1             │
│                       │ Segment 2             │
│                       │ Segment 3             │
├───────────────────────┴───────────────────────┤
│ Segment Inspector / Speaker / Metadata        │
└───────────────────────────────────────────────┘
```

#### Segment Row

Show:

- timestamp
- speaker
- transcript text
- confidence
- review state
- playback control

Advanced view may include:

- provider
- model
- version

#### Playback Sync

Selecting a segment should:

- select the segment
- seek player to segment start
- optionally play the segment

During playback:

- active segment highlights
- auto-scroll optionally follows playback

#### Editing

Support:

- edit
- save as new version
- discard
- undo/redo where practical

The UI must show where a version is original, selected, or manually edited.

---

### 12.10 Translation Workspace

#### UX Requirements

Show side-by-side:

- source transcript
- selected translation
- alternative candidates
- speaker
- time window
- generated duration
- sync status
- glossary context
- voice context

#### Candidate Selection

Alternative translations are immutable.

User may:

- select an existing candidate
- edit selected text and save as manual version
- discard edits

Historical provider-generated versions must not be overwritten.

#### Unsaved Changes

If navigating away:

- save
- discard
- cancel

Never silently lose edits.

---

### 12.11 Voice Assignment Workspace

#### Speaker List

Show:

- speaker
- segment count
- first appearance
- last appearance
- assigned voice
- provider
- voice type
- consent state

#### Voice Selector

Allow:

- preview voice
- select voice
- replace voice
- reset to automatic where applicable

Only compatible voices may be shown.

Backend validation remains authoritative.

#### Voice Cloning

Show explicit consent state:

- not available
- consent required
- consent valid
- consent revoked

If cloning is disabled by tenant policy, show a clear policy message.

#### Voice Change Consequences

Before confirming voice change, show impact:

- affected segment count
- expected invalidation/retry
- estimated cost impact where available

---

### 12.12 Media Player and Timeline Workspace

#### Media Player

Support:

- play/pause
- seek
- volume
- playback speed
- fullscreen
- picture-in-picture where supported
- segment navigation
- time navigation

#### Waveform

Display:

- source waveform
- dialogue regions
- generated dialogue regions
- selected segment
- overlap regions

Use waveform peak artifacts.

Do not download full archival audio solely to draw waveform.

#### Timeline Lanes

Display:

- source video
- source/background audio
- dialogue segments
- generated dialogue
- metadata markers where useful

Timeline should expose:

- start/end
- overlap
- silence
- timing warnings
- missing audio
- review markers

#### Timeline Interaction

Support:

- zoom
- pan
- segment selection
- seek
- play selected region
- jump to next issue
- jump to previous issue

The timeline is read-only with respect to authoritative timing.

Manual timing edits are not part of this plan unless explicitly introduced later.

---

### 12.13 Manual Review Studio

#### Review Queue

Show:

- review item
- project
- segment
- severity
- reason
- created
- status
- assigned reviewer where available

Filters:

- project
- severity
- status
- issue type
- speaker
- language
- age

#### Review Detail

A review detail screen must show all necessary context in one place:

- source media
- transcript
- translation
- speaker
- assigned voice
- generated audio
- timing/sync result
- QC findings
- provider summary where authorized
- previous versions
- allowed actions

Do not require navigation through multiple unrelated screens to understand one review.

#### Review Actions

Support:

- approve
- reject
- requeue
- resolve with edit

For `ResolvedWithEdit`:

- edit content
- reason
- save
- confirm

Show explicitly:

> This creates a new version and records the reviewer decision.

#### Concurrency

If another user resolved the review first:

- show conflict
- refresh context
- prevent duplicate resolution

---

### 12.14 Quality Control Interface

#### Quality Summary

Show:

- overall status
- passed
- warnings
- retry required
- manual review required
- blocked

#### Issue List

Each issue shows:

- code
- severity
- scope
- segment
- description
- evidence
- suggested action
- status

#### Evidence

Where possible show:

- waveform excerpt
- timestamp
- audio preview
- video position
- QC measurement
- related artifact reference

Blocking issues must be visually obvious.

---

### 12.15 Output and Export

#### Output Page

Show:

- final video
- audio output
- subtitles
- transcript
- translation
- timeline JSON
- speaker metadata
- quality report

Each item shows:

- ready
- generating
- failed
- partial
- unavailable

#### Export Creation

Dialog asks for:

- export type
- data scope
- run where applicable
- format

Then:

- generate export

Partial export must be clearly explained.

Example:

> This export contains 96 of 100 segments. 4 segments are unresolved.

#### Downloads

All downloads use signed URLs.

The frontend must not expose internal storage paths or long-lived credentials.

---

### 12.16 Notifications

#### Notification Center

Header icon shows:

- unread count
- recent notifications
- mark read
- open related resource

#### Persistence

Notifications must survive browser reload.

#### Event Integration

SSE `notification.created` events should invalidate notification queries and update unread count.

#### Preferences

Users may configure in-app notification preferences where supported.

Email/webhook are future channel extensions.

---

### 12.17 Activity and Audit

#### Activity Timeline

Examples:

- media uploaded
- processing started
- translation completed
- manual review requested
- reviewer edited segment
- export generated
- project completed

Each event shows:

- timestamp
- actor
- action
- summary

#### Advanced Diagnostics

Authorized users may inspect:

- run ID
- stage execution
- attempt
- provider execution
- model
- latency
- cost
- artifact
- correlation ID

This belongs in an advanced area, not default user views.

---

### 12.18 Cost, Usage, and Quota

#### Project Usage

Show:

- estimated cost
- actual cost where available
- audio duration
- provider usage
- processing units
- storage usage

#### Preflight Estimate

Before expensive operations:

- estimated duration
- estimated provider usage
- estimated cost

Label estimates clearly.

#### Quota States

Show:

- available
- near limit
- exceeded
- reserved

Never expose internal reservation IDs.

---

### 12.19 Admin and Operator Frontend

#### Admin Areas

- tenants
- users
- roles
- provider health
- provider routes
- usage
- quotas
- retention
- audit
- diagnostics
- feature flags
- system health

#### Operational Dashboard

Show:

- queue depth
- active workers
- provider errors
- DLQ depth
- lease recoveries
- storage orphan status
- review backlog
- processing failures

#### Safety

Destructive actions require:

- explicit confirmation
- reason
- permission
- audit event

Examples:

- delete project
- force retry
- cancel run
- change provider policy
- change retention setting

---

## 13. Security, Privacy, and Authorization

This section extends and reinforces the security model of `PLAN A`.

---

### 13.1 Authorization Model

Backend remains the final authorization authority.

Frontend permission checks only control visibility and UX.

Every API endpoint must enforce:

- authentication
- tenant membership
- project access
- role/permission requirements
- resource ownership

---

### 13.2 Tenant Isolation

Tenant isolation must hold across:

- API endpoints
- read models
- SSE streams
- notifications
- activity feeds
- storage keys
- signed URLs
- telemetry tags
- frontend caching keys

Cross-tenant access must return structured unauthorized/forbidden responses and must be covered by negative tests.

---

### 13.3 Token and Session Security

Requirements:

- no access tokens in URLs
- no refresh tokens in `localStorage`
- prefer in-memory access tokens
- use secure HttpOnly cookies where session-based browser authentication is implemented
- enforce CSRF protection for cookie-authenticated mutations
- log out must clear session state client-side and server-side where applicable

---

### 13.4 Signed URL Security

Rules:

- signed URLs are issued only after authorization
- default expiry remains short, 15 minutes unless explicitly changed
- signed URLs must not be logged
- signed URLs must not be sent to analytics
- signed URLs must not be cached long-term by the frontend

---

### 13.5 Content Security

Frontend must configure:

- strict CSP
- no unsafe inline scripts where avoidable
- restricted external resource origins
- safe iframe policies where media/embeds are used
- sanitized rendering of user-generated text
- no media content sent to analytics
- no transcript/translation bodies in telemetry unless explicitly allowed by privacy policy

---

### 13.6 CORS

CORS must allow only configured frontend origins.

No wildcard origin is permitted for authenticated endpoints.

---

### 13.7 Consent and Voice Cloning

Rules:

- voice cloning disabled by default
- cloning requires valid consent record
- consent revocation blocks new cloning usage
- consent state is visible in voice assignment UX
- consent violations produce explicit user-facing errors

---

### 13.8 Secrets

Secrets must remain in secret management infrastructure.

The frontend must never receive:

- provider API keys
- database credentials
- object storage root credentials
- worker credentials
- signing secrets

Admin UI may show provider health and configuration state, but not secret values.

---

## 14. Observability

Observability must cover both backend and frontend.

---

### 14.1 Backend Observability

Existing backend observability from `PLAN A` remains.

Add frontend-supporting metrics where missing:

- SSE active connections
- SSE reconnect rate
- notification generation failure
- read-model latency
- upload UI completion funnel metrics
- review resolution latency
- export generation latency
- preview artifact generation latency

---

### 14.2 Frontend Telemetry

Capture:

- page load
- route transition
- API failure
- UI error
- upload failure
- SSE connection failure
- processing action
- review action
- export action

Include:

- app version
- environment
- browser
- OS
- route
- correlation ID

Never capture:

- tokens
- signed URLs
- secrets
- raw media
- full transcript bodies
- translation bodies
- tenant-private content

---

### 14.3 Product Analytics

Allowed events:

- `project.created`
- `upload.started`
- `upload.completed`
- `processing.started`
- `processing.completed`
- `review.opened`
- `review.resolved`
- `translation.edited`
- `voice.changed`
- `export.created`
- `export.downloaded`

Do not record sensitive payloads.

Product analytics should be disableable by configuration.

---

### 14.4 Correlation

Correlation IDs must propagate:

- API request
- backend logs/traces
- outbox events
- SSE events where appropriate
- frontend error reports

This enables end-to-end diagnosis.

---

## 15. Testing Strategy

Testing must cover backend extensions, frontend features, and cross-layer workflows.

---

### 15.1 Unit Tests

Cover:

- status mapping
- permission helpers
- formatting
- validation
- error mapping
- timeline calculations
- duration formatting
- query key factories
- editor transformations
- upload retry logic

---

### 15.2 Component Tests

Cover:

- upload component
- project status badge
- progress component
- transcript editor
- translation editor
- voice selector
- review panel
- export panel
- notification center
- QC issue list

Each component must cover relevant states:

- loading
- empty
- success
- failure
- disabled
- permission denied

---

### 15.3 API Mock Tests

Use MSW or equivalent.

Cover:

- success
- 401
- 403
- 404
- 409
- 429
- 500
- validation error
- provider error
- partial results
- stale version conflict

---

### 15.4 Integration Tests for Backend Extensions

Cover:

- `/me`
- preferences update
- project archive/unarchive
- workspace read model
- activity pagination
- notification deduplication
- review context
- voice preview authorization
- transcript/translation selection conflicts
- admin diagnostics authorization

---

### 15.5 Cross-Layer Tests

Required cross-layer flows:

- frontend → API → PostgreSQL
- frontend → upload → object storage
- frontend → processing start → SSE
- frontend → review resolution → versioned mutation
- frontend → export creation → signed download
- frontend → voice change → invalidation/retry
- frontend → stale edit conflict → refresh UX

---

### 15.6 E2E Tests

Use Playwright.

Required scenarios:

- login
- create project
- upload media
- resume upload
- start processing
- watch live progress
- open project workspace
- review transcript
- edit translation
- assign voice
- open review item
- resolve review
- download output
- create export
- download export
- cancel processing
- retry failed stage
- handle stale edit conflict
- handle unauthorized/forbidden state

---

### 15.7 Full Product E2E Smoke

At least one browser test must run against:

- real frontend
- real API
- real PostgreSQL
- real object storage
- real message transport where required
- mock AI providers

This is the primary product-level smoke test.

---

### 15.8 Visual Regression

Capture deterministic screenshots for:

- dashboard
- project overview
- upload
- processing
- transcript
- translation
- voice assignment
- timeline
- review studio
- quality
- exports
- settings
- admin

Test:

- desktop
- tablet
- mobile
- dark/light if supported
- LTR
- RTL test mode

Do not compare dynamic progress values pixel-for-pixel.

Use deterministic fixtures.

---

### 15.9 Accessibility Tests

Cover:

- keyboard navigation
- focus visibility
- screen reader labels
- contrast
- reduced motion
- accessible dialogs
- accessible forms
- accessible media controls

---

### 15.10 Performance Tests

Measure:

- initial app interactive time
- project list render time
- project workspace render time
- timeline interaction latency
- segment search latency
- media seek responsiveness

Use representative fixtures.

---

## 16. CI/CD and Contract Management

The CI pipeline must cover both backend extensions and frontend.

---

### 16.1 Backend CI

Include:

- restore
- build
- unit tests
- integration tests
- contract tests
- migrations compatibility
- container build
- image scan
- SBOM
- image signing
- publish

Fail CI on warnings.

---

### 16.2 Frontend CI

Include:

- install
- lint
- type check
- unit tests
- component tests
- build
- API contract generation
- API contract verification
- Playwright E2E
- visual regression
- accessibility tests
- dependency audit
- container build
- publish

Fail CI on:

- TypeScript errors
- lint violations
- failing tests
- contract drift
- production build failure
- severe dependency vulnerabilities

---

### 16.3 Contract Rules

OpenAPI is authoritative.

Requirements:

- generate TypeScript clients from OpenAPI
- version API schemas
- include examples
- document enums
- document error codes
- document pagination
- document SSE event contracts
- document idempotency requirements
- document optimistic concurrency fields
- detect breaking API changes in CI

---

## 17. Deployment

Deployment must remain consistent with the `PLAN A` Kubernetes model.

---

### 17.1 Frontend Deployment

The frontend is deployed as static assets through:

- CDN, or
- ingress/static host in Kubernetes

Requirements:

- SPA fallback
- immutable hashed asset caching
- short-lived HTML entrypoint caching
- HTTPS enforced
- compression enabled
- CSP headers configured
- environment configuration injected safely
- health/version endpoint exposed where practical

---

### 17.2 Topology

Recommended production path:

```text
Browser
  ↓
CDN / Ingress
  ↓
Frontend Static Host
  ↓
API
  ↓
Workers / Storage / Providers
```

The frontend must not communicate directly with:

- PostgreSQL
- RabbitMQ
- Redis
- worker containers

---

### 17.3 Backend Extension Deployment

Required backend extensions must be deployed using:

- expand/contract migrations
- migration job before API rollout where required
- backward compatibility during rollout
- feature-flagged UI exposure where useful

---

### 17.4 Environment Configuration

Frontend environment variables may include:

- `VITE_API_BASE_URL`
- `VITE_ENVIRONMENT`
- `VITE_APP_VERSION`
- `VITE_ENABLE_ANALYTICS`
- `VITE_ENABLE_DIAGNOSTICS`
- `VITE_ENABLE_EXPERIMENTAL_FEATURES`
- `VITE_SENTRY_DSN`

Never expose secrets.

---

## 18. Operations, DR, and Operational Readiness

Operational requirements from `PLAN A` remain authoritative.

This plan adds product-facing operational concerns.

---

### 18.1 Frontend Operational Concerns

Operations must monitor:

- CDN/static host availability
- frontend error rate
- SSE connection failure rate
- authentication failure rate
- upload failure rate
- API contract mismatch incidents
- frontend deployment rollback health

---

### 18.2 Support Diagnostics

Authorized operators must be able to inspect:

- project workspace state
- run state
- review backlog
- notification backlog
- queue depth
- DLQ
- stale leases
- orphan objects
- provider health
- cost anomalies

Diagnostics access must be role-restricted and audited.

---

### 18.3 Incident Runbooks

Add runbooks for:

- authentication outage
- CDN outage
- SSE outage
- frontend deployment failure
- notification backlog incident
- contract drift incident
- upload surge/failure incident
- review backlog surge

These supplement backend runbooks from `PLAN A`.

---

### 18.4 Backup and Restore

New durable entities must be included in backup/restore:

- tenant users
- preferences
- notifications
- activity events
- project memberships
- voice preview jobs
- extended project metadata

---

## 19. Optional Enrichment and Local AI / GPU UX

Optional enrichment remains governed by `PLAN A`.

This plan defines only the UX boundary.

---

### 19.1 Video Intelligence

If enabled:

- show enrichment as optional output
- do not block core completion on enrichment failure
- display separate artifacts/results
- expose feature-flag state where relevant

---

### 19.2 Lip Sync

If enabled:

- show lip-sync score/results where available
- show transformed output as separate asset
- make clear that core dubbing completion is independent

---

### 19.3 Local AI / GPU

If local inference is enabled:

- admin/operator UI may show local provider health
- diagnostics may show model/version/device metadata
- privacy policy must still govern routing
- frontend must not expose GPU internals to ordinary users

---

## 20. State Ownership Matrix

This matrix is binding for implementation and review.

| State | Authority |
|---|---|
| Project lifecycle status | PostgreSQL |
| Processing lifecycle status | PostgreSQL / `ProcessingRun` |
| Stage execution status | PostgreSQL / `StageExecution` |
| Review status | PostgreSQL / `ReviewItem` |
| Transcript content versions | PostgreSQL / immutable versions |
| Translation content versions | PostgreSQL / immutable versions |
| Voice assignment | PostgreSQL |
| Artifact status | PostgreSQL |
| Content bytes | Object storage |
| Upload part state | Object storage / upload reconciliation |
| Rate limit state | Redis |
| Cost reservation state | PostgreSQL |
| Notification state | PostgreSQL |
| Activity feed | PostgreSQL projection |
| User preferences | PostgreSQL |
| UI modal state | Frontend |
| Timeline viewport | Frontend |
| Current playback position | Frontend |
| Editor draft | Frontend |
| Project progress projection | Backend read model |

No duplicate authorities are allowed.

---

## 21. Async Workflow Completeness

Every major long-running operation must expose durable backend state and clear UX state.

| Workflow | Backend Authority | UX States |
|---|---|---|
| Upload | UploadSession / object storage | uploading, paused, validating, ready, rejected |
| Media validation | StageExecution / MediaAsset | pending, analyzing, ready, rejected |
| Processing run | ProcessingRun | pending, running, review, failed, cancelling, cancelled, completed |
| Review | ReviewItem | open, resolved, requeued, rejected, approved |
| Export | ExportJob | pending, running, completed, failed, cancelled |
| Voice preview | VoicePreviewJob | pending, running, completed, failed |
| Retry | ProcessingRun / StageExecution | requested, scheduled, running, completed, failed |
| Enrichment | Optional job model | pending, running, completed, failed, skipped |

If any async workflow lacks one of these states, it is not complete.

---

## 22. Implementation Order

The following order minimizes rework.

### Phase B-1 — UX and Contract Foundation

Deliver:

- design tokens
- component inventory
- wireframes for core screens
- API read-model contracts
- SSE event contract
- OpenAPI enrichment

Dependencies:

- implemented backend baseline
- authentication mechanism selected

### Phase B-2 — Frontend Foundation

Deliver:

- React/TypeScript/Vite app
- routing
- API client generation
- TanStack Query setup
- auth/session handling
- error mapping
- telemetry foundation
- i18n foundation

Dependencies:

- B-1

### Phase B-3 — Core Project Workflow

Deliver:

- dashboard
- project list
- project creation
- upload
- resumable upload
- project workspace
- processing start
- progress
- cancel/retry

Dependencies:

- B-2
- backend read models
- upload and processing APIs

### Phase B-4 — Inspection and Editing

Deliver:

- transcript workspace
- translation workspace
- voice assignment
- media player
- waveform
- segment inspector
- timing visualization

Dependencies:

- B-3
- preview artifacts
- segment APIs

### Phase B-5 — Review Studio

Deliver:

- review queue
- review context
- transcript/translation editing
- version-aware save
- approve/reject/requeue/resolve
- review notifications

Dependencies:

- B-4
- review context endpoint
- version-aware editing endpoints

### Phase B-6 — Timeline, QC, Output, Export

Deliver:

- timeline workspace
- final playback
- QC dashboard
- output page
- export creation
- downloads
- partial export states

Dependencies:

- B-5
- output summary endpoint
- export endpoints

### Phase B-7 — Admin, Notifications, Activity

Deliver:

- notification center
- activity feed
- admin dashboards
- usage/quota views
- diagnostics views
- settings/preferences

Dependencies:

- B-3 through B-6
- notification/activity backend extensions

### Phase B-8 — Hardening

Deliver:

- accessibility audit
- responsive audit
- visual regression
- E2E
- browser compatibility
- performance profiling
- security audit
- production telemetry
- deployment hardening

Dependencies:

- all prior phases

---

## 23. Final Verification Checklists

### 23.1 Product Completeness

- [ ] User can log in and resolve tenant context
- [ ] Dashboard shows actionable project state
- [ ] Project creation works with settings and target language
- [ ] Upload is resumable and survives refresh
- [ ] Media validation state is clear
- [ ] Processing can be started explicitly
- [ ] Live progress is visible
- [ ] Cancellation works
- [ ] Retry works
- [ ] Transcript inspection works
- [ ] Translation inspection and editing works
- [ ] Voice assignment works
- [ ] Voice preview works where supported
- [ ] Timeline/workspace playback works
- [ ] Manual review can be resolved
- [ ] QC results are visible
- [ ] Final output can be previewed
- [ ] Exports can be generated and downloaded
- [ ] Notifications persist and link correctly
- [ ] Activity history is visible
- [ ] Admin/operator views are protected and useful

---

### 23.2 Backend Extension Completeness

- [ ] `/me` returns current user and tenant
- [ ] Preferences are persisted
- [ ] Project metadata and archive state are persisted
- [ ] Project membership is enforced where used
- [ ] Notifications are durable and deduplicated
- [ ] Activity events are paginated and tenant-scoped
- [ ] Workspace read model returns consistent state
- [ ] Review context endpoint returns complete context
- [ ] SSE stream contract is implemented
- [ ] Transcript/translation edits are version-aware
- [ ] Stale edits return conflict
- [ ] Voice assignment changes invalidate dependents correctly
- [ ] Voice previews respect consent/quota
- [ ] Preview artifacts are generated and authorized
- [ ] Admin diagnostics endpoints are protected
- [ ] New tables are covered by RLS and tenant filters

---

### 23.3 Frontend Architecture Completeness

- [ ] Generated API client is synchronized with OpenAPI
- [ ] Server state and client state are separated
- [ ] Query keys are centralized
- [ ] Real-time events invalidate queries
- [ ] Polling fallback works
- [ ] Upload client supports resume/retry/refresh recovery
- [ ] Media UI uses preview artifacts
- [ ] Timeline remains performant on large projects
- [ ] Forms implement unsaved-change protection
- [ ] Errors are mapped to user-facing recovery actions
- [ ] Permissions control visibility but do not replace backend authorization
- [ ] Accessibility requirements are met
- [ ] Responsive breakpoints are implemented
- [ ] Feature flags control experimental UI safely

---

### 23.4 Security and Privacy Completeness

- [ ] Authentication/session flows are secure
- [ ] Tokens are not stored insecurely
- [ ] Signed URLs are short-lived and authorized
- [ ] CORS is restricted
- [ ] CSP is configured
- [ ] Tenant isolation negative tests pass
- [ ] Cross-tenant notifications/activity are impossible
- [ ] Telemetry excludes sensitive content
- [ ] Consent state is enforced in voice cloning UX
- [ ] Provider secrets are not exposed
- [ ] Admin diagnostics are role-protected
- [ ] Destructive actions require confirmation and audit

---

### 23.5 Testing and Deployment Completeness

- [ ] Unit tests pass
- [ ] Component tests pass
- [ ] API mock tests pass
- [ ] Backend extension integration tests pass
- [ ] Cross-layer tests pass
- [ ] E2E full product smoke passes
- [ ] Visual regression passes
- [ ] Accessibility tests pass
- [ ] Performance targets are measured
- [ ] CI fails on contract drift
- [ ] Frontend container/static deployment works
- [ ] SPA fallback and cache headers are correct
- [ ] Rollback procedure is tested
- [ ] Operational dashboards include frontend health
- [ ] New durable entities are included in backup/restore

---

## 24. Completion Criteria

The implementation is complete only when the following real user journey works end-to-end:

```text
User logs in
  ↓
Creates project
  ↓
Selects target language
  ↓
Uploads large media
  ↓
Upload resumes after interruption
  ↓
Media validation succeeds
  ↓
User starts processing
  ↓
Project workspace shows live progress
  ↓
Backend executes durable DAG
  ↓
User sees warnings/review requirements
  ↓
User opens Review Studio
  ↓
User inspects source audio/video
  ↓
User edits transcript/translation when required
  ↓
User reviews voice assignment
  ↓
User resolves blocking review
  ↓
Pipeline resumes
  ↓
Timing + mixing + QC execute
  ↓
Final output becomes available
  ↓
User previews result
  ↓
User downloads final media
  ↓
User generates exports
  ↓
User downloads subtitles/JSON/QC data
```

The user must not need to understand internal orchestration to complete this workflow.

---

## 25. Architecture Boundary

The final platform boundary is:

```text
                         ┌─────────────────────┐
                         │      Browser        │
                         │ React + TypeScript  │
                         └──────────┬──────────┘
                                    │
                         HTTPS / SSE│
                                    │
                         ┌──────────▼──────────┐
                         │     ASP.NET API     │
                         │ Auth / REST / SSE   │
                         └──────────┬──────────┘
                                    │
               ┌────────────────────┼────────────────────┐
               │                    │                    │
        ┌──────▼──────┐      ┌─────▼─────┐       ┌──────▼──────┐
        │ PostgreSQL  │      │  RabbitMQ │       │    Redis    │
        │ Source Truth│      │  Transport│       │ Ephemeral   │
        └─────────────┘      └─────┬─────┘       └─────────────┘
                                   │
             ┌─────────────────────┼─────────────────────────┐
             │                     │                         │
       ┌─────▼─────┐        ┌──────▼─────┐          ┌──────▼──────┐
       │   Media   │        │ AI Workers │          │ Export/QC   │
       │  Workers  │        │ / GPU      │          │  Workers    │
       └─────┬─────┘        └──────┬─────┘          └──────┬──────┘
             │                     │                         │
             └─────────────────────┼─────────────────────────┘
                                   │
                           ┌───────▼────────┐
                           │ Object Storage │
                           │ Artifacts      │
                           └────────────────┘
```

The binding boundary is:

- backend owns truth
- workers own execution
- object storage owns bytes
- frontend owns presentation and interaction

The frontend must never become a second workflow engine.