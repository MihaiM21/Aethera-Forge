using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aethera.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddR1BackupsNotificationsTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_secrets_purpose",
                table: "secrets");

            migrationBuilder.AddColumn<Dictionary<string, string>>(
                name: "custom_labels",
                table: "workloads",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AddColumn<Guid>(
                name: "basic_auth_secret_id",
                table: "domains",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "compose_service",
                table: "domains",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "dns_provider_id",
                table: "domains",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "redirect_to",
                table: "domains",
                type: "character varying(253)",
                maxLength: 253,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "strip_path_prefix",
                table: "domains",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "alerts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    severity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    dedup_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    resource_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_notified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    notification_count = table.Column<int>(type: "integer", nullable: false),
                    acknowledged_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alerts", x => x.id);
                    table.ForeignKey(
                        name: "fk_alerts_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "backup_destinations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    endpoint = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    region = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    bucket = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    path_prefix = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    access_key_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    secret_access_key_secret_id = table.Column<Guid>(type: "uuid", nullable: true),
                    force_path_style = table.Column<bool>(type: "boolean", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    last_tested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_test_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backup_destinations", x => x.id);
                    table.CheckConstraint("ck_backup_destinations_s3", "kind <> 's3' OR (endpoint IS NOT NULL AND bucket IS NOT NULL AND access_key_id IS NOT NULL AND secret_access_key_secret_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_backup_destinations_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_backup_destinations_secrets_secret_access_key_secret_id",
                        column: x => x.secret_access_key_secret_id,
                        principalTable: "secrets",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "dns_providers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    api_token_secret_id = table.Column<Guid>(type: "uuid", nullable: false),
                    zones = table.Column<List<string>>(type: "text[]", nullable: false),
                    last_verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dns_providers", x => x.id);
                    table.ForeignKey(
                        name: "fk_dns_providers_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dns_providers_secrets_api_token_secret_id",
                        column: x => x.api_token_secret_id,
                        principalTable: "secrets",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "email_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    transport = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    smtp_host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    smtp_port = table.Column<int>(type: "integer", nullable: true),
                    smtp_encryption = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    smtp_username = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    smtp_password_secret_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resend_api_key_secret_id = table.Column<Guid>(type: "uuid", nullable: true),
                    from_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    from_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    last_tested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_test_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_settings", x => x.id);
                    table.CheckConstraint("ck_email_settings_port", "smtp_port IS NULL OR smtp_port BETWEEN 1 AND 65535");
                    table.ForeignKey(
                        name: "fk_email_settings_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_email_settings_secrets_resend_api_key_secret_id",
                        column: x => x.resend_api_key_secret_id,
                        principalTable: "secrets",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_email_settings_secrets_smtp_password_secret_id",
                        column: x => x.smtp_password_secret_id,
                        principalTable: "secrets",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "notification_channels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    send_to_members = table.Column<bool>(type: "boolean", nullable: false),
                    email_recipients = table.Column<List<string>>(type: "text[]", nullable: false),
                    webhook_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    webhook_secret_id = table.Column<Guid>(type: "uuid", nullable: true),
                    events = table.Column<List<string>>(type: "text[]", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_channels", x => x.id);
                    table.CheckConstraint("ck_notification_channels_webhook", "kind <> 'webhook' OR (webhook_url IS NOT NULL AND webhook_secret_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_notification_channels_environments_environment_id",
                        column: x => x.environment_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_notification_channels_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_notification_channels_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_notification_channels_secrets_webhook_secret_id",
                        column: x => x.webhook_secret_id,
                        principalTable: "secrets",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "scheduled_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workload_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    command = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    compose_service = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    schedule = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    time_zone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    next_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scheduled_tasks", x => x.id);
                    table.CheckConstraint("ck_scheduled_tasks_timeout", "timeout_seconds BETWEEN 1 AND 86400");
                    table.ForeignKey(
                        name: "fk_scheduled_tasks_workloads_workload_id",
                        column: x => x.workload_id,
                        principalTable: "workloads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "backup_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workload_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    volume_id = table.Column<Guid>(type: "uuid", nullable: true),
                    destination_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    schedule = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    time_zone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    keep_daily = table.Column<int>(type: "integer", nullable: false),
                    keep_weekly = table.Column<int>(type: "integer", nullable: false),
                    keep_monthly = table.Column<int>(type: "integer", nullable: false),
                    next_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backup_policies", x => x.id);
                    table.CheckConstraint("ck_backup_policies_retention", "keep_daily >= 0 AND keep_weekly >= 0 AND keep_monthly >= 0 AND keep_daily + keep_weekly + keep_monthly >= 1");
                    table.CheckConstraint("ck_backup_policies_target", "(kind = 'controlPlane' AND workload_id IS NULL AND volume_id IS NULL) OR (kind = 'database' AND workload_id IS NOT NULL AND volume_id IS NULL) OR (kind = 'volume' AND workload_id IS NOT NULL AND volume_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_backup_policies_backup_destinations_destination_id",
                        column: x => x.destination_id,
                        principalTable: "backup_destinations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_backup_policies_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_backup_policies_volumes_volume_id",
                        column: x => x.volume_id,
                        principalTable: "volumes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_backup_policies_workloads_workload_id",
                        column: x => x.workload_id,
                        principalTable: "workloads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_deliveries", x => x.id);
                    table.ForeignKey(
                        name: "fk_notification_deliveries_alerts_alert_id",
                        column: x => x.alert_id,
                        principalTable: "alerts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_notification_deliveries_notification_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "notification_channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scheduled_task_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workload_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    exit_code = table.Column<int>(type: "integer", nullable: true),
                    output_tail = table.Column<string>(type: "character varying(70000)", maxLength: 70000, nullable: true),
                    output_truncated = table.Column<bool>(type: "boolean", nullable: false),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scheduled_task_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_scheduled_task_runs_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_scheduled_task_runs_scheduled_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "scheduled_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "backup_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workload_id = table.Column<Guid>(type: "uuid", nullable: true),
                    destination_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    engine = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    object_key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    raw_size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    stored_size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    stored_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    pruned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backup_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_backup_runs_backup_destinations_destination_id",
                        column: x => x.destination_id,
                        principalTable: "backup_destinations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_backup_runs_backup_policies_policy_id",
                        column: x => x.policy_id,
                        principalTable: "backup_policies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_backup_runs_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_secrets_purpose",
                table: "secrets",
                sql: "purpose IN ('user', 'registryCredential', 'sshCredential', 'gitCredential', 'serviceGenerated', 'backupCredential', 'notificationCredential', 'dnsCredential', 'basicAuth')");

            migrationBuilder.CreateIndex(
                name: "ix_domains_basic_auth_secret_id",
                table: "domains",
                column: "basic_auth_secret_id");

            migrationBuilder.CreateIndex(
                name: "ix_domains_dns_provider_id",
                table: "domains",
                column: "dns_provider_id");

            migrationBuilder.CreateIndex(
                name: "ix_alerts_organization_id_dedup_key",
                table: "alerts",
                columns: new[] { "organization_id", "dedup_key" },
                unique: true,
                filter: "state = 'open'");

            migrationBuilder.CreateIndex(
                name: "ix_alerts_organization_id_state_opened_at",
                table: "alerts",
                columns: new[] { "organization_id", "state", "opened_at" });

            migrationBuilder.CreateIndex(
                name: "ix_alerts_resource_type_resource_id",
                table: "alerts",
                columns: new[] { "resource_type", "resource_id" });

            migrationBuilder.CreateIndex(
                name: "ix_backup_destinations_organization_id_name",
                table: "backup_destinations",
                columns: new[] { "organization_id", "name" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_backup_destinations_secret_access_key_secret_id",
                table: "backup_destinations",
                column: "secret_access_key_secret_id");

            migrationBuilder.CreateIndex(
                name: "ix_backup_policies_destination_id",
                table: "backup_policies",
                column: "destination_id");

            migrationBuilder.CreateIndex(
                name: "ix_backup_policies_next_run_at",
                table: "backup_policies",
                column: "next_run_at",
                filter: "enabled");

            migrationBuilder.CreateIndex(
                name: "ix_backup_policies_organization_id_workload_id_kind_volume_id",
                table: "backup_policies",
                columns: new[] { "organization_id", "workload_id", "kind", "volume_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_backup_policies_volume_id",
                table: "backup_policies",
                column: "volume_id");

            migrationBuilder.CreateIndex(
                name: "ix_backup_policies_workload_id",
                table: "backup_policies",
                column: "workload_id");

            migrationBuilder.CreateIndex(
                name: "ix_backup_runs_destination_id",
                table: "backup_runs",
                column: "destination_id");

            migrationBuilder.CreateIndex(
                name: "ix_backup_runs_job_id",
                table: "backup_runs",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_backup_runs_organization_id_created_at",
                table: "backup_runs",
                columns: new[] { "organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_backup_runs_policy_id_created_at",
                table: "backup_runs",
                columns: new[] { "policy_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_backup_runs_workload_id",
                table: "backup_runs",
                column: "workload_id");

            migrationBuilder.CreateIndex(
                name: "ix_dns_providers_api_token_secret_id",
                table: "dns_providers",
                column: "api_token_secret_id");

            migrationBuilder.CreateIndex(
                name: "ix_dns_providers_organization_id_name",
                table: "dns_providers",
                columns: new[] { "organization_id", "name" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_email_settings_organization_id",
                table: "email_settings",
                column: "organization_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_email_settings_resend_api_key_secret_id",
                table: "email_settings",
                column: "resend_api_key_secret_id");

            migrationBuilder.CreateIndex(
                name: "ix_email_settings_smtp_password_secret_id",
                table: "email_settings",
                column: "smtp_password_secret_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_channels_environment_id",
                table: "notification_channels",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_channels_organization_id_name",
                table: "notification_channels",
                columns: new[] { "organization_id", "name" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_notification_channels_project_id",
                table: "notification_channels",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_channels_webhook_secret_id",
                table: "notification_channels",
                column: "webhook_secret_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_alert_id",
                table: "notification_deliveries",
                column: "alert_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_channel_id_created_at",
                table: "notification_deliveries",
                columns: new[] { "channel_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_organization_id_created_at",
                table: "notification_deliveries",
                columns: new[] { "organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_scheduled_task_runs_job_id",
                table: "scheduled_task_runs",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_scheduled_task_runs_task_id_created_at",
                table: "scheduled_task_runs",
                columns: new[] { "task_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_scheduled_task_runs_workload_id",
                table: "scheduled_task_runs",
                column: "workload_id");

            migrationBuilder.CreateIndex(
                name: "ix_scheduled_tasks_next_run_at",
                table: "scheduled_tasks",
                column: "next_run_at",
                filter: "enabled AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_scheduled_tasks_workload_id_name",
                table: "scheduled_tasks",
                columns: new[] { "workload_id", "name" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_domains_dns_providers_dns_provider_id",
                table: "domains",
                column: "dns_provider_id",
                principalTable: "dns_providers",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_domains_secrets_basic_auth_secret_id",
                table: "domains",
                column: "basic_auth_secret_id",
                principalTable: "secrets",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_domains_dns_providers_dns_provider_id",
                table: "domains");

            migrationBuilder.DropForeignKey(
                name: "fk_domains_secrets_basic_auth_secret_id",
                table: "domains");

            migrationBuilder.DropTable(
                name: "backup_runs");

            migrationBuilder.DropTable(
                name: "dns_providers");

            migrationBuilder.DropTable(
                name: "email_settings");

            migrationBuilder.DropTable(
                name: "notification_deliveries");

            migrationBuilder.DropTable(
                name: "scheduled_task_runs");

            migrationBuilder.DropTable(
                name: "backup_policies");

            migrationBuilder.DropTable(
                name: "alerts");

            migrationBuilder.DropTable(
                name: "notification_channels");

            migrationBuilder.DropTable(
                name: "scheduled_tasks");

            migrationBuilder.DropTable(
                name: "backup_destinations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_secrets_purpose",
                table: "secrets");

            migrationBuilder.DropIndex(
                name: "ix_domains_basic_auth_secret_id",
                table: "domains");

            migrationBuilder.DropIndex(
                name: "ix_domains_dns_provider_id",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "custom_labels",
                table: "workloads");

            migrationBuilder.DropColumn(
                name: "basic_auth_secret_id",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "compose_service",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "dns_provider_id",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "redirect_to",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "strip_path_prefix",
                table: "domains");

            migrationBuilder.AddCheckConstraint(
                name: "ck_secrets_purpose",
                table: "secrets",
                sql: "purpose IN ('user', 'registryCredential', 'sshCredential', 'gitCredential', 'serviceGenerated')");
        }
    }
}
