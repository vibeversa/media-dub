using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DubbingPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_state_bus_name_created",
                table: "outbox_state");

            migrationBuilder.DropColumn(
                name: "bus_name",
                table: "outbox_state");

            migrationBuilder.AddColumn<bool>(
                name: "is_deleted",
                table: "dubbing_projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_state_created",
                table: "outbox_state",
                column: "created");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_enqueue_time",
                table: "outbox_message",
                column: "enqueue_time");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_expiration_time",
                table: "outbox_message",
                column: "expiration_time");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_state_created",
                table: "outbox_state");

            migrationBuilder.DropIndex(
                name: "ix_outbox_message_enqueue_time",
                table: "outbox_message");

            migrationBuilder.DropIndex(
                name: "ix_outbox_message_expiration_time",
                table: "outbox_message");

            migrationBuilder.DropColumn(
                name: "is_deleted",
                table: "dubbing_projects");

            migrationBuilder.AddColumn<string>(
                name: "bus_name",
                table: "outbox_state",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_state_bus_name_created",
                table: "outbox_state",
                columns: new[] { "bus_name", "created" });
        }
    }
}
