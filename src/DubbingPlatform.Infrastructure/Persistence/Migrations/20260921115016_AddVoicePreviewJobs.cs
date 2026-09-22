using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DubbingPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVoicePreviewJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "voice_preview_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    speaker_id = table.Column<Guid>(type: "uuid", nullable: false),
                    voice_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    quota_check = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    quota_check_reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    consent_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    provider_execution_id = table.Column<Guid>(type: "uuid", nullable: true),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    error_message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_voice_preview_jobs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_voice_preview_jobs_tenant_id_idempotency_key",
                table: "voice_preview_jobs",
                columns: new[] { "tenant_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_voice_preview_jobs_tenant_id_project_id_status",
                table: "voice_preview_jobs",
                columns: new[] { "tenant_id", "project_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_voice_preview_jobs_tenant_id_speaker_id_created_at",
                table: "voice_preview_jobs",
                columns: new[] { "tenant_id", "speaker_id", "created_at" });

            migrationBuilder.Sql(
                """
                ALTER TABLE voice_preview_jobs ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON voice_preview_jobs USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS tenant_isolation ON voice_preview_jobs;
                """);

            migrationBuilder.DropTable(
                name: "voice_preview_jobs");
        }
    }
}
