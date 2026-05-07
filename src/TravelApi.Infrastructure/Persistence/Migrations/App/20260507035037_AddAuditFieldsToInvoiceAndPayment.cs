using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class AddAuditFieldsToInvoiceAndPayment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Payments",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "CreatedByUserId",
                table: "Payments",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByUserName",
                table: "Payments",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "IssuedAt",
                table: "Invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IssuedByUserId",
                table: "Invoices",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IssuedByUserName",
                table: "Invoices",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CreatedByUserId",
                table: "Payments",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_IssuedByUserId",
                table: "Invoices",
                column: "IssuedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Invoices_AspNetUsers_IssuedByUserId",
                table: "Invoices",
                column: "IssuedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Payments_AspNetUsers_CreatedByUserId",
                table: "Payments",
                column: "CreatedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // B1.15 Fase 1 - Backfill historicos.
            // Decision Gaston (decision fiscal): no completar IssuedBy*/CreatedBy* con
            // ningun userId real (queda NULL). Para preservar trazabilidad humana,
            // marcamos los Name como '(legacy)'. La columna Payments.CreatedAt es NOT
            // NULL, asi que la backfilleamos con PaidAt (si existe) o NOW() como fallback.
            migrationBuilder.Sql("""
                UPDATE "Invoices"
                SET "IssuedByUserName" = '(legacy)'
                WHERE "IssuedByUserName" IS NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE "Payments"
                SET "CreatedByUserName" = '(legacy)'
                WHERE "CreatedByUserName" IS NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE "Payments"
                SET "CreatedAt" = COALESCE("PaidAt", NOW())
                WHERE "CreatedAt" = '0001-01-01 00:00:00'::timestamp;
                """);

            // B1.15 Fase 1 - Re-seed RolePermissions con los 10 permisos nuevos.
            // Idempotente: INSERT solo si no existe ya el par (RoleName, Permission).
            // Convencion alineada con la migracion 20260327200000.
            //
            // Admin: TODOS los permisos nuevos.
            // Colaborador: opera global y cancela con pago, factura y anula, ve costos
            //   y edita datos fiscales del proveedor. NO tiene discount_above_threshold.
            // Vendedor (Decision 1 de Gaston B1.15): puede cancelar reservas propias y
            //   facturar. NO tiene *_all, *_annul, see_cost, edit_fiscal,
            //   discount_above_threshold.
            var adminNewPermissions = new[]
            {
                "reservas.view_all",
                "reservas.cancel",
                "reservas.cancel_with_payment",
                "reservas.discount_above_threshold",
                "cobranzas.view_all",
                "cobranzas.annul",
                "cobranzas.invoice",
                "cobranzas.invoice_annul",
                "cobranzas.see_cost",
                "proveedores.edit_fiscal",
            };

            foreach (var perm in adminNewPermissions)
            {
                migrationBuilder.Sql($"""
                    INSERT INTO "RolePermissions" ("RoleName", "Permission")
                    SELECT 'Admin', '{perm}'
                    WHERE NOT EXISTS (SELECT 1 FROM "RolePermissions" WHERE "RoleName" = 'Admin' AND "Permission" = '{perm}');
                    """);
            }

            var colaboradorNewPermissions = new[]
            {
                "reservas.view_all",
                "reservas.cancel",
                "reservas.cancel_with_payment",
                "cobranzas.view_all",
                "cobranzas.annul",
                "cobranzas.invoice",
                "cobranzas.invoice_annul",
                "cobranzas.see_cost",
                "proveedores.edit_fiscal",
            };

            foreach (var perm in colaboradorNewPermissions)
            {
                migrationBuilder.Sql($"""
                    INSERT INTO "RolePermissions" ("RoleName", "Permission")
                    SELECT 'Colaborador', '{perm}'
                    WHERE NOT EXISTS (SELECT 1 FROM "RolePermissions" WHERE "RoleName" = 'Colaborador' AND "Permission" = '{perm}');
                    """);
            }

            var vendedorNewPermissions = new[]
            {
                "reservas.cancel",
                "cobranzas.invoice",
            };

            foreach (var perm in vendedorNewPermissions)
            {
                migrationBuilder.Sql($"""
                    INSERT INTO "RolePermissions" ("RoleName", "Permission")
                    SELECT 'Vendedor', '{perm}'
                    WHERE NOT EXISTS (SELECT 1 FROM "RolePermissions" WHERE "RoleName" = 'Vendedor' AND "Permission" = '{perm}');
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Invoices_AspNetUsers_IssuedByUserId",
                table: "Invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_Payments_AspNetUsers_CreatedByUserId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_CreatedByUserId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_IssuedByUserId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "CreatedByUserName",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "IssuedAt",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "IssuedByUserId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "IssuedByUserName",
                table: "Invoices");
        }
    }
}
