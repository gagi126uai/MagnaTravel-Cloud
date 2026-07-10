using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// ADR-044 T3b Decision 3 (config, 2026-07-10): "quién asume la diferencia de cambio de tesorería",
    /// contemplando TODOS los casos (cada agencia puede tener un default y CADA operador puede pisarlo).
    /// <list type="bullet">
    /// <item><c>OperationalFinanceSettings.TreasuryFxAssumedByDefault</c> (int NOT NULL, default 0 = Client):
    /// default a nivel agencia. El default 0 = comportamiento de hoy (lo asume el cliente), invisible.</item>
    /// <item><c>Suppliers.TreasuryFxAssumedByOverride</c> (int NULLABLE): override por operador; null = hereda
    /// el default de la agencia.</item>
    /// </list>
    /// Aditiva, sin backfill de datos: la fila de settings existente queda en 0 (Client) y todos los operadores
    /// en null (heredan). El motor resuelve override ?? default ?? Client y congela el resultado en el ajuste.
    ///
    /// <para><b>Consulta de validación (solo lectura)</b>:
    /// <code>
    /// SELECT "TreasuryFxAssumedByDefault" FROM "OperationalFinanceSettings";      -- esperado: 0 (Client)
    /// SELECT COUNT(*) FROM "Suppliers" WHERE "TreasuryFxAssumedByOverride" IS NOT NULL;  -- esperado: 0
    /// </code></para>
    /// </summary>
    public partial class Adr044_M_T4b_AddTreasuryFxAssumedByConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TreasuryFxAssumedByOverride",
                table: "Suppliers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TreasuryFxAssumedByDefault",
                table: "OperationalFinanceSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TreasuryFxAssumedByOverride",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "TreasuryFxAssumedByDefault",
                table: "OperationalFinanceSettings");
        }
    }
}
