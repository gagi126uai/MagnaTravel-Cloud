using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// B1.15 smoke 2026-05-09: el Vendedor real en VPS reportó bloqueos operativos
    /// post-deploy de Fase 0' (cierre de gating en Suppliers/Vouchers + nuevos
    /// permisos requeridos):
    ///
    /// 1) NO veia proveedores → faltaba <c>proveedores.view</c> (gating Fase 0').
    /// 2) NO podia registrar cobranzas → faltaba <c>cobranzas.edit</c>.
    /// 3) NO podia operar vouchers (issue/upload/revoke) → faltaban permisos
    ///    granulares (decision 1: el Vendedor SI opera vouchers de SUS reservas).
    ///
    /// Esta migracion seedea los 5 permisos al rol "Vendedor" en BD existente.
    /// Idempotente: WHERE NOT EXISTS sobre la unique constraint (RoleName,
    /// Permission) de RolePermissions. Roles con cualquier nombre custom (no
    /// "Vendedor" canonico) NO reciben el seed automatico — el Admin los
    /// asigna desde la UI de roles.
    ///
    /// La constante <c>Permissions.DefaultVendedor</c> tambien fue actualizada
    /// para que los Vendedores creados desde cero (BD vacia) reciban estos
    /// permisos via el seeder de roles iniciales.
    /// </summary>
    public partial class SeedVendedorMissingPermissions : Migration
    {
        private static readonly string[] PermissionsToSeed = new[]
        {
            "proveedores.view",
            "cobranzas.edit",
            "vouchers.issue",
            "vouchers.upload",
            "vouchers.revoke",
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var permission in PermissionsToSeed)
            {
                migrationBuilder.Sql($"""
                    INSERT INTO "RolePermissions" ("RoleName", "Permission")
                    SELECT 'Vendedor', '{permission}'
                    WHERE NOT EXISTS (
                        SELECT 1 FROM "RolePermissions"
                        WHERE "RoleName" = 'Vendedor' AND "Permission" = '{permission}'
                    );
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Quitar SOLO los permisos que esta migracion seedeo, sin tocar otros
            // que un Admin pudo haber asignado manualmente al Vendedor (ej.
            // proveedores.view via UI). Filtramos por la lista exacta.
            foreach (var permission in PermissionsToSeed)
            {
                migrationBuilder.Sql($"""
                    DELETE FROM "RolePermissions"
                    WHERE "RoleName" = 'Vendedor' AND "Permission" = '{permission}';
                    """);
            }
        }
    }
}
