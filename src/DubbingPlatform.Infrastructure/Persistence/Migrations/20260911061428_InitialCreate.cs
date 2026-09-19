using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DubbingPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "artifact_parents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    child_artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_artifact_parents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processing_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    produced_by_stage = table.Column<int>(type: "integer", nullable: true),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    schema_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, defaultValue: "1"),
                    content_object_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    configuration_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    execution_snapshot_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    metadata_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_artifacts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    action = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    resource_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    resource_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    details_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "consent_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_identity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    evidence_reference = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    scope = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    jurisdiction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    voice_profile_id = table.Column<Guid>(type: "uuid", nullable: true),
                    granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consent_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "content_objects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sha256hex = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    media_format = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_referenced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_content_objects", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "context_windows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    context_text = table.Column<string>(type: "text", nullable: false),
                    context_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    token_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_context_windows", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cost_reservations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processing_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    segment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    capability = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reserved_amount = table.Column<double>(type: "double precision", nullable: false),
                    actual_amount = table.Column<double>(type: "double precision", nullable: false),
                    currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    price_table_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cost_reservations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "deletion_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scope = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deletion_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "dubbing_projects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    target_language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    settings_json = table.Column<string>(type: "jsonb", nullable: false),
                    configuration_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_media_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    active_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dubbing_projects", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "export_artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    export_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_export_artifacts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "export_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processing_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    format = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    artifact_id_ref = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    completeness_json = table.Column<string>(type: "jsonb", nullable: true),
                    is_partial = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_export_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "generated_audio_artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    segment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    voice_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_object_id = table.Column<Guid>(type: "uuid", nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    is_preview = table.Column<bool>(type: "boolean", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_generated_audio_artifacts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    response_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    response_body = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "inbox_state",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lock_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_version = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    received = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    receive_count = table.Column<int>(type: "integer", nullable: false),
                    expiration_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    consumed = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    delivered = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_sequence_number = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox_state", x => x.id);
                    table.UniqueConstraint("ak_inbox_states_message_id_consumer_id", x => new { x.message_id, x.consumer_id });
                });

            migrationBuilder.CreateTable(
                name: "media_assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_object_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    container = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    audio_codec = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    video_codec = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    sample_rate = table.Column<int>(type: "integer", nullable: false),
                    channels = table.Column<int>(type: "integer", nullable: false),
                    channel_layout = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_assets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_state",
                columns: table => new
                {
                    outbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lock_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_version = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    delivered = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_sequence_number = table.Column<long>(type: "bigint", nullable: true),
                    bus_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_state", x => x.outbox_id);
                });

            migrationBuilder.CreateTable(
                name: "output_assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processing_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    media_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    container = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_output_assets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "overlap_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_ms = table.Column<int>(type: "integer", nullable: false),
                    end_ms = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_overlap_groups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "processing_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_providers_allowed = table.Column<bool>(type: "boolean", nullable: false),
                    allowed_providers = table.Column<string[]>(type: "text[]", nullable: false),
                    residency_constraint = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    sensitive_policy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    voice_policy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    local_inference_allowed = table.Column<bool>(type: "boolean", nullable: false),
                    retention_override = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processing_policies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "processing_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    pipeline_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    configuration_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    provider_route_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    execution_snapshot_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processing_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "prompt_template_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    prompt_template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    system_instruction = table.Column<string>(type: "text", nullable: false),
                    template_body = table.Column<string>(type: "text", nullable: false),
                    safety_settings_json = table.Column<string>(type: "jsonb", nullable: false),
                    prompt_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_prompt_template_versions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "prompt_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_prompt_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "provider_capability_descriptors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    capability = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    supported_languages = table.Column<string[]>(type: "text[]", nullable: false),
                    supported_formats = table.Column<string[]>(type: "text[]", nullable: false),
                    max_input_bytes = table.Column<long>(type: "bigint", nullable: false),
                    max_duration_ms = table.Column<int>(type: "integer", nullable: false),
                    batching = table.Column<bool>(type: "boolean", nullable: false),
                    async_job = table.Column<bool>(type: "boolean", nullable: false),
                    word_timestamps = table.Column<bool>(type: "boolean", nullable: false),
                    diarization = table.Column<bool>(type: "boolean", nullable: false),
                    voice_inventory = table.Column<string[]>(type: "text[]", nullable: false),
                    voice_cloning = table.Column<bool>(type: "boolean", nullable: false),
                    timing_controls = table.Column<double[]>(type: "double precision[]", nullable: false),
                    confidence_semantics = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    rate_limit_dims_json = table.Column<string>(type: "jsonb", nullable: false),
                    cost_dims_json = table.Column<string>(type: "jsonb", nullable: false),
                    privacy_class = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    region = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_capability_descriptors", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "provider_executions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processing_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage_execution_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    capability = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    model = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    model_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    deployment = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    region = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    api_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    response_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    latency_ms = table.Column<long>(type: "bigint", nullable: false),
                    tokens_in = table.Column<int>(type: "integer", nullable: true),
                    tokens_out = table.Column<int>(type: "integer", nullable: true),
                    audio_seconds = table.Column<double>(type: "double precision", nullable: true),
                    estimated_cost = table.Column<double>(type: "double precision", nullable: true),
                    actual_cost = table.Column<double>(type: "double precision", nullable: true),
                    price_table_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    fallback_reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    prompt_template_id = table.Column<string>(type: "text", nullable: true),
                    prompt_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    voice_profile_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    external_job_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    provider_idempotency_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_executions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "provider_route_snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processing_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    route_config_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    capability_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    privacy_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_route_snapshots", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "quality_results",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processing_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    scope_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    segment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    severity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    details_json = table.Column<string>(type: "jsonb", nullable: true),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quality_results", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "quota_usages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dimension = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    used = table.Column<long>(type: "bigint", nullable: false),
                    limit = table.Column<long>(type: "bigint", nullable: false),
                    window_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    window_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quota_usages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "retention_holds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: false),
                    placed_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    placed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_retention_holds", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "review_decisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reviewer = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    metadata_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_decisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "review_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processing_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    scope_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    segment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_items", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "run_stage_summaries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processing_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    expected_units = table.Column<int>(type: "integer", nullable: false),
                    completed_units = table.Column<int>(type: "integer", nullable: false),
                    failed_units = table.Column<int>(type: "integer", nullable: false),
                    skipped_units = table.Column<int>(type: "integer", nullable: false),
                    review_units = table.Column<int>(type: "integer", nullable: false),
                    cancelled_units = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_run_stage_summaries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "segment_context_assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    segment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    context_window_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_segment_context_assignments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "segment_overlaps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    overlap_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    segment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    relation_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    overlap_start_ms = table.Column<int>(type: "integer", nullable: false),
                    overlap_end_ms = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_segment_overlaps", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "speaker_voice_assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    speaker_id = table.Column<Guid>(type: "uuid", nullable: false),
                    voice_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    policy_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_speaker_voice_assignments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "speakers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    speaker_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    first_appearance_ms = table.Column<int>(type: "integer", nullable: false),
                    last_appearance_ms = table.Column<int>(type: "integer", nullable: false),
                    mapping_method = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    mapping_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    provider_label = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_speakers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "speech_segments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    start_ms = table.Column<int>(type: "integer", nullable: false),
                    end_ms = table.Column<int>(type: "integer", nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    speaker_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_speech_segments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stage_executions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processing_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    scope_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    scope_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    segment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    lease_owner = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    lease_token = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    lease_token_version = table.Column<long>(type: "bigint", nullable: false),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    input_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    configuration_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    execution_snapshot_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    output_artifact_ids_json = table.Column<string>(type: "jsonb", nullable: true),
                    error_code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stage_executions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stage_input_artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage_execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stage_input_artifacts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stage_output_artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage_execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stage_output_artifacts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stage_unit_completions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processing_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    scope_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    scope_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    stage_execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stage_unit_completions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sync_results",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    segment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sync_score = table.Column<double>(type: "double precision", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    target_window_ms = table.Column<int>(type: "integer", nullable: false),
                    actual_duration_ms = table.Column<int>(type: "integer", nullable: false),
                    rate_delta = table.Column<double>(type: "double precision", nullable: false),
                    stretch_factor = table.Column<double>(type: "double precision", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_results", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    slug = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "transcript_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    segment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    word_timestamps_artifact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_selected = table.Column<bool>(type: "boolean", nullable: false),
                    needs_review = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transcript_versions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "translation_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    segment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    primary_text = table.Column<string>(type: "text", nullable: false),
                    alternative_texts = table.Column<string[]>(type: "text[]", nullable: false),
                    semantic_score = table.Column<double>(type: "double precision", nullable: false),
                    naturalness_score = table.Column<double>(type: "double precision", nullable: false),
                    timing_score = table.Column<double>(type: "double precision", nullable: false),
                    provider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    prompt_template_id = table.Column<Guid>(type: "uuid", nullable: true),
                    prompt_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    is_selected = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_translation_versions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "upload_parts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    upload_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    part_number = table.Column<int>(type: "integer", nullable: false),
                    e_tag = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_upload_parts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "upload_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    declared_content_type = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    declared_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    max_part_bytes = table.Column<long>(type: "bigint", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    multipart_upload_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    client_sha256hex = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    part_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_upload_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "voice_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    voice_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    voice_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    cloning_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    model_ref_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_voice_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_message",
                columns: table => new
                {
                    sequence_number = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    enqueue_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    sent_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    headers = table.Column<string>(type: "text", nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    inbox_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    inbox_consumer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    outbox_id = table.Column<Guid>(type: "uuid", nullable: true),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_type = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    message_type = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    initiator_id = table.Column<Guid>(type: "uuid", nullable: true),
                    request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_address = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    destination_address = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    response_address = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    fault_address = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    expiration_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_message", x => x.sequence_number);
                    table.ForeignKey(
                        name: "fk_outbox_message_inbox_states_inbox_message_id_inbox_consumer",
                        columns: x => new { x.inbox_message_id, x.inbox_consumer_id },
                        principalTable: "inbox_state",
                        principalColumns: new[] { "message_id", "consumer_id" });
                    table.ForeignKey(
                        name: "fk_outbox_message_outbox_states_outbox_id",
                        column: x => x.outbox_id,
                        principalTable: "outbox_state",
                        principalColumn: "outbox_id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_artifact_parents_child_artifact_id_parent_artifact_id",
                table: "artifact_parents",
                columns: new[] { "child_artifact_id", "parent_artifact_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_artifact_parents_parent_artifact_id",
                table: "artifact_parents",
                column: "parent_artifact_id");

            migrationBuilder.CreateIndex(
                name: "ix_artifacts_content_object_id",
                table: "artifacts",
                column: "content_object_id");

            migrationBuilder.CreateIndex(
                name: "ix_artifacts_tenant_id_project_id_processing_run_id",
                table: "artifacts",
                columns: new[] { "tenant_id", "project_id", "processing_run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_tenant_id_created_at",
                table: "audit_events",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_tenant_id_resource_type_resource_id",
                table: "audit_events",
                columns: new[] { "tenant_id", "resource_type", "resource_id" });

            migrationBuilder.CreateIndex(
                name: "ix_consent_records_tenant_id_status",
                table: "consent_records",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_consent_records_tenant_id_subject_identity",
                table: "consent_records",
                columns: new[] { "tenant_id", "subject_identity" });

            migrationBuilder.CreateIndex(
                name: "ix_content_objects_tenant_id_content_hash",
                table: "content_objects",
                columns: new[] { "tenant_id", "content_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_content_objects_tenant_id_status",
                table: "content_objects",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_context_windows_tenant_id_project_id",
                table: "context_windows",
                columns: new[] { "tenant_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_context_windows_tenant_id_run_id_sequence",
                table: "context_windows",
                columns: new[] { "tenant_id", "run_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cost_reservations_tenant_id_project_id",
                table: "cost_reservations",
                columns: new[] { "tenant_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_cost_reservations_tenant_id_state",
                table: "cost_reservations",
                columns: new[] { "tenant_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_deletion_jobs_tenant_id_status_created_at",
                table: "deletion_jobs",
                columns: new[] { "tenant_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_dubbing_projects_tenant_id_created_at",
                table: "dubbing_projects",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_dubbing_projects_tenant_id_status",
                table: "dubbing_projects",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_export_artifacts_export_job_id_artifact_id",
                table: "export_artifacts",
                columns: new[] { "export_job_id", "artifact_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_export_jobs_tenant_id_project_id_status",
                table: "export_jobs",
                columns: new[] { "tenant_id", "project_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_export_jobs_tenant_id_status_created_at",
                table: "export_jobs",
                columns: new[] { "tenant_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_generated_audio_artifacts_project_id_run_id_segment_id",
                table: "generated_audio_artifacts",
                columns: new[] { "project_id", "run_id", "segment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_generated_audio_artifacts_tenant_id_project_id",
                table: "generated_audio_artifacts",
                columns: new[] { "tenant_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_records_tenant_id_endpoint_idempotency_key",
                table: "idempotency_records",
                columns: new[] { "tenant_id", "endpoint", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_records_tenant_id_state_expires_at",
                table: "idempotency_records",
                columns: new[] { "tenant_id", "state", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_inbox_state_delivered",
                table: "inbox_state",
                column: "delivered");

            migrationBuilder.CreateIndex(
                name: "ix_media_assets_tenant_id_content_hash",
                table: "media_assets",
                columns: new[] { "tenant_id", "content_hash" });

            migrationBuilder.CreateIndex(
                name: "ix_media_assets_tenant_id_project_id",
                table: "media_assets",
                columns: new[] { "tenant_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_inbox_message_id_inbox_consumer_id_sequence_",
                table: "outbox_message",
                columns: new[] { "inbox_message_id", "inbox_consumer_id", "sequence_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_outbox_id_sequence_number",
                table: "outbox_message",
                columns: new[] { "outbox_id", "sequence_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_state_bus_name_created",
                table: "outbox_state",
                columns: new[] { "bus_name", "created" });

            migrationBuilder.CreateIndex(
                name: "ix_output_assets_tenant_id_processing_run_id_artifact_id",
                table: "output_assets",
                columns: new[] { "tenant_id", "processing_run_id", "artifact_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_output_assets_tenant_id_project_id",
                table: "output_assets",
                columns: new[] { "tenant_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_overlap_groups_tenant_id_project_id",
                table: "overlap_groups",
                columns: new[] { "tenant_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_processing_policies_tenant_id",
                table: "processing_policies",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_processing_runs_project_id",
                table: "processing_runs",
                column: "project_id",
                unique: true,
                filter: "status IN ('Pending','Running','Cancelling','ManualReviewRequired')");

            migrationBuilder.CreateIndex(
                name: "ix_processing_runs_tenant_id_project_id",
                table: "processing_runs",
                columns: new[] { "tenant_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_processing_runs_tenant_id_status_created_at",
                table: "processing_runs",
                columns: new[] { "tenant_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_prompt_template_versions_prompt_template_id_version",
                table: "prompt_template_versions",
                columns: new[] { "prompt_template_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_prompt_template_versions_tenant_id_prompt_template_id",
                table: "prompt_template_versions",
                columns: new[] { "tenant_id", "prompt_template_id" });

            migrationBuilder.CreateIndex(
                name: "ix_prompt_templates_tenant_id_name",
                table: "prompt_templates",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_provider_capability_descriptors_tenant_id_provider_capabili",
                table: "provider_capability_descriptors",
                columns: new[] { "tenant_id", "provider", "capability", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_provider_executions_project_id_created_at",
                table: "provider_executions",
                columns: new[] { "project_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_provider_executions_tenant_id_processing_run_id_created_at",
                table: "provider_executions",
                columns: new[] { "tenant_id", "processing_run_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_provider_route_snapshots_tenant_id_processing_run_id",
                table: "provider_route_snapshots",
                columns: new[] { "tenant_id", "processing_run_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_provider_route_snapshots_tenant_id_project_id",
                table: "provider_route_snapshots",
                columns: new[] { "tenant_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_quality_results_tenant_id_processing_run_id",
                table: "quality_results",
                columns: new[] { "tenant_id", "processing_run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_quality_results_tenant_id_project_id",
                table: "quality_results",
                columns: new[] { "tenant_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_quota_usages_tenant_id_dimension_window_start",
                table: "quota_usages",
                columns: new[] { "tenant_id", "dimension", "window_start" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_retention_holds_tenant_id_artifact_id",
                table: "retention_holds",
                columns: new[] { "tenant_id", "artifact_id" });

            migrationBuilder.CreateIndex(
                name: "ix_retention_holds_tenant_id_is_active",
                table: "retention_holds",
                columns: new[] { "tenant_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_retention_holds_tenant_id_project_id",
                table: "retention_holds",
                columns: new[] { "tenant_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_review_decisions_review_item_id",
                table: "review_decisions",
                column: "review_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_items_tenant_id_project_id_processing_run_id",
                table: "review_items",
                columns: new[] { "tenant_id", "project_id", "processing_run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_review_items_tenant_id_status_created_at",
                table: "review_items",
                columns: new[] { "tenant_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_run_stage_summaries_tenant_id_processing_run_id_stage_type",
                table: "run_stage_summaries",
                columns: new[] { "tenant_id", "processing_run_id", "stage_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_segment_context_assignments_segment_id_context_window_id",
                table: "segment_context_assignments",
                columns: new[] { "segment_id", "context_window_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_segment_overlaps_tenant_id_overlap_group_id",
                table: "segment_overlaps",
                columns: new[] { "tenant_id", "overlap_group_id" });

            migrationBuilder.CreateIndex(
                name: "ix_speaker_voice_assignments_tenant_id_project_id",
                table: "speaker_voice_assignments",
                columns: new[] { "tenant_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_speaker_voice_assignments_tenant_id_run_id_speaker_id",
                table: "speaker_voice_assignments",
                columns: new[] { "tenant_id", "run_id", "speaker_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_speakers_tenant_id_project_id_speaker_key",
                table: "speakers",
                columns: new[] { "tenant_id", "project_id", "speaker_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_speech_segments_project_id_sequence",
                table: "speech_segments",
                columns: new[] { "project_id", "sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_speech_segments_tenant_id_project_id",
                table: "speech_segments",
                columns: new[] { "tenant_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stage_executions_processing_run_id_stage_type_scope_type_sc",
                table: "stage_executions",
                columns: new[] { "processing_run_id", "stage_type", "scope_type", "scope_id", "attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stage_executions_processing_run_id_stage_type_status",
                table: "stage_executions",
                columns: new[] { "processing_run_id", "stage_type", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_stage_executions_processing_run_id_status_lease_expires_at",
                table: "stage_executions",
                columns: new[] { "processing_run_id", "status", "lease_expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_stage_executions_status",
                table: "stage_executions",
                column: "status",
                filter: "status = 'Running'");

            migrationBuilder.CreateIndex(
                name: "ix_stage_executions_tenant_id_project_id",
                table: "stage_executions",
                columns: new[] { "tenant_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stage_input_artifacts_artifact_id",
                table: "stage_input_artifacts",
                column: "artifact_id");

            migrationBuilder.CreateIndex(
                name: "ix_stage_input_artifacts_stage_execution_id_artifact_id",
                table: "stage_input_artifacts",
                columns: new[] { "stage_execution_id", "artifact_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stage_output_artifacts_artifact_id",
                table: "stage_output_artifacts",
                column: "artifact_id");

            migrationBuilder.CreateIndex(
                name: "ix_stage_output_artifacts_stage_execution_id_artifact_id",
                table: "stage_output_artifacts",
                columns: new[] { "stage_execution_id", "artifact_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stage_unit_completions_processing_run_id_stage_type_scope_t",
                table: "stage_unit_completions",
                columns: new[] { "processing_run_id", "stage_type", "scope_type", "scope_id", "stage_execution_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stage_unit_completions_processing_run_id_stage_type_unit_st",
                table: "stage_unit_completions",
                columns: new[] { "processing_run_id", "stage_type", "unit_state" });

            migrationBuilder.CreateIndex(
                name: "ix_sync_results_tenant_id_project_id",
                table: "sync_results",
                columns: new[] { "tenant_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sync_results_tenant_id_run_id_segment_id",
                table: "sync_results",
                columns: new[] { "tenant_id", "run_id", "segment_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenants_slug",
                table: "tenants",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transcript_versions_project_id_segment_id",
                table: "transcript_versions",
                columns: new[] { "project_id", "segment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_transcript_versions_tenant_id_project_id",
                table: "transcript_versions",
                columns: new[] { "tenant_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_translation_versions_project_id_segment_id",
                table: "translation_versions",
                columns: new[] { "project_id", "segment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_translation_versions_tenant_id_project_id",
                table: "translation_versions",
                columns: new[] { "tenant_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_upload_parts_upload_session_id_part_number",
                table: "upload_parts",
                columns: new[] { "upload_session_id", "part_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_upload_sessions_tenant_id_project_id",
                table: "upload_sessions",
                columns: new[] { "tenant_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_upload_sessions_tenant_id_status_expires_at",
                table: "upload_sessions",
                columns: new[] { "tenant_id", "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_voice_profiles_tenant_id_provider",
                table: "voice_profiles",
                columns: new[] { "tenant_id", "provider" });

            // RLS (Task 7 decision): enabled in this same initial migration after table
            // creation. Source of truth is Persistence/Sql/rls_policies.sql; the statements
            // below are embedded verbatim so the migration is self-contained (no file I/O
            // at migrate time, no secrets, deny-by-default via current_setting(..., true)).
            migrationBuilder.Sql(
                """
                ALTER TABLE dubbing_projects ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON dubbing_projects USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE processing_runs ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON processing_runs USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE media_assets ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON media_assets USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE upload_sessions ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON upload_sessions USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE upload_parts ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON upload_parts USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE voice_profiles ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON voice_profiles USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE speakers ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON speakers USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE speech_segments ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON speech_segments USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE context_windows ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON context_windows USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE segment_context_assignments ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON segment_context_assignments USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE overlap_groups ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON overlap_groups USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE segment_overlaps ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON segment_overlaps USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE speaker_voice_assignments ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON speaker_voice_assignments USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE consent_records ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON consent_records USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE transcript_versions ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON transcript_versions USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE translation_versions ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON translation_versions USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE generated_audio_artifacts ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON generated_audio_artifacts USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE sync_results ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON sync_results USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE stage_executions ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON stage_executions USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE run_stage_summaries ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON run_stage_summaries USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE stage_unit_completions ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON stage_unit_completions USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE content_objects ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON content_objects USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE artifacts ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON artifacts USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE artifact_parents ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON artifact_parents USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE stage_input_artifacts ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON stage_input_artifacts USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE stage_output_artifacts ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON stage_output_artifacts USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE provider_executions ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON provider_executions USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE provider_capability_descriptors ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON provider_capability_descriptors USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE provider_route_snapshots ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON provider_route_snapshots USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE prompt_templates ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON prompt_templates USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE prompt_template_versions ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON prompt_template_versions USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE quality_results ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON quality_results USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE review_items ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON review_items USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE review_decisions ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON review_decisions USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE output_assets ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON output_assets USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE export_jobs ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON export_jobs USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE export_artifacts ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON export_artifacts USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE audit_events ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON audit_events USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE idempotency_records ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON idempotency_records USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE cost_reservations ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON cost_reservations USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE quota_usages ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON quota_usages USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE processing_policies ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON processing_policies USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE retention_holds ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON retention_holds USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE deletion_jobs ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON deletion_jobs USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // RLS rollback: DROP TABLE removes policies automatically, but drop them
            // explicitly first so `database update 0` succeeds even if a table drop is
            // reordered. IF EXISTS keeps the rollback idempotent.
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS tenant_isolation ON dubbing_projects;
                DROP POLICY IF EXISTS tenant_isolation ON processing_runs;
                DROP POLICY IF EXISTS tenant_isolation ON media_assets;
                DROP POLICY IF EXISTS tenant_isolation ON upload_sessions;
                DROP POLICY IF EXISTS tenant_isolation ON upload_parts;
                DROP POLICY IF EXISTS tenant_isolation ON voice_profiles;
                DROP POLICY IF EXISTS tenant_isolation ON speakers;
                DROP POLICY IF EXISTS tenant_isolation ON speech_segments;
                DROP POLICY IF EXISTS tenant_isolation ON context_windows;
                DROP POLICY IF EXISTS tenant_isolation ON segment_context_assignments;
                DROP POLICY IF EXISTS tenant_isolation ON overlap_groups;
                DROP POLICY IF EXISTS tenant_isolation ON segment_overlaps;
                DROP POLICY IF EXISTS tenant_isolation ON speaker_voice_assignments;
                DROP POLICY IF EXISTS tenant_isolation ON consent_records;
                DROP POLICY IF EXISTS tenant_isolation ON transcript_versions;
                DROP POLICY IF EXISTS tenant_isolation ON translation_versions;
                DROP POLICY IF EXISTS tenant_isolation ON generated_audio_artifacts;
                DROP POLICY IF EXISTS tenant_isolation ON sync_results;
                DROP POLICY IF EXISTS tenant_isolation ON stage_executions;
                DROP POLICY IF EXISTS tenant_isolation ON run_stage_summaries;
                DROP POLICY IF EXISTS tenant_isolation ON stage_unit_completions;
                DROP POLICY IF EXISTS tenant_isolation ON content_objects;
                DROP POLICY IF EXISTS tenant_isolation ON artifacts;
                DROP POLICY IF EXISTS tenant_isolation ON artifact_parents;
                DROP POLICY IF EXISTS tenant_isolation ON stage_input_artifacts;
                DROP POLICY IF EXISTS tenant_isolation ON stage_output_artifacts;
                DROP POLICY IF EXISTS tenant_isolation ON provider_executions;
                DROP POLICY IF EXISTS tenant_isolation ON provider_capability_descriptors;
                DROP POLICY IF EXISTS tenant_isolation ON provider_route_snapshots;
                DROP POLICY IF EXISTS tenant_isolation ON prompt_templates;
                DROP POLICY IF EXISTS tenant_isolation ON prompt_template_versions;
                DROP POLICY IF EXISTS tenant_isolation ON quality_results;
                DROP POLICY IF EXISTS tenant_isolation ON review_items;
                DROP POLICY IF EXISTS tenant_isolation ON review_decisions;
                DROP POLICY IF EXISTS tenant_isolation ON output_assets;
                DROP POLICY IF EXISTS tenant_isolation ON export_jobs;
                DROP POLICY IF EXISTS tenant_isolation ON export_artifacts;
                DROP POLICY IF EXISTS tenant_isolation ON audit_events;
                DROP POLICY IF EXISTS tenant_isolation ON idempotency_records;
                DROP POLICY IF EXISTS tenant_isolation ON cost_reservations;
                DROP POLICY IF EXISTS tenant_isolation ON quota_usages;
                DROP POLICY IF EXISTS tenant_isolation ON processing_policies;
                DROP POLICY IF EXISTS tenant_isolation ON retention_holds;
                DROP POLICY IF EXISTS tenant_isolation ON deletion_jobs;
                """);

            migrationBuilder.DropTable(
                name: "artifact_parents");

            migrationBuilder.DropTable(
                name: "artifacts");

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "consent_records");

            migrationBuilder.DropTable(
                name: "content_objects");

            migrationBuilder.DropTable(
                name: "context_windows");

            migrationBuilder.DropTable(
                name: "cost_reservations");

            migrationBuilder.DropTable(
                name: "deletion_jobs");

            migrationBuilder.DropTable(
                name: "dubbing_projects");

            migrationBuilder.DropTable(
                name: "export_artifacts");

            migrationBuilder.DropTable(
                name: "export_jobs");

            migrationBuilder.DropTable(
                name: "generated_audio_artifacts");

            migrationBuilder.DropTable(
                name: "idempotency_records");

            migrationBuilder.DropTable(
                name: "media_assets");

            migrationBuilder.DropTable(
                name: "outbox_message");

            migrationBuilder.DropTable(
                name: "output_assets");

            migrationBuilder.DropTable(
                name: "overlap_groups");

            migrationBuilder.DropTable(
                name: "processing_policies");

            migrationBuilder.DropTable(
                name: "processing_runs");

            migrationBuilder.DropTable(
                name: "prompt_template_versions");

            migrationBuilder.DropTable(
                name: "prompt_templates");

            migrationBuilder.DropTable(
                name: "provider_capability_descriptors");

            migrationBuilder.DropTable(
                name: "provider_executions");

            migrationBuilder.DropTable(
                name: "provider_route_snapshots");

            migrationBuilder.DropTable(
                name: "quality_results");

            migrationBuilder.DropTable(
                name: "quota_usages");

            migrationBuilder.DropTable(
                name: "retention_holds");

            migrationBuilder.DropTable(
                name: "review_decisions");

            migrationBuilder.DropTable(
                name: "review_items");

            migrationBuilder.DropTable(
                name: "run_stage_summaries");

            migrationBuilder.DropTable(
                name: "segment_context_assignments");

            migrationBuilder.DropTable(
                name: "segment_overlaps");

            migrationBuilder.DropTable(
                name: "speaker_voice_assignments");

            migrationBuilder.DropTable(
                name: "speakers");

            migrationBuilder.DropTable(
                name: "speech_segments");

            migrationBuilder.DropTable(
                name: "stage_executions");

            migrationBuilder.DropTable(
                name: "stage_input_artifacts");

            migrationBuilder.DropTable(
                name: "stage_output_artifacts");

            migrationBuilder.DropTable(
                name: "stage_unit_completions");

            migrationBuilder.DropTable(
                name: "sync_results");

            migrationBuilder.DropTable(
                name: "tenants");

            migrationBuilder.DropTable(
                name: "transcript_versions");

            migrationBuilder.DropTable(
                name: "translation_versions");

            migrationBuilder.DropTable(
                name: "upload_parts");

            migrationBuilder.DropTable(
                name: "upload_sessions");

            migrationBuilder.DropTable(
                name: "voice_profiles");

            migrationBuilder.DropTable(
                name: "inbox_state");

            migrationBuilder.DropTable(
                name: "outbox_state");
        }
    }
}
