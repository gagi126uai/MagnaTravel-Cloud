using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// B1.15 (2026-05-11): soporte para anulacion de comprobante interno de pago
    /// (PaymentReceipt). Cuatro cambios atomicos en una sola migracion:
    ///
    ///  1. Columnas de audit trail de anulacion (PaymentReceipts):
    ///     - VoidReason (text, max 500)
    ///     - VoidedByUserId (text, max 64)
    ///     - VoidedByUserName (text, max 200)
    ///     Todas nullable: el default es "nunca anulado". La fila Receipt nunca se borra
    ///     fisicamente (ARCA + Contable 2026-05-06) — solo Status -> Voided + audit fields.
    ///
    ///  2. Permiso <c>cobranzas.receipt_void</c> seedeado a roles Admin, Colaborador y
    ///     Vendedor. El endpoint POST /api/payments/{id}/receipt/void requiere el permiso
    ///     y RequireOwnership (Payment, bypass via cobranzas.view_all), por lo que el
    ///     Vendedor solo anula recibos de sus pagos. Idempotente (WHERE NOT EXISTS).
    ///
    ///  3. ApprovalPolicy para <c>ReceiptVoidance</c> seedeada con RequiresApproval=true
    ///     (default conservador, mismo patron que InvoiceAnnulment). Admin bypassa el
    ///     workflow en el service; Colaborador/Vendedor requieren ApprovalRequest Approved.
    ///     Idempotente (WHERE NOT EXISTS sobre unique RequestType).
    ///
    ///  4. Down() revierte los 3 cambios manuales y luego dropea las columnas.
    /// </summary>
    public partial class AddReceiptVoidFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // (1) Columnas de audit trail (autogeneradas por EF, conservadas).
            migrationBuilder.AddColumn<string>(
                name: "VoidReason",
                table: "PaymentReceipts",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VoidedByUserId",
                table: "PaymentReceipts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VoidedByUserName",
                table: "PaymentReceipts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            // (2) Seed permiso cobranzas.receipt_void a Admin/Colaborador/Vendedor.
            // Idempotente: WHERE NOT EXISTS sobre la unique constraint (RoleName, Permission).
            migrationBuilder.Sql("""
                INSERT INTO "RolePermissions" ("RoleName", "Permission")
                SELECT 'Admin', 'cobranzas.receipt_void'
                WHERE NOT EXISTS (
                    SELECT 1 FROM "RolePermissions"
                    WHERE "RoleName" = 'Admin' AND "Permission" = 'cobranzas.receipt_void'
                );

                INSERT INTO "RolePermissions" ("RoleName", "Permission")
                SELECT 'Colaborador', 'cobranzas.receipt_void'
                WHERE NOT EXISTS (
                    SELECT 1 FROM "RolePermissions"
                    WHERE "RoleName" = 'Colaborador' AND "Permission" = 'cobranzas.receipt_void'
                );

                INSERT INTO "RolePermissions" ("RoleName", "Permission")
                SELECT 'Vendedor', 'cobranzas.receipt_void'
                WHERE NOT EXISTS (
                    SELECT 1 FROM "RolePermissions"
                    WHERE "RoleName" = 'Vendedor' AND "Permission" = 'cobranzas.receipt_void'
                );
                """);

            // (3) Seed ApprovalPolicy ReceiptVoidance (RequiresApproval = TRUE).
            // Default conservador: Vendedor/Colaborador deben pedir aprobacion (Admin bypassa).
            // Mismo patron que el seed de InvoiceAnnulment en migration AddApprovalPolicies.
            // Defaults de expiracion/cooldown via override null -> heredan de
            // OperationalFinanceSettings.ApprovalDefaultExpirationDays y ApprovalRejectionCooldownHours.
            migrationBuilder.Sql("""
                INSERT INTO "ApprovalPolicies" ("RequestType", "RequiresApproval", "UpdatedAt")
                SELECT 'ReceiptVoidance', TRUE, NOW()
                WHERE NOT EXISTS (SELECT 1 FROM "ApprovalPolicies" WHERE "RequestType" = 'ReceiptVoidance');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revertir seeds antes de dropear columnas (orden inverso de Up).
            migrationBuilder.Sql("""
                DELETE FROM "ApprovalPolicies"
                WHERE "RequestType" = 'ReceiptVoidance';

                DELETE FROM "RolePermissions"
                WHERE "Permission" = 'cobranzas.receipt_void';
                """);

            migrationBuilder.DropColumn(
                name: "VoidReason",
                table: "PaymentReceipts");

            migrationBuilder.DropColumn(
                name: "VoidedByUserId",
                table: "PaymentReceipts");

            migrationBuilder.DropColumn(
                name: "VoidedByUserName",
                table: "PaymentReceipts");
        }
    }
}
