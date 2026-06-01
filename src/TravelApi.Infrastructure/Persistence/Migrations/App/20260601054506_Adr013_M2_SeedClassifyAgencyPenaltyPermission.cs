using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// ADR-013 (2026-06-01): seedea el permiso nuevo
    /// <c>cancellations.classify_agency_penalty</c> al rol "Colaborador" en BDs ya
    /// inicializadas. Sin esto, una BD existente no tendria el permiso en
    /// <c>RolePermissions</c> y el Colaborador no podria clasificar la penalidad como
    /// ingreso propio (la guarda del service lo rechazaria) hasta re-seedear roles.
    ///
    /// <para><b>Solo Colaborador</b>: el Admin ya pasa por el bypass de rol en
    /// <c>PermissionAuthorizationHandler</c> (no necesita fila en RolePermissions). El
    /// Vendedor NO lo recibe a proposito: un vendedor comun no debe poder disparar una
    /// ND fiscal (decision de seguridad ADR-013).</para>
    ///
    /// <para><b>NO toca el schema</b> (el model snapshot queda igual): es una migracion
    /// 100% de datos. <b>Idempotente</b>: <c>WHERE NOT EXISTS</c> sobre la unique
    /// (RoleName, Permission), mismo patron que <c>SeedVendedorInvoiceAnnul</c>.</para>
    ///
    /// <para>La constante <c>Permissions.DefaultColaborador</c> tambien se actualizo,
    /// asi los Colaboradores creados desde BD vacia reciben el permiso via el seeder
    /// de roles iniciales.</para>
    /// </summary>
    public partial class Adr013_M2_SeedClassifyAgencyPenaltyPermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO "RolePermissions" ("RoleName", "Permission")
                SELECT 'Colaborador', 'cancellations.classify_agency_penalty'
                WHERE NOT EXISTS (
                    SELECT 1 FROM "RolePermissions"
                    WHERE "RoleName" = 'Colaborador'
                      AND "Permission" = 'cancellations.classify_agency_penalty'
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM "RolePermissions"
                WHERE "RoleName" = 'Colaborador'
                  AND "Permission" = 'cancellations.classify_agency_penalty';
                """);
        }
    }
}
