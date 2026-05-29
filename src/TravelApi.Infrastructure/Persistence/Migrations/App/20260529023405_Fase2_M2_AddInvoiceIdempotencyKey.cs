using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// FC1.3 Fase 2 (Fase2_M2, 2026-05-28): agrega <c>Invoices.IdempotencyKey</c>, la huella
    /// real de idempotencia de cada NC parcial (el mismo hash que se inserta en
    /// <c>ArcaIdempotencyKeys.Key</c>). El job de reconciliacion la lee directo en vez de
    /// re-derivarla, eliminando la fragilidad de recalcular el hash desde el monto.
    ///
    /// <para><b>Por que es 100% SEGURA (aditiva)</b>: solo agrega UNA columna NULLABLE, SIN
    /// default, SIN backfill y SIN CHECK. Las filas existentes (NC viejas) quedan con NULL y el
    /// codigo las maneja con el camino de re-derivacion historico. No reescribe ni una sola fila,
    /// asi que no hay lock de tabla pesado ni riesgo de datos. La version vieja de la app sigue
    /// funcionando contra el schema nuevo (ignora la columna), y la version nueva contra el
    /// schema viejo fallaria solo al leer la columna — por eso el deploy aplica la migracion
    /// ANTES de subir el binario nuevo (orden estandar del repo).</para>
    /// </summary>
    public partial class Fase2_M2_AddInvoiceIdempotencyKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Columna nullable de 64 chars (ancho de un SHA256 en hex). Sin defaultValue: una NC
            // legacy debe quedar NULL para que el lookup caiga al fallback de re-derivacion, no a
            // un string vacio que no matchearia ninguna ArcaIdempotencyKey.
            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "Invoices",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "Invoices");
        }
    }
}
