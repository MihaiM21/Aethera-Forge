using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aethera.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTwoFactorAndImageTag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "require_two_factor",
                table: "organizations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "image_tag",
                table: "deployments",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "user_recovery_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_recovery_codes", x => x.id);
                    table.CheckConstraint("ck_user_recovery_codes_code_hash_len", "octet_length(code_hash) = 32");
                    table.ForeignKey(
                        name: "fk_user_recovery_codes_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_two_factors",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    secret_ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    secret_nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    wrapped_data_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    wrapped_data_key_nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    master_key_version = table.Column<int>(type: "integer", nullable: false),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_step = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_two_factors", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_user_two_factors_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_recovery_codes_user_id_code_hash",
                table: "user_recovery_codes",
                columns: new[] { "user_id", "code_hash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_recovery_codes");

            migrationBuilder.DropTable(
                name: "user_two_factors");

            migrationBuilder.DropColumn(
                name: "require_two_factor",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "image_tag",
                table: "deployments");
        }
    }
}
