using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DubbingPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderExecutionAuditColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "output_content_hash",
                table: "provider_executions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "prompt_template_version",
                table: "provider_executions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "safety_settings_hash",
                table: "provider_executions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "system_instruction_hash",
                table: "provider_executions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "usage_dimensions_json",
                table: "provider_executions",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "output_content_hash",
                table: "provider_executions");

            migrationBuilder.DropColumn(
                name: "prompt_template_version",
                table: "provider_executions");

            migrationBuilder.DropColumn(
                name: "safety_settings_hash",
                table: "provider_executions");

            migrationBuilder.DropColumn(
                name: "system_instruction_hash",
                table: "provider_executions");

            migrationBuilder.DropColumn(
                name: "usage_dimensions_json",
                table: "provider_executions");
        }
    }
}
