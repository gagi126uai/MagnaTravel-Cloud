using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// B1.15 Fase B' (2026-05-11): tabla ApprovalRequests + 2 settings nuevos +
    /// seed de los permisos approvals.request / approvals.review a roles
    /// existentes (Admin/Colaborador reciben los dos; Vendedor recibe solo
    /// approvals.request).
    /// </summary>
    public partial class AddApprovalRequestsAndSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Defaults coherentes con OperationalFinanceSettings.cs: 7 dias y 1 hora.
            // El defaultValue se aplica a filas existentes (la unica fila singleton
            // queda con valores razonables sin SQL extra).
            migrationBuilder.AddColumn<int>(
                name: "ApprovalDefaultExpirationDays",
                table: "OperationalFinanceSettings",
                type: "integer",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.AddColumn<int>(
                name: "ApprovalRejectionCooldownHours",
                table: "OperationalFinanceSettings",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "ApprovalRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestType = table.Column<int>(type: "integer", nullable: false),
                    RequestedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    RequestedByUserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    EntityId = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ResolvedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    ResolvedByUserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolverNotes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CooldownUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Metadata = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_EntityType_EntityId_Status",
                table: "ApprovalRequests",
                columns: new[] { "EntityType", "EntityId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_ExpiresAt",
                table: "ApprovalRequests",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_PublicId",
                table: "ApprovalRequests",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_RequestedByUserId_Status",
                table: "ApprovalRequests",
                columns: new[] { "RequestedByUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_Status",
                table: "ApprovalRequests",
                column: "Status");

            // Seed permisos a roles existentes en BD. Idempotente (WHERE NOT EXISTS
            // sobre la unique constraint (RoleName, Permission)).
            //  - Admin: ambos permisos (review + request).
            //  - Colaborador: ambos.
            //  - Vendedor: solo request.
            // Roles custom no son tocados — el Admin los gestiona desde UI.
            string[] adminColabPermissions = { "approvals.request", "approvals.review" };
            string[] adminColabRoles = { "Admin", "Colaborador" };
            foreach (var role in adminColabRoles)
            {
                foreach (var permission in adminColabPermissions)
                {
                    migrationBuilder.Sql($"""
                        INSERT INTO "RolePermissions" ("RoleName", "Permission")
                        SELECT '{role}', '{permission}'
                        WHERE NOT EXISTS (
                            SELECT 1 FROM "RolePermissions"
                            WHERE "RoleName" = '{role}' AND "Permission" = '{permission}'
                        );
                        """);
                }
            }
            migrationBuilder.Sql("""
                INSERT INTO "RolePermissions" ("RoleName", "Permission")
                SELECT 'Vendedor', 'approvals.request'
                WHERE NOT EXISTS (
                    SELECT 1 FROM "RolePermissions"
                    WHERE "RoleName" = 'Vendedor' AND "Permission" = 'approvals.request'
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Quitar SOLO los permisos seedeados por esta migracion.
            migrationBuilder.Sql("""
                DELETE FROM "RolePermissions"
                WHERE "Permission" IN ('approvals.request', 'approvals.review');
                """);

            migrationBuilder.DropTable(
                name: "ApprovalRequests");

            migrationBuilder.DropColumn(
                name: "ApprovalDefaultExpirationDays",
                table: "OperationalFinanceSettings");

            migrationBuilder.DropColumn(
                name: "ApprovalRejectionCooldownHours",
                table: "OperationalFinanceSettings");
        }
    }
}
