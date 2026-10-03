using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aethera.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobOrganization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nullable first: existing rows are attributed to an organization before the column becomes required.
            migrationBuilder.AddColumn<Guid>(
                name: "organization_id",
                table: "jobs",
                type: "uuid",
                nullable: true);

            // Backfill: the creator's (oldest) membership, else the oldest organization (jobs of the system, e.g. webhooks, belonged
            // to every organization until now; single-organization installs lose nothing).
            migrationBuilder.Sql("""
                UPDATE jobs j
                SET organization_id = COALESCE(
                    (SELECT m.organization_id FROM organization_members m
                     WHERE m.user_id = j.created_by
                     ORDER BY m.created_at, m.id LIMIT 1),
                    (SELECT o.id FROM organizations o ORDER BY o.created_at, o.id LIMIT 1));
                """);

            // Only possible when there is no organization at all: such jobs cannot belong to anyone and were never visible to a caller.
            migrationBuilder.Sql("""
                DELETE FROM log_chunks WHERE stream_id IN (SELECT 'job:' || id::text FROM jobs WHERE organization_id IS NULL);
                DELETE FROM jobs WHERE organization_id IS NULL;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "organization_id",
                table: "jobs",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_jobs_organization_created_at",
                table: "jobs",
                columns: new[] { "organization_id", "created_at" });

            migrationBuilder.AddForeignKey(
                name: "fk_jobs_organizations_organization_id",
                table: "jobs",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_jobs_organizations_organization_id",
                table: "jobs");

            migrationBuilder.DropIndex(
                name: "ix_jobs_organization_created_at",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "jobs");
        }
    }
}
