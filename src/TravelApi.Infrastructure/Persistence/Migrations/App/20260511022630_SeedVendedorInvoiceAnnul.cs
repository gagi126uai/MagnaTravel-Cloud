using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// B1.15 Fase 0 (smoke 2026-05-10): el Vendedor real no podia anular sus
    /// propias facturas (recibia Forbidden). El endpoint POST /api/invoices/{id}/annul
    /// requiere <c>cobranzas.invoice_annul</c>, que el Vendedor no tenia.
    ///
    /// El caso operativo es legitimo: el Vendedor crea la factura y necesita
    /// anularla en el momento cuando se equivoca (cliente/monto/items). El
    /// endpoint ya valida ownership (RequireOwnership Invoice, bypass via
    /// cobranzas.view_all), por lo que el Vendedor solo podra anular SUS
    /// facturas. La operacion queda auditada (AnnulledByUser*, AnnulmentReason)
    /// y dispara la emision de Nota de Credito en AFIP.
    ///
    /// Esta migracion seedea <c>cobranzas.invoice_annul</c> al rol "Vendedor"
    /// en BD existente. Idempotente: WHERE NOT EXISTS sobre la unique constraint
    /// (RoleName, Permission) de RolePermissions.
    ///
    /// La constante <c>Permissions.DefaultVendedor</c> tambien fue actualizada
    /// para que los Vendedores creados desde cero (BD vacia) reciban este
    /// permiso via el seeder de roles iniciales.
    /// </summary>
    public partial class SeedVendedorInvoiceAnnul : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO "RolePermissions" ("RoleName", "Permission")
                SELECT 'Vendedor', 'cobranzas.invoice_annul'
                WHERE NOT EXISTS (
                    SELECT 1 FROM "RolePermissions"
                    WHERE "RoleName" = 'Vendedor' AND "Permission" = 'cobranzas.invoice_annul'
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM "RolePermissions"
                WHERE "RoleName" = 'Vendedor' AND "Permission" = 'cobranzas.invoice_annul';
                """);
        }
    }
}
