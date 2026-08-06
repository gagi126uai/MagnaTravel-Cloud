using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// "Ayuda invisible del tipo de cambio" (spec firmada 2026-08-06, A4 + Parte B): dos columnas
    /// NUEVAS y NULLABLE en <c>Invoices</c> para el rastro interno del tipo de cambio.
    ///
    /// <list type="bullet">
    ///   <item><c>ExchangeRateOrigin</c>: COMO llego el sistema a ese tipo de cambio (lo acepto el
    ///   usuario, lo escribio el, se lo acomodo el sistema al techo del dia, o lo completo el sistema
    ///   solo). Se guarda como numero, igual que las demas clasificaciones fiscales del sistema.</item>
    ///   <item><c>RequestedExchangeRate</c>: el tipo de cambio que el usuario QUISO poner cuando el
    ///   sistema tuvo que acomodarlo. Es lo que permite explicar, meses despues, por que la factura
    ///   salio a un dolar y el cobro entro a otro.</item>
    /// </list>
    ///
    /// <para><b>Migracion ADITIVA y sin backfill</b> (regla T-8): no toca ni una fila existente. Las
    /// facturas ya emitidas quedan en <c>NULL</c> a proposito — no hay forma honesta de reconstruir la
    /// intencion del usuario de una factura vieja, y inventarla seria peor que no tenerla. Volver atras
    /// es tirar las dos columnas: ningun comprobante depende de ellas para existir (el tipo de cambio
    /// en si sigue viviendo en <c>MonCotiz</c>, como siempre).</para>
    /// </summary>
    public partial class TcAyudaInvisible_M1_AddInvoiceExchangeRateOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExchangeRateOrigin",
                table: "Invoices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RequestedExchangeRate",
                table: "Invoices",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExchangeRateOrigin",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "RequestedExchangeRate",
                table: "Invoices");
        }
    }
}
