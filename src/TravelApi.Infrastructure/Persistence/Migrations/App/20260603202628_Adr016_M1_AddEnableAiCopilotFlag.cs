using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// ADR-016 F0a (Base del copiloto de IA, 2026-06-03): migracion 100% ADITIVA.
    /// Agrega <c>OperationalFinanceSettings.EnableAiCopilot</c> = bool NOT NULL default
    /// <c>false</c>. Con el flag OFF (default) el copiloto no existe: el cerebro de IA esta
    /// registrado en DI pero nadie lo invoca, asi que el comportamiento es byte-identico a hoy
    /// y cero datos salen hacia la nube.
    ///
    /// <para><b>Orden de deploy</b>: aplicar esta migracion ANTES de subir el binario nuevo
    /// (orden estandar del repo). La app vieja ignora la columna nueva; la app nueva la lee solo
    /// para exponer el flag (default OFF). No hay backfill ni reescritura de filas.</para>
    ///
    /// <para><b>Rollback</b>: dropear la columna apaga la posibilidad de prender el copiloto;
    /// no hay datos que perder (la API key del proveedor nunca estuvo en la DB, vive en env).</para>
    /// </summary>
    public partial class Adr016_M1_AddEnableAiCopilotFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnableAiCopilot",
                table: "OperationalFinanceSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnableAiCopilot",
                table: "OperationalFinanceSettings");
        }
    }
}
