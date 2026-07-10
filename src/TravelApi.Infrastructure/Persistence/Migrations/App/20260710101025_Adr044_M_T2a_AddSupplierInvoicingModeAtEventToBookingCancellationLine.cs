using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// ADR-044 T2 Addendum, Decision A (2026-07-10): agrega el snapshot NULLABLE del modo de facturacion del
    /// operador (<c>SupplierInvoicingMode</c>) a nivel de <c>BookingCancellationLine</c>. Sin backfill (a
    /// proposito): el campo equivalente del padre (<c>FiscalSnapshot.InvoicingModeAtEvent</c>) NUNCA se llego
    /// a poblar en produccion (verificado leyendo los 3 sitios reales donde se construye
    /// <c>new FiscalSnapshot {...}</c> en <c>BookingCancellationService.cs</c>), asi que toda linea existente
    /// arranca con esta columna en <c>NULL</c> y cae al fallback vivo
    /// (<c>line.SupplierInvoicingModeAtEvent ?? line.Supplier.InvoicingMode</c>) que ya usa el sistema real hoy
    /// — cero regresion, comportamiento identico al de antes de esta migracion.
    ///
    /// <para><b>Consulta de validacion (solo lectura, para correr antes/despues de aplicar)</b>: confirma que
    /// la columna nueva existe y que TODAS las filas existentes arrancan en null (el comportamiento esperado,
    /// sin backfill):
    /// <code>
    /// SELECT COUNT(*) AS total_lineas,
    ///        COUNT(*) FILTER (WHERE "SupplierInvoicingModeAtEvent" IS NOT NULL) AS con_snapshot
    /// FROM "BookingCancellationLines";
    /// -- Esperado justo despues de aplicar esta migracion: con_snapshot = 0 (nadie confirmo un cargo
    /// -- todavia bajo el modelo nuevo). Un con_snapshot &gt; 0 mas adelante es normal: son lineas que ya
    /// -- pasaron por el confirm-penalty nuevo (T2 service layer).
    /// </code></para>
    /// </summary>
    public partial class Adr044_M_T2a_AddSupplierInvoicingModeAtEventToBookingCancellationLine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SupplierInvoicingModeAtEvent",
                table: "BookingCancellationLines",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SupplierInvoicingModeAtEvent",
                table: "BookingCancellationLines");
        }
    }
}
