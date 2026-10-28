using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aethera.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AutomaticDomains : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "auto_domain_assigned",
                table: "workloads",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Existing workloads predate automatic domains: an upgrade must not route them anywhere new (ADR 0011).
            migrationBuilder.Sql("UPDATE workloads SET auto_domain_assigned = true;");

            migrationBuilder.AddColumn<string>(
                name: "wildcard_domain",
                table: "servers",
                type: "character varying(253)",
                maxLength: 253,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_automatic",
                table: "domains",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "auto_domain_assigned",
                table: "workloads");

            migrationBuilder.DropColumn(
                name: "wildcard_domain",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "is_automatic",
                table: "domains");
        }
    }
}
