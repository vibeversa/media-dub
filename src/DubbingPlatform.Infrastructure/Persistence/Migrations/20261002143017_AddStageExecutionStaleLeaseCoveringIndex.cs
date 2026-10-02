using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DubbingPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStageExecutionStaleLeaseCoveringIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_stage_executions_status_lease_expires_at",
                table: "stage_executions",
                columns: new[] { "status", "lease_expires_at" },
                filter: "status = 'Running'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_stage_executions_status_lease_expires_at",
                table: "stage_executions");
        }
    }
}
