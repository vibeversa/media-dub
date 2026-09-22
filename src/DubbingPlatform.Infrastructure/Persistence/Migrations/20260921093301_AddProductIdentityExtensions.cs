using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DubbingPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductIdentityExtensions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "archived_at",
                table: "dubbing_projects",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "created_by_user_id",
                table: "dubbing_projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "description",
                table: "dubbing_projects",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_archived",
                table: "dubbing_projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "name",
                table: "dubbing_projects",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "owner_user_id",
                table: "dubbing_projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "processing_settings_json",
                table: "dubbing_projects",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "settings_version",
                table: "dubbing_projects",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<Guid>(
                name: "updated_by_user_id",
                table: "dubbing_projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "project_memberships",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    granted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_memberships", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_preferences",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    value_json = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_preferences", x => new { x.tenant_id, x.user_id, x.key });
                });

            migrationBuilder.CreateIndex(
                name: "ix_dubbing_projects_tenant_id_is_archived",
                table: "dubbing_projects",
                columns: new[] { "tenant_id", "is_archived" });

            migrationBuilder.CreateIndex(
                name: "ix_dubbing_projects_tenant_id_owner_user_id",
                table: "dubbing_projects",
                columns: new[] { "tenant_id", "owner_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_project_memberships_project_id_user_id",
                table: "project_memberships",
                columns: new[] { "project_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_project_memberships_tenant_id_project_id",
                table: "project_memberships",
                columns: new[] { "tenant_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_project_memberships_tenant_id_user_id",
                table: "project_memberships",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_users_tenant_id_email",
                table: "tenant_users",
                columns: new[] { "tenant_id", "email" });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_users_tenant_id_external_subject",
                table: "tenant_users",
                columns: new[] { "tenant_id", "external_subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenant_users_tenant_id_status",
                table: "tenant_users",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_user_preferences_tenant_id_user_id",
                table: "user_preferences",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.Sql(
                """
                ALTER TABLE tenant_users ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON tenant_users USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE user_preferences ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON user_preferences USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                ALTER TABLE project_memberships ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON project_memberships USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS tenant_isolation ON tenant_users;
                DROP POLICY IF EXISTS tenant_isolation ON user_preferences;
                DROP POLICY IF EXISTS tenant_isolation ON project_memberships;
                """);

            migrationBuilder.DropTable(
                name: "project_memberships");

            migrationBuilder.DropTable(
                name: "tenant_users");

            migrationBuilder.DropTable(
                name: "user_preferences");

            migrationBuilder.DropIndex(
                name: "ix_dubbing_projects_tenant_id_is_archived",
                table: "dubbing_projects");

            migrationBuilder.DropIndex(
                name: "ix_dubbing_projects_tenant_id_owner_user_id",
                table: "dubbing_projects");

            migrationBuilder.DropColumn(
                name: "archived_at",
                table: "dubbing_projects");

            migrationBuilder.DropColumn(
                name: "created_by_user_id",
                table: "dubbing_projects");

            migrationBuilder.DropColumn(
                name: "description",
                table: "dubbing_projects");

            migrationBuilder.DropColumn(
                name: "is_archived",
                table: "dubbing_projects");

            migrationBuilder.DropColumn(
                name: "name",
                table: "dubbing_projects");

            migrationBuilder.DropColumn(
                name: "owner_user_id",
                table: "dubbing_projects");

            migrationBuilder.DropColumn(
                name: "processing_settings_json",
                table: "dubbing_projects");

            migrationBuilder.DropColumn(
                name: "settings_version",
                table: "dubbing_projects");

            migrationBuilder.DropColumn(
                name: "updated_by_user_id",
                table: "dubbing_projects");
        }
    }
}
