using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aethera.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddR2MultiServerMeshPreviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_environment_variables_workload_id_key",
                table: "environment_variables");

            migrationBuilder.AddColumn<Guid>(
                name: "build_server_id",
                table: "workloads",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "preview_allow_forks",
                table: "workloads",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "preview_max_active",
                table: "workloads",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "preview_server_id",
                table: "workloads",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "previews_enabled",
                table: "workloads",
                type: "boolean",
                nullable: true);

            // Applications that existed before R2 have previews off (the complex property cannot read null).
            migrationBuilder.Sql("UPDATE workloads SET previews_enabled = false, preview_allow_forks = false WHERE kind = 'application';");

            migrationBuilder.AddColumn<string>(
                name: "priority",
                table: "workloads",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "normal");

            migrationBuilder.AddColumn<string>(
                name: "bind_scope",
                table: "workload_ports",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "public");

            migrationBuilder.AddColumn<double>(
                name: "build_cpu_limit",
                table: "servers",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "build_memory_limit_bytes",
                table: "servers",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "build_registry_id",
                table: "servers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mesh_address",
                table: "servers",
                type: "character varying(45)",
                maxLength: 45,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mesh_endpoint",
                table: "servers",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mesh_error",
                table: "servers",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "mesh_handshake_at",
                table: "servers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "mesh_listen_port",
                table: "servers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mesh_provider",
                table: "servers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "none");

            migrationBuilder.AddColumn<string>(
                name: "mesh_public_key",
                table: "servers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mesh_status",
                table: "servers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "disabled");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "mesh_status_changed_at",
                table: "servers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "namespace",
                table: "registries",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_preview",
                table: "environment_variables",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "build_server_id",
                table: "deployments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "preview_id",
                table: "deployments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "previews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    author_login = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    source_branch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    source_repository_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    from_fork = table.Column<bool>(type: "boolean", nullable: false),
                    head_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    hostname = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status_reason = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    status_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    current_deployment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    provider_comment_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    comment_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_previews", x => x.id);
                    table.CheckConstraint("ck_previews_number", "number >= 1");
                    table.ForeignKey(
                        name: "fk_previews_applications_application_id",
                        column: x => x.application_id,
                        principalTable: "workloads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_previews_deployments_current_deployment_id",
                        column: x => x.current_deployment_id,
                        principalTable: "deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_previews_servers_server_id",
                        column: x => x.server_id,
                        principalTable: "servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_workloads_build_server_id",
                table: "workloads",
                column: "build_server_id");

            migrationBuilder.CreateIndex(
                name: "ix_servers_build_registry_id",
                table: "servers",
                column: "build_registry_id");

            migrationBuilder.CreateIndex(
                name: "ix_servers_mesh_address",
                table: "servers",
                column: "mesh_address",
                unique: true,
                filter: "mesh_address IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_servers_build_quota",
                table: "servers",
                sql: "(build_cpu_limit IS NULL OR build_cpu_limit > 0) AND (build_memory_limit_bytes IS NULL OR build_memory_limit_bytes > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_servers_mesh_listen_port",
                table: "servers",
                sql: "mesh_listen_port IS NULL OR mesh_listen_port BETWEEN 1 AND 65535");

            migrationBuilder.CreateIndex(
                name: "ix_environment_variables_workload_id_key_is_preview",
                table: "environment_variables",
                columns: new[] { "workload_id", "key", "is_preview" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deployments_build_server_id",
                table: "deployments",
                column: "build_server_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployments_preview_id",
                table: "deployments",
                column: "preview_id");

            migrationBuilder.CreateIndex(
                name: "ix_previews_application_id_number",
                table: "previews",
                columns: new[] { "application_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_previews_current_deployment_id",
                table: "previews",
                column: "current_deployment_id");

            migrationBuilder.CreateIndex(
                name: "ix_previews_hostname",
                table: "previews",
                column: "hostname",
                unique: true,
                filter: "hostname IS NOT NULL AND removed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_previews_server_id",
                table: "previews",
                column: "server_id");

            migrationBuilder.CreateIndex(
                name: "ix_previews_status_last_event_at",
                table: "previews",
                columns: new[] { "status", "last_event_at" });

            migrationBuilder.AddForeignKey(
                name: "fk_deployments_previews_preview_id",
                table: "deployments",
                column: "preview_id",
                principalTable: "previews",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_deployments_servers_build_server_id",
                table: "deployments",
                column: "build_server_id",
                principalTable: "servers",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_servers_registries_build_registry_id",
                table: "servers",
                column: "build_registry_id",
                principalTable: "registries",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_workloads_servers_build_server_id",
                table: "workloads",
                column: "build_server_id",
                principalTable: "servers",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_deployments_previews_preview_id",
                table: "deployments");

            migrationBuilder.DropForeignKey(
                name: "fk_deployments_servers_build_server_id",
                table: "deployments");

            migrationBuilder.DropForeignKey(
                name: "fk_servers_registries_build_registry_id",
                table: "servers");

            migrationBuilder.DropForeignKey(
                name: "fk_workloads_servers_build_server_id",
                table: "workloads");

            migrationBuilder.DropTable(
                name: "previews");

            migrationBuilder.DropIndex(
                name: "ix_workloads_build_server_id",
                table: "workloads");

            migrationBuilder.DropIndex(
                name: "ix_servers_build_registry_id",
                table: "servers");

            migrationBuilder.DropIndex(
                name: "ix_servers_mesh_address",
                table: "servers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_servers_build_quota",
                table: "servers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_servers_mesh_listen_port",
                table: "servers");

            migrationBuilder.DropIndex(
                name: "ix_environment_variables_workload_id_key_is_preview",
                table: "environment_variables");

            migrationBuilder.DropIndex(
                name: "ix_deployments_build_server_id",
                table: "deployments");

            migrationBuilder.DropIndex(
                name: "ix_deployments_preview_id",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "build_server_id",
                table: "workloads");

            migrationBuilder.DropColumn(
                name: "preview_allow_forks",
                table: "workloads");

            migrationBuilder.DropColumn(
                name: "preview_max_active",
                table: "workloads");

            migrationBuilder.DropColumn(
                name: "preview_server_id",
                table: "workloads");

            migrationBuilder.DropColumn(
                name: "previews_enabled",
                table: "workloads");

            migrationBuilder.DropColumn(
                name: "priority",
                table: "workloads");

            migrationBuilder.DropColumn(
                name: "bind_scope",
                table: "workload_ports");

            migrationBuilder.DropColumn(
                name: "build_cpu_limit",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "build_memory_limit_bytes",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "build_registry_id",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "mesh_address",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "mesh_endpoint",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "mesh_error",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "mesh_handshake_at",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "mesh_listen_port",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "mesh_provider",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "mesh_public_key",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "mesh_status",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "mesh_status_changed_at",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "namespace",
                table: "registries");

            migrationBuilder.DropColumn(
                name: "is_preview",
                table: "environment_variables");

            migrationBuilder.DropColumn(
                name: "build_server_id",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "preview_id",
                table: "deployments");

            migrationBuilder.CreateIndex(
                name: "ix_environment_variables_workload_id_key",
                table: "environment_variables",
                columns: new[] { "workload_id", "key" },
                unique: true);
        }
    }
}
