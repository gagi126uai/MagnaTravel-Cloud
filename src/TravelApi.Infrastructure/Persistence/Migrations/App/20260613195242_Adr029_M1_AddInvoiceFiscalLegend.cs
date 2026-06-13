using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// Fix fiscal emisor RI->Monotributista (2026-06-13): agrega la columna
    /// <c>Invoice.FiscalLegend</c> (varchar(1000), NULL) para persistir la leyenda obligatoria
    /// de la Ley 27.618 que debe llevar una Factura A emitida por un Responsable Inscripto a un
    /// Monotributista. El job de emision la relee de esta fila y la manda a ARCA en el campo Obs.
    ///
    /// <para>100% ADITIVA y reversible: columna nullable nueva, no toca datos existentes (todas
    /// las facturas previas quedan con FiscalLegend NULL -> envelope byte-identico al historico).
    /// EF puro, sin SQL crudo. <b>Down</b> dropea la columna; seguro porque ningun otro objeto
    /// depende de ella.</para>
    /// </summary>
    public partial class Adr029_M1_AddInvoiceFiscalLegend : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FiscalLegend",
                table: "Invoices",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FiscalLegend",
                table: "Invoices");
        }
    }
}
