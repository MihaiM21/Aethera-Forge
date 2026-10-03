using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aethera.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSecretPurpose : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "purpose",
                table: "secrets",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValueSql: "'user'");

            // Backfill. Everything stays 'user' unless it is provably owned by another resource; the first matching rule wins.
            // 1. Registry passwords: linked from a registry, or named like the ones the registry endpoints create ('registry/<id>', organization scope).
            migrationBuilder.Sql("""
                UPDATE secrets SET purpose = 'registryCredential'
                WHERE purpose = 'user'
                  AND (id IN (SELECT password_secret_id FROM registries WHERE password_secret_id IS NOT NULL)
                       OR (name LIKE 'registry/%' AND project_id IS NULL AND environment_id IS NULL AND workload_id IS NULL));
                """);

            // 2. SSH credentials of servers, 3. git credentials: by reference.
            migrationBuilder.Sql("""
                UPDATE secrets SET purpose = 'sshCredential'
                WHERE purpose = 'user' AND id IN (SELECT ssh_credential_secret_id FROM servers WHERE ssh_credential_secret_id IS NOT NULL);
                """);
            migrationBuilder.Sql("""
                UPDATE secrets SET purpose = 'gitCredential'
                WHERE purpose = 'user' AND id IN (SELECT secret_id FROM git_credentials);
                """);

            // 4. Passwords generated for a service from a template: scoped to the service, described as generated, and used by that service.
            migrationBuilder.Sql("""
                UPDATE secrets s SET purpose = 'serviceGenerated'
                WHERE s.purpose = 'user'
                  AND s.workload_id IS NOT NULL
                  AND s.description LIKE 'Generated for service%'
                  AND EXISTS (SELECT 1 FROM workloads w WHERE w.id = s.workload_id AND w.kind = 'service')
                  AND EXISTS (SELECT 1 FROM environment_variables v WHERE v.secret_id = s.id AND v.workload_id = s.workload_id);
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_secrets_purpose",
                table: "secrets",
                sql: "purpose IN ('user', 'registryCredential', 'sshCredential', 'gitCredential', 'serviceGenerated')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_secrets_purpose",
                table: "secrets");

            migrationBuilder.DropColumn(
                name: "purpose",
                table: "secrets");
        }
    }
}
