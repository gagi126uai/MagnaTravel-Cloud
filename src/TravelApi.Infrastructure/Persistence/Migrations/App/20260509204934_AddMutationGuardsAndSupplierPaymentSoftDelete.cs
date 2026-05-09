using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// B1.15 Fase 0' (CODE-09 / CODE-10 / INV-2):
    ///
    /// 1) Agrega soft-delete a <c>SupplierPayments</c> (columnas <c>IsDeleted</c>,
    ///    <c>DeletedAt</c>, <c>DeletedByUserId</c> + indice IX_SupplierPayments_IsDeleted).
    ///    Antes era hard-delete: el pago desaparecia y el ajuste de
    ///    <c>Supplier.CurrentBalance</c> quedaba sin trazabilidad. Ahora el
    ///    delete handler hace soft-delete y registra <c>AuditLog</c> con quien.
    ///    Restore queda diferido a Fase I (no hay UI hoy).
    ///
    /// 2) Seed del permiso nuevo <c>tesoreria.supplier_payments</c> para los
    ///    roles Admin y Colaborador. El Vendedor NO recibe este permiso por
    ///    default — el modulo de pagos a proveedores es back-office.
    ///
    /// Backfill: las filas <c>SupplierPayments</c> existentes quedan en
    /// <c>IsDeleted = false</c> (default columna). No hay que correr UPDATE.
    /// </summary>
    public partial class AddMutationGuardsAndSupplierPaymentSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "SupplierPayments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedByUserId",
                table: "SupplierPayments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "SupplierPayments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayments_IsDeleted",
                table: "SupplierPayments",
                column: "IsDeleted");

            // Seed permiso nuevo `tesoreria.supplier_payments` para Admin y
            // Colaborador. Vendedor NO lo tiene por default — back-office only.
            // Idempotente: WHERE NOT EXISTS (RolePermissions tiene UQ por (RoleName, Permission)).
            foreach (var role in new[] { "Admin", "Colaborador" })
            {
                migrationBuilder.Sql($"""
                    INSERT INTO "RolePermissions" ("RoleName", "Permission")
                    SELECT '{role}', 'tesoreria.supplier_payments'
                    WHERE NOT EXISTS (
                        SELECT 1 FROM "RolePermissions"
                        WHERE "RoleName" = '{role}' AND "Permission" = 'tesoreria.supplier_payments'
                    );
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Quitar el seed primero — luego las columnas. Si el rollback corre
            // contra una BD donde un admin manual otorgo el permiso a otros
            // roles, se borra todo: la filosofia de Down() es dejar el sistema
            // en el estado pre-Up().
            migrationBuilder.Sql("""
                DELETE FROM "RolePermissions" WHERE "Permission" = 'tesoreria.supplier_payments';
                """);

            migrationBuilder.DropIndex(
                name: "IX_SupplierPayments_IsDeleted",
                table: "SupplierPayments");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "SupplierPayments");

            migrationBuilder.DropColumn(
                name: "DeletedByUserId",
                table: "SupplierPayments");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "SupplierPayments");
        }
    }
}
