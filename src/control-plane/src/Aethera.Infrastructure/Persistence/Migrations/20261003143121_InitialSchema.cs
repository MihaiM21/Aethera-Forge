using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Aethera.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "certificate_authorities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    certificate_pem = table.Column<string>(type: "text", nullable: false),
                    fingerprint_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    key_algorithm = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    not_before = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    not_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    private_key_ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    private_key_nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    wrapped_data_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    wrapped_data_key_nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    master_key_version = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_certificate_authorities", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                columns: table => new
                {
                    principal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    request_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    response_status = table.Column<int>(type: "integer", nullable: true),
                    response_headers = table.Column<string>(type: "jsonb", nullable: true),
                    response_body = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_records", x => new { x.principal_id, x.key });
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    resource_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lock_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    result = table.Column<string>(type: "jsonb", nullable: true),
                    error = table.Column<string>(type: "jsonb", nullable: true),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    retry_no = table.Column<int>(type: "integer", nullable: false),
                    run_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    locked_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    parent_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_jobs", x => x.id);
                    table.CheckConstraint("ck_jobs_attempts", "attempt >= 0 AND max_attempts >= 1 AND retry_no >= 0");
                    table.ForeignKey(
                        name: "fk_jobs_jobs_parent_job_id",
                        column: x => x.parent_job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "log_chunks",
                columns: table => new
                {
                    stream_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    ts = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source = table.Column<short>(type: "smallint", nullable: false),
                    stream = table.Column<short>(type: "smallint", nullable: false),
                    data = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_log_chunks", x => new { x.stream_id, x.sequence });
                });

            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organizations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "resource_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    axis = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    old_value = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    new_value = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_resource_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    normalized_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_login_count = table.Column<int>(type: "integer", nullable: false),
                    lockout_end_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_api_token_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_label = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    resource_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resource_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: false),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    request_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_audit_events_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    template_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_projects", x => x.id);
                    table.ForeignKey(
                        name: "fk_projects_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "teams",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_teams", x => x.id);
                    table.ForeignKey(
                        name: "fk_teams_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "api_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    prefix = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    secret_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    scopes = table.Column<List<string>>(type: "text[]", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_tokens", x => x.id);
                    table.CheckConstraint("ck_api_tokens_secret_hash_len", "octet_length(secret_hash) = 32");
                    table.ForeignKey(
                        name: "fk_api_tokens_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_api_tokens_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "organization_members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organization_members", x => x.id);
                    table.ForeignKey(
                        name: "fk_organization_members_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_organization_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "settings",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    value = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settings", x => x.key);
                    table.ForeignKey(
                        name: "fk_settings_users_updated_by_user_id",
                        column: x => x.updated_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "user_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    secret_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_sessions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "environments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    is_production = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_environments", x => x.id);
                    table.ForeignKey(
                        name: "fk_environments_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_team_members", x => x.id);
                    table.ForeignKey(
                        name: "fk_team_members_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_team_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_certificates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    certificate_authority_id = table.Column<Guid>(type: "uuid", nullable: false),
                    serial = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    fingerprint_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    subject_uri = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    not_before = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    not_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_certificates", x => x.id);
                    table.ForeignKey(
                        name: "fk_agent_certificates_certificate_authorities_certificate_auth",
                        column: x => x.certificate_authority_id,
                        principalTable: "certificate_authorities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "build_configs",
                columns: table => new
                {
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engine = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    context = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    dockerfile_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    dockerfile_inline = table.Column<string>(type: "text", nullable: true),
                    install_command = table.Column<string>(type: "text", nullable: true),
                    build_command = table.Column<string>(type: "text", nullable: true),
                    start_command = table.Column<string>(type: "text", nullable: true),
                    output_directory = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    build_args = table.Column<Dictionary<string, string>>(type: "jsonb", nullable: false),
                    cache_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    target_platform = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_build_configs", x => x.application_id);
                });

            migrationBuilder.CreateTable(
                name: "builds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    engine = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    platform = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    duration_ms = table.Column<long>(type: "bigint", nullable: true),
                    cache_hit = table.Column<bool>(type: "boolean", nullable: true),
                    cache_hit_layers = table.Column<int>(type: "integer", nullable: true),
                    cache_total_layers = table.Column<int>(type: "integer", nullable: true),
                    commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    result_image = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    result_image_digest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    result_image_size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_builds", x => x.id);
                    table.ForeignKey(
                        name: "fk_builds_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "compose_sources",
                columns: table => new
                {
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    inline_content = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_compose_sources", x => x.application_id);
                    table.CheckConstraint("ck_compose_sources_content", "file_path IS NOT NULL OR inline_content IS NOT NULL");
                });

            migrationBuilder.CreateTable(
                name: "deployment_steps",
                columns: table => new
                {
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    error_message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_steps", x => new { x.deployment_id, x.step });
                });

            migrationBuilder.CreateTable(
                name: "deployments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workload_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    number = table.Column<int>(type: "integer", nullable: false),
                    trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    strategy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    current_step = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    failed_step = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    failure_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    source_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    repository_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    @ref = table.Column<string>(name: "ref", type: "character varying(255)", maxLength: 255, nullable: true),
                    commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    commit_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    commit_author = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    build_id = table.Column<Guid>(type: "uuid", nullable: true),
                    image_ref = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    image_digest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    container_ids = table.Column<List<string>>(type: "text[]", nullable: false),
                    config_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    health_check = table.Column<string>(type: "jsonb", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_rollback_point = table.Column<bool>(type: "boolean", nullable: false),
                    rollback_of_deployment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    triggered_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    triggered_by_api_token_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployments", x => x.id);
                    table.CheckConstraint("ck_deployments_number", "number >= 1");
                    table.ForeignKey(
                        name: "fk_deployments_api_tokens_triggered_by_api_token_id",
                        column: x => x.triggered_by_api_token_id,
                        principalTable: "api_tokens",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_deployments_builds_build_id",
                        column: x => x.build_id,
                        principalTable: "builds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_deployments_deployments_rollback_of_deployment_id",
                        column: x => x.rollback_of_deployment_id,
                        principalTable: "deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_deployments_environments_environment_id",
                        column: x => x.environment_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_deployments_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_deployments_users_triggered_by_user_id",
                        column: x => x.triggered_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "domains",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workload_id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hostname = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    path_prefix = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    https_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    target_port = table.Column<int>(type: "integer", nullable: true),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    certificate_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    certificate_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    certificate_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    dns_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    dns_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dns_resolved_ips = table.Column<List<string>>(type: "text[]", nullable: false),
                    proxy_route_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_domains", x => x.id);
                    table.CheckConstraint("ck_domains_path_prefix", "path_prefix LIKE '/%'");
                });

            migrationBuilder.CreateTable(
                name: "environment_variables",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workload_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    value = table.Column<string>(type: "text", nullable: true),
                    secret_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_build_time = table.Column<bool>(type: "boolean", nullable: false),
                    is_runtime = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_environment_variables", x => x.id);
                    table.CheckConstraint("ck_environment_variables_value_or_secret", "value IS NULL OR secret_id IS NULL");
                });

            migrationBuilder.CreateTable(
                name: "git_credentials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    username = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    public_key = table.Column<string>(type: "text", nullable: true),
                    secret_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_git_credentials", x => x.id);
                    table.ForeignKey(
                        name: "fk_git_credentials_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "git_sources",
                columns: table => new
                {
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    repository_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    branch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    commit_pin = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    git_credential_id = table.Column<Guid>(type: "uuid", nullable: true),
                    auto_deploy = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_git_sources", x => x.application_id);
                    table.ForeignKey(
                        name: "fk_git_sources_git_credentials_git_credential_id",
                        column: x => x.git_credential_id,
                        principalTable: "git_credentials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "image_sources",
                columns: table => new
                {
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    registry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    image = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    tag = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    pull_policy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_image_sources", x => x.application_id);
                });

            migrationBuilder.CreateTable(
                name: "images",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workload_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    tag = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    digest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    image_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_images", x => x.id);
                    table.ForeignKey(
                        name: "fk_images_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalTable: "deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "join_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_join_tokens", x => x.id);
                    table.CheckConstraint("ck_join_tokens_hash_len", "octet_length(token_hash) = 32");
                    table.CheckConstraint("ck_join_tokens_ttl", "expires_at > created_at AND expires_at <= created_at + interval '24 hours'");
                    table.ForeignKey(
                        name: "fk_join_tokens_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "metric_samples",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    container_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    workload_id = table.Column<Guid>(type: "uuid", nullable: true),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolution = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    cpu_percent = table.Column<double>(type: "double precision", nullable: true),
                    memory_used_bytes = table.Column<long>(type: "bigint", nullable: true),
                    memory_total_bytes = table.Column<long>(type: "bigint", nullable: true),
                    disk_used_bytes = table.Column<long>(type: "bigint", nullable: true),
                    disk_total_bytes = table.Column<long>(type: "bigint", nullable: true),
                    net_rx_bytes = table.Column<long>(type: "bigint", nullable: true),
                    net_tx_bytes = table.Column<long>(type: "bigint", nullable: true),
                    load1 = table.Column<double>(type: "double precision", nullable: true),
                    load5 = table.Column<double>(type: "double precision", nullable: true),
                    load15 = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_metric_samples", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "networks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    docker_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    is_internal = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_networks", x => x.id);
                    table.ForeignKey(
                        name: "fk_networks_environments_environment_id",
                        column: x => x.environment_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_networks_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "registries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    username = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    password_secret_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_registries", x => x.id);
                    table.ForeignKey(
                        name: "fk_registries_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "secret_versions",
                columns: table => new
                {
                    secret_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    wrapped_data_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    wrapped_data_key_nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    master_key_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_secret_versions", x => new { x.secret_id, x.version });
                    table.CheckConstraint("ck_secret_versions_version", "version >= 1");
                });

            migrationBuilder.CreateTable(
                name: "secrets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    workload_id = table.Column<Guid>(type: "uuid", nullable: true),
                    current_version = table.Column<int>(type: "integer", nullable: false),
                    rotated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_secrets", x => x.id);
                    table.CheckConstraint("ck_secrets_single_scope", "num_nonnulls(project_id, environment_id, workload_id) <= 1");
                    table.ForeignKey(
                        name: "fk_secrets_environments_environment_id",
                        column: x => x.environment_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_secrets_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_secrets_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "servers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    ssh_port = table.Column<int>(type: "integer", nullable: false),
                    ssh_user = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ssh_credential_secret_id = table.Column<Guid>(type: "uuid", nullable: true),
                    transport = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    roles = table.Column<string[]>(type: "text[]", nullable: false),
                    lifecycle = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reachability_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reachability_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reachability_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reachability_probe_port = table.Column<int>(type: "integer", nullable: true),
                    agent_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    agent_status_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    docker_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    docker_status_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    max_concurrent_builds = table.Column<int>(type: "integer", nullable: false),
                    ssh_host_key_fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    public_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    agent_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    cert_fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    cert_serial = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    cert_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    architecture = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    cpu_cores = table.Column<int>(type: "integer", nullable: true),
                    cpu_model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    facts_discovered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    disk_bytes = table.Column<long>(type: "bigint", nullable: true),
                    docker_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    kernel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    memory_bytes = table.Column<long>(type: "bigint", nullable: true),
                    os = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    os_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_servers", x => x.id);
                    table.CheckConstraint("ck_servers_max_concurrent_builds", "max_concurrent_builds >= 1");
                    table.CheckConstraint("ck_servers_ssh_port", "ssh_port BETWEEN 1 AND 65535");
                    table.ForeignKey(
                        name: "fk_servers_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_servers_secrets_ssh_credential_secret_id",
                        column: x => x.ssh_credential_secret_id,
                        principalTable: "secrets",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "workloads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    desired_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status_reason = table.Column<string>(type: "text", nullable: true),
                    current_deployment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deployment_sequence = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    cpu_limit = table.Column<double>(type: "double precision", nullable: true),
                    cpu_reservation = table.Column<double>(type: "double precision", nullable: true),
                    deployment_strategy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    memory_limit_bytes = table.Column<long>(type: "bigint", nullable: true),
                    memory_reservation_bytes = table.Column<long>(type: "bigint", nullable: true),
                    pids_limit = table.Column<int>(type: "integer", nullable: true),
                    restart_policy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    health_check_interval_seconds = table.Column<int>(type: "integer", nullable: false),
                    health_check_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    health_check_port = table.Column<int>(type: "integer", nullable: true),
                    health_check_retries = table.Column<int>(type: "integer", nullable: false),
                    health_check_start_period_seconds = table.Column<int>(type: "integer", nullable: false),
                    health_check_timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    health_check_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    template_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    template_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    image = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    config = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workloads", x => x.id);
                    table.CheckConstraint("ck_workloads_application_columns", "kind <> 'application' OR (source_kind IS NOT NULL AND template_key IS NULL AND image IS NULL)");
                    table.CheckConstraint("ck_workloads_service_columns", "kind <> 'service' OR (template_key IS NOT NULL AND image IS NOT NULL AND source_kind IS NULL)");
                    table.ForeignKey(
                        name: "fk_workloads_deployments_current_deployment_id",
                        column: x => x.current_deployment_id,
                        principalTable: "deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_workloads_environments_environment_id",
                        column: x => x.environment_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_workloads_servers_server_id",
                        column: x => x.server_id,
                        principalTable: "servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "volumes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workload_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    mount_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    host_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    read_only = table.Column<bool>(type: "boolean", nullable: false),
                    backup_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_volumes", x => x.id);
                    table.ForeignKey(
                        name: "fk_volumes_workloads_workload_id",
                        column: x => x.workload_id,
                        principalTable: "workloads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "webhook_endpoints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workload_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    secret_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    branch_filter = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    last_delivery_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_endpoints", x => x.id);
                    table.ForeignKey(
                        name: "fk_webhook_endpoints_secrets_secret_id",
                        column: x => x.secret_id,
                        principalTable: "secrets",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_webhook_endpoints_workloads_workload_id",
                        column: x => x.workload_id,
                        principalTable: "workloads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workload_networks",
                columns: table => new
                {
                    workload_id = table.Column<Guid>(type: "uuid", nullable: false),
                    network_id = table.Column<Guid>(type: "uuid", nullable: false),
                    aliases = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workload_networks", x => new { x.workload_id, x.network_id });
                    table.ForeignKey(
                        name: "fk_workload_networks_networks_network_id",
                        column: x => x.network_id,
                        principalTable: "networks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_workload_networks_workloads_workload_id",
                        column: x => x.workload_id,
                        principalTable: "workloads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workload_ports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workload_id = table.Column<Guid>(type: "uuid", nullable: false),
                    container_port = table.Column<int>(type: "integer", nullable: false),
                    protocol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    published_port = table.Column<int>(type: "integer", nullable: true),
                    is_http = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workload_ports", x => x.id);
                    table.CheckConstraint("ck_workload_ports_container_port", "container_port BETWEEN 1 AND 65535");
                    table.CheckConstraint("ck_workload_ports_published_port", "published_port IS NULL OR published_port BETWEEN 1 AND 65535");
                    table.ForeignKey(
                        name: "fk_workload_ports_workloads_workload_id",
                        column: x => x.workload_id,
                        principalTable: "workloads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivery_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    @ref = table.Column<string>(name: "ref", type: "character varying(255)", maxLength: 255, nullable: true),
                    commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    signature_valid = table.Column<bool>(type: "boolean", nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_deliveries", x => x.id);
                    table.ForeignKey(
                        name: "fk_webhook_deliveries_webhook_endpoints_endpoint_id",
                        column: x => x.endpoint_id,
                        principalTable: "webhook_endpoints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_certificates_certificate_authority_id",
                table: "agent_certificates",
                column: "certificate_authority_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_certificates_not_after",
                table: "agent_certificates",
                column: "not_after");

            migrationBuilder.CreateIndex(
                name: "ix_agent_certificates_serial",
                table: "agent_certificates",
                column: "serial",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agent_certificates_server_id",
                table: "agent_certificates",
                column: "server_id");

            migrationBuilder.CreateIndex(
                name: "ix_api_tokens_created_by_user_id",
                table: "api_tokens",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_api_tokens_organization_id",
                table: "api_tokens",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_api_tokens_prefix",
                table: "api_tokens",
                column: "prefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_actor_user_id",
                table: "audit_events",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_organization_id_occurred_at",
                table: "audit_events",
                columns: new[] { "organization_id", "occurred_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_resource_type_resource_id",
                table: "audit_events",
                columns: new[] { "resource_type", "resource_id" });

            migrationBuilder.CreateIndex(
                name: "ix_builds_deployment_id_attempt",
                table: "builds",
                columns: new[] { "deployment_id", "attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_builds_job_id",
                table: "builds",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_certificate_authorities_fingerprint_sha256",
                table: "certificate_authorities",
                column: "fingerprint_sha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_certificate_authorities_is_active",
                table: "certificate_authorities",
                column: "is_active",
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_deployments_build_id",
                table: "deployments",
                column: "build_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployments_environment_id",
                table: "deployments",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployments_job_id",
                table: "deployments",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployments_rollback_of_deployment_id",
                table: "deployments",
                column: "rollback_of_deployment_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployments_running",
                table: "deployments",
                column: "workload_id",
                filter: "status = 'running'");

            migrationBuilder.CreateIndex(
                name: "ix_deployments_server_id_status",
                table: "deployments",
                columns: new[] { "server_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_deployments_triggered_by_api_token_id",
                table: "deployments",
                column: "triggered_by_api_token_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployments_triggered_by_user_id",
                table: "deployments",
                column: "triggered_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployments_workload_id_created_at",
                table: "deployments",
                columns: new[] { "workload_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_deployments_workload_id_number",
                table: "deployments",
                columns: new[] { "workload_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deployments_workload_id_number1",
                table: "deployments",
                columns: new[] { "workload_id", "number" },
                filter: "is_rollback_point");

            migrationBuilder.CreateIndex(
                name: "ix_domains_hostname_path_prefix",
                table: "domains",
                columns: new[] { "hostname", "path_prefix" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_domains_server_id",
                table: "domains",
                column: "server_id");

            migrationBuilder.CreateIndex(
                name: "ix_domains_workload_id",
                table: "domains",
                column: "workload_id");

            migrationBuilder.CreateIndex(
                name: "ix_environment_variables_secret_id",
                table: "environment_variables",
                column: "secret_id");

            migrationBuilder.CreateIndex(
                name: "ix_environment_variables_workload_id_key",
                table: "environment_variables",
                columns: new[] { "workload_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_environments_project_id_slug",
                table: "environments",
                columns: new[] { "project_id", "slug" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_git_credentials_organization_id_name",
                table: "git_credentials",
                columns: new[] { "organization_id", "name" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_git_credentials_secret_id",
                table: "git_credentials",
                column: "secret_id");

            migrationBuilder.CreateIndex(
                name: "ix_git_sources_git_credential_id",
                table: "git_sources",
                column: "git_credential_id");

            migrationBuilder.CreateIndex(
                name: "ix_git_sources_repository_url",
                table: "git_sources",
                column: "repository_url");

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_records_expires_at",
                table: "idempotency_records",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_image_sources_registry_id",
                table: "image_sources",
                column: "registry_id");

            migrationBuilder.CreateIndex(
                name: "ix_images_deployment_id",
                table: "images",
                column: "deployment_id");

            migrationBuilder.CreateIndex(
                name: "ix_images_server_id_created_at",
                table: "images",
                columns: new[] { "server_id", "created_at" },
                filter: "removed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_images_workload_id_created_at",
                table: "images",
                columns: new[] { "workload_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_claim",
                table: "jobs",
                columns: new[] { "priority", "run_after", "id" },
                descending: new[] { true, false, false },
                filter: "status = 'queued'");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_idempotency_key",
                table: "jobs",
                column: "idempotency_key",
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_parent_job_id",
                table: "jobs",
                column: "parent_job_id");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_resource",
                table: "jobs",
                columns: new[] { "resource_type", "resource_id", "created_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_running_lease",
                table: "jobs",
                column: "lease_expires_at",
                filter: "status = 'running'");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_running_lock_key",
                table: "jobs",
                column: "lock_key",
                filter: "status = 'running'");

            migrationBuilder.CreateIndex(
                name: "ix_join_tokens_created_by_user_id",
                table: "join_tokens",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_join_tokens_server_id",
                table: "join_tokens",
                column: "server_id");

            migrationBuilder.CreateIndex(
                name: "ix_join_tokens_token_hash",
                table: "join_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_log_chunks_ts",
                table: "log_chunks",
                column: "ts");

            migrationBuilder.CreateIndex(
                name: "ix_metric_samples_server_id_container_id_resolution_timestamp",
                table: "metric_samples",
                columns: new[] { "server_id", "container_id", "resolution", "timestamp" },
                filter: "container_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_metric_samples_server_id_resolution_timestamp",
                table: "metric_samples",
                columns: new[] { "server_id", "resolution", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_networks_environment_id",
                table: "networks",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "ix_networks_project_id",
                table: "networks",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_networks_server_id_docker_name",
                table: "networks",
                columns: new[] { "server_id", "docker_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_organization_members_organization_id_user_id",
                table: "organization_members",
                columns: new[] { "organization_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_organization_members_user_id",
                table: "organization_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_organizations_slug",
                table: "organizations",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_projects_organization_id_slug",
                table: "projects",
                columns: new[] { "organization_id", "slug" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_registries_organization_id_name",
                table: "registries",
                columns: new[] { "organization_id", "name" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_registries_password_secret_id",
                table: "registries",
                column: "password_secret_id");

            migrationBuilder.CreateIndex(
                name: "ix_resource_events_resource_type_resource_id_occurred_at",
                table: "resource_events",
                columns: new[] { "resource_type", "resource_id", "occurred_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_secrets_environment_id",
                table: "secrets",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "ix_secrets_organization_id_project_id_environment_id_workload_",
                table: "secrets",
                columns: new[] { "organization_id", "project_id", "environment_id", "workload_id", "name" },
                unique: true,
                filter: "deleted_at IS NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_secrets_project_id",
                table: "secrets",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_secrets_workload_id",
                table: "secrets",
                column: "workload_id");

            migrationBuilder.CreateIndex(
                name: "ix_servers_cert_serial",
                table: "servers",
                column: "cert_serial");

            migrationBuilder.CreateIndex(
                name: "ix_servers_organization_id_name",
                table: "servers",
                columns: new[] { "organization_id", "name" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_servers_ssh_credential_secret_id",
                table: "servers",
                column: "ssh_credential_secret_id");

            migrationBuilder.CreateIndex(
                name: "ix_settings_updated_by_user_id",
                table: "settings",
                column: "updated_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_team_members_team_id_user_id",
                table: "team_members",
                columns: new[] { "team_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_team_members_user_id",
                table: "team_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_teams_organization_id_slug",
                table: "teams",
                columns: new[] { "organization_id", "slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_sessions_expires_at",
                table: "user_sessions",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_user_sessions_secret_hash",
                table: "user_sessions",
                column: "secret_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_sessions_user_id",
                table: "user_sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_normalized_email",
                table: "users",
                column: "normalized_email",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_volumes_workload_id_mount_path",
                table: "volumes",
                columns: new[] { "workload_id", "mount_path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_volumes_workload_id_name",
                table: "volumes",
                columns: new[] { "workload_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_endpoint_id_delivery_id",
                table: "webhook_deliveries",
                columns: new[] { "endpoint_id", "delivery_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_endpoint_id_received_at",
                table: "webhook_deliveries",
                columns: new[] { "endpoint_id", "received_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_endpoints_secret_id",
                table: "webhook_endpoints",
                column: "secret_id");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_endpoints_workload_id",
                table: "webhook_endpoints",
                column: "workload_id");

            migrationBuilder.CreateIndex(
                name: "ix_workload_networks_network_id",
                table: "workload_networks",
                column: "network_id");

            migrationBuilder.CreateIndex(
                name: "ix_workload_ports_workload_id_container_port_protocol",
                table: "workload_ports",
                columns: new[] { "workload_id", "container_port", "protocol" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workloads_current_deployment_id",
                table: "workloads",
                column: "current_deployment_id");

            migrationBuilder.CreateIndex(
                name: "ix_workloads_environment_id_slug",
                table: "workloads",
                columns: new[] { "environment_id", "slug" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_workloads_server_id",
                table: "workloads",
                column: "server_id");

            migrationBuilder.AddForeignKey(
                name: "fk_agent_certificates_servers_server_id",
                table: "agent_certificates",
                column: "server_id",
                principalTable: "servers",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_build_configs_applications_application_id",
                table: "build_configs",
                column: "application_id",
                principalTable: "workloads",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_builds_deployments_deployment_id",
                table: "builds",
                column: "deployment_id",
                principalTable: "deployments",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_compose_sources_applications_application_id",
                table: "compose_sources",
                column: "application_id",
                principalTable: "workloads",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_deployment_steps_deployments_deployment_id",
                table: "deployment_steps",
                column: "deployment_id",
                principalTable: "deployments",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_deployments_servers_server_id",
                table: "deployments",
                column: "server_id",
                principalTable: "servers",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_deployments_workloads_workload_id",
                table: "deployments",
                column: "workload_id",
                principalTable: "workloads",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_domains_servers_server_id",
                table: "domains",
                column: "server_id",
                principalTable: "servers",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_domains_workloads_workload_id",
                table: "domains",
                column: "workload_id",
                principalTable: "workloads",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_environment_variables_secrets_secret_id",
                table: "environment_variables",
                column: "secret_id",
                principalTable: "secrets",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_environment_variables_workloads_workload_id",
                table: "environment_variables",
                column: "workload_id",
                principalTable: "workloads",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_git_credentials_secrets_secret_id",
                table: "git_credentials",
                column: "secret_id",
                principalTable: "secrets",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_git_sources_applications_application_id",
                table: "git_sources",
                column: "application_id",
                principalTable: "workloads",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_image_sources_applications_application_id",
                table: "image_sources",
                column: "application_id",
                principalTable: "workloads",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_image_sources_registries_registry_id",
                table: "image_sources",
                column: "registry_id",
                principalTable: "registries",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_images_servers_server_id",
                table: "images",
                column: "server_id",
                principalTable: "servers",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_images_workloads_workload_id",
                table: "images",
                column: "workload_id",
                principalTable: "workloads",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_join_tokens_servers_server_id",
                table: "join_tokens",
                column: "server_id",
                principalTable: "servers",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_metric_samples_servers_server_id",
                table: "metric_samples",
                column: "server_id",
                principalTable: "servers",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_networks_servers_server_id",
                table: "networks",
                column: "server_id",
                principalTable: "servers",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_registries_secrets_password_secret_id",
                table: "registries",
                column: "password_secret_id",
                principalTable: "secrets",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_secret_versions_secrets_secret_id",
                table: "secret_versions",
                column: "secret_id",
                principalTable: "secrets",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_secrets_workloads_workload_id",
                table: "secrets",
                column: "workload_id",
                principalTable: "workloads",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            // Hand-written: audit events are append-only. Retention deletes remain possible; updates are not.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION audit_events_reject_update() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'audit_events is append-only';
                END;
                $$;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_audit_events_reject_update
                BEFORE UPDATE ON audit_events
                FOR EACH ROW EXECUTE FUNCTION audit_events_reject_update();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_audit_events_reject_update ON audit_events;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS audit_events_reject_update();");

            migrationBuilder.DropForeignKey(
                name: "fk_deployments_servers_server_id",
                table: "deployments");

            migrationBuilder.DropForeignKey(
                name: "fk_workloads_servers_server_id",
                table: "workloads");

            migrationBuilder.DropForeignKey(
                name: "fk_api_tokens_organizations_organization_id",
                table: "api_tokens");

            migrationBuilder.DropForeignKey(
                name: "fk_projects_organizations_organization_id",
                table: "projects");

            migrationBuilder.DropForeignKey(
                name: "fk_api_tokens_users_created_by_user_id",
                table: "api_tokens");

            migrationBuilder.DropForeignKey(
                name: "fk_deployments_users_triggered_by_user_id",
                table: "deployments");

            migrationBuilder.DropForeignKey(
                name: "fk_deployments_workloads_workload_id",
                table: "deployments");

            migrationBuilder.DropForeignKey(
                name: "fk_builds_deployments_deployment_id",
                table: "builds");

            migrationBuilder.DropTable(
                name: "agent_certificates");

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "build_configs");

            migrationBuilder.DropTable(
                name: "compose_sources");

            migrationBuilder.DropTable(
                name: "deployment_steps");

            migrationBuilder.DropTable(
                name: "domains");

            migrationBuilder.DropTable(
                name: "environment_variables");

            migrationBuilder.DropTable(
                name: "git_sources");

            migrationBuilder.DropTable(
                name: "idempotency_records");

            migrationBuilder.DropTable(
                name: "image_sources");

            migrationBuilder.DropTable(
                name: "images");

            migrationBuilder.DropTable(
                name: "join_tokens");

            migrationBuilder.DropTable(
                name: "log_chunks");

            migrationBuilder.DropTable(
                name: "metric_samples");

            migrationBuilder.DropTable(
                name: "organization_members");

            migrationBuilder.DropTable(
                name: "resource_events");

            migrationBuilder.DropTable(
                name: "secret_versions");

            migrationBuilder.DropTable(
                name: "settings");

            migrationBuilder.DropTable(
                name: "team_members");

            migrationBuilder.DropTable(
                name: "user_sessions");

            migrationBuilder.DropTable(
                name: "volumes");

            migrationBuilder.DropTable(
                name: "webhook_deliveries");

            migrationBuilder.DropTable(
                name: "workload_networks");

            migrationBuilder.DropTable(
                name: "workload_ports");

            migrationBuilder.DropTable(
                name: "certificate_authorities");

            migrationBuilder.DropTable(
                name: "git_credentials");

            migrationBuilder.DropTable(
                name: "registries");

            migrationBuilder.DropTable(
                name: "teams");

            migrationBuilder.DropTable(
                name: "webhook_endpoints");

            migrationBuilder.DropTable(
                name: "networks");

            migrationBuilder.DropTable(
                name: "servers");

            migrationBuilder.DropTable(
                name: "secrets");

            migrationBuilder.DropTable(
                name: "organizations");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "workloads");

            migrationBuilder.DropTable(
                name: "deployments");

            migrationBuilder.DropTable(
                name: "api_tokens");

            migrationBuilder.DropTable(
                name: "builds");

            migrationBuilder.DropTable(
                name: "environments");

            migrationBuilder.DropTable(
                name: "jobs");

            migrationBuilder.DropTable(
                name: "projects");
        }
    }
}
