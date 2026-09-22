using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DubbingPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSegmentSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "segment_selections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    segment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    selected_transcript_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    selected_translation_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    selected_audio_artifact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    selection_version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_segment_selections", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_segment_selections_tenant_id_project_id",
                table: "segment_selections",
                columns: new[] { "tenant_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_segment_selections_tenant_id_segment_id",
                table: "segment_selections",
                columns: new[] { "tenant_id", "segment_id" },
                unique: true);

            migrationBuilder.Sql(
                """
                ALTER TABLE segment_selections ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON segment_selections USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS tenant_isolation ON segment_selections;
                """);

            migrationBuilder.DropTable(
                name: "segment_selections");
        }
    }
}
