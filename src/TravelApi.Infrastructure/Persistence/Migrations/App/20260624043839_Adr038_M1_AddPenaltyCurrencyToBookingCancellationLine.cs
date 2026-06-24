using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// CAMBIO 3 (2026-06-24): agrega la moneda en que el operador retuvo la multa, a nivel linea del BC.
    /// Aditiva y nullable. Backfill = la moneda de la propia linea (BookingCancellationLines.Currency), asi las
    /// lineas historicas quedan con un valor coherente sin asumir nada (cada servicio cancelado ya tiene su
    /// moneda). NO cambia la moneda en la que se EMITE la Nota de Debito al cliente.
    /// </summary>
    public partial class Adr038_M1_AddPenaltyCurrencyToBookingCancellationLine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PenaltyCurrency",
                table: "BookingCancellationLines",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            // Backfill: las lineas existentes toman como moneda de la multa la moneda de la propia linea.
            // Solo donde quedo NULL (la columna nace NULL para todas). Las comillas dobles escapan los
            // identificadores con mayusculas de Postgres.
            migrationBuilder.Sql(
                "UPDATE \"BookingCancellationLines\" " +
                "SET \"PenaltyCurrency\" = \"Currency\" " +
                "WHERE \"PenaltyCurrency\" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PenaltyCurrency",
                table: "BookingCancellationLines");
        }
    }
}
