using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class AnnulWithoutInvoice_MakeOriginatingInvoiceIdOptional : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BookingCancellations_OriginatingInvoiceId",
                table: "BookingCancellations");

            migrationBuilder.AlterColumn<int>(
                name: "OriginatingInvoiceId",
                table: "BookingCancellations",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellations_OriginatingInvoiceId",
                table: "BookingCancellations",
                column: "OriginatingInvoiceId",
                unique: true,
                filter: "\"OriginatingInvoiceId\" IS NOT NULL AND \"Status\" NOT IN (4, 6)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // N2 (2026-07-23, coherencia forward-only): NO se vuelve a NOT NULL. El proyecto es forward-only
            // en PROD (nunca se corre Down contra la base real), pero si alguna vez se ejecutara, un
            // "ALTER COLUMN ... SET NOT NULL DEFAULT 0" sobre filas que YA tienen OriginatingInvoiceId=null
            // (los BC de la obra "anular sin factura", que ancla el receivable del operador SIN factura de
            // venta) fallaria o — peor — les "inventaria" una factura falsa (Id=0) que no existe. Mismo
            // criterio que Fix500_FlightStatusMaxLength y AnnulWithoutInvoice_RelaxFiscalSnapshotCheck: un
            // rollback "seguro" no puede reintroducir una restriccion que los datos reales ya violan.
        }
    }
}
