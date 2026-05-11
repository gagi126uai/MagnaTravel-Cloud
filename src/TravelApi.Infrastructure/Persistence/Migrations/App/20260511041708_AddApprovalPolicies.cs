using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// B1.15 Fase B'' (2026-05-11): tabla ApprovalPolicies con policy configurable
    /// por RequestType + seed inicial de los 6 tipos.
    ///
    /// Defaults (acordados con Gaston):
    ///  - InvoiceAnnulment: hereda del valor actual de RequireApprovalForInvoiceAnnulment
    ///    (default true). El setting viejo queda en la entidad por compat pero
    ///    nadie lo lee mas — el InvoiceService ahora consulta la policy.
    ///  - ReservationCancellationWithPayment / DiscountAboveThreshold /
    ///    FrozenEntityMutation: true (sensibles).
    ///  - PaymentDeadlineOverride / ReservationTransfer: false (ágil / bajo
    ///    riesgo, el Admin puede activarlos desde UI).
    ///
    /// Tambien seedea el permiso approvals.policies a Admin (Colaborador no:
    /// la decision fue "solo Admin configura policies").
    /// </summary>
    public partial class AddApprovalPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApprovalPolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RequestType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    RequiresApproval = table.Column<bool>(type: "boolean", nullable: false),
                    ExpirationDaysOverride = table.Column<int>(type: "integer", nullable: true),
                    CooldownHoursOverride = table.Column<int>(type: "integer", nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    UpdatedByUserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalPolicies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalPolicies_RequestType",
                table: "ApprovalPolicies",
                column: "RequestType",
                unique: true);

            // Seed: InvoiceAnnulment hereda del valor actual del setting viejo.
            // Para los otros 5 usamos los defaults acordados.
            // Todos los INSERT son idempotentes con WHERE NOT EXISTS sobre el
            // unique constraint de RequestType (re-run safe).
            migrationBuilder.Sql("""
                INSERT INTO "ApprovalPolicies" ("RequestType", "RequiresApproval", "UpdatedAt")
                SELECT 'InvoiceAnnulment',
                       COALESCE((SELECT "RequireApprovalForInvoiceAnnulment" FROM "OperationalFinanceSettings" LIMIT 1), TRUE),
                       NOW()
                WHERE NOT EXISTS (SELECT 1 FROM "ApprovalPolicies" WHERE "RequestType" = 'InvoiceAnnulment');
                """);

            // Tipo, valor por default
            (string Type, bool Default)[] policies = new[]
            {
                ("ReservationCancellationWithPayment", true),
                ("DiscountAboveThreshold", true),
                ("FrozenEntityMutation", true),
                ("PaymentDeadlineOverride", false),
                ("ReservationTransfer", false),
            };
            foreach (var (type, def) in policies)
            {
                var defSql = def ? "TRUE" : "FALSE";
                migrationBuilder.Sql($"""
                    INSERT INTO "ApprovalPolicies" ("RequestType", "RequiresApproval", "UpdatedAt")
                    SELECT '{type}', {defSql}, NOW()
                    WHERE NOT EXISTS (SELECT 1 FROM "ApprovalPolicies" WHERE "RequestType" = '{type}');
                    """);
            }

            // Seed permiso approvals.policies al rol Admin (Colaborador NO).
            migrationBuilder.Sql("""
                INSERT INTO "RolePermissions" ("RoleName", "Permission")
                SELECT 'Admin', 'approvals.policies'
                WHERE NOT EXISTS (
                    SELECT 1 FROM "RolePermissions"
                    WHERE "RoleName" = 'Admin' AND "Permission" = 'approvals.policies'
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM "RolePermissions"
                WHERE "Permission" = 'approvals.policies';
                """);

            migrationBuilder.DropTable(name: "ApprovalPolicies");
        }
    }
}
