using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// Agrega la columna <c>Tax</c> (impuesto INCLUIDO en el costo) a los 4 tipos de servicio que
    /// todavia no la tenian: Hotel, Traslado, Paquete y Asistencia. Calca el patron que ya existia
    /// en FlightSegment.Tax / Rate.Tax (ver entidades). El impuesto NO suma al precio que paga el
    /// cliente; es un componente del costo y entra en la ganancia: Commission = SalePrice - NetCost - Tax.
    ///
    /// <para>Migracion ADITIVA y byte-safe: la columna es <c>numeric(18,2) NOT NULL DEFAULT 0</c>,
    /// asi las filas existentes quedan con Tax=0. Con Tax=0 la ganancia no cambia respecto de hoy
    /// (SalePrice - NetCost - 0 = SalePrice - NetCost), por eso no rompe saldos ni montos previos.
    /// FlightSegment NO se toca aca porque su columna Tax ya existia.</para>
    ///
    /// <para>Down: dropea las 4 columnas. Es seguro porque ninguna otra logica depende de Tax
    /// para los montos historicos (Tax=0 es el estado neutro).</para>
    /// </summary>
    public partial class AddTaxToServiceBookings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Tax",
                table: "TransferBookings",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "Tax",
                table: "PackageBookings",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "Tax",
                table: "HotelBookings",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "Tax",
                table: "AssistanceBookings",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Tax",
                table: "TransferBookings");

            migrationBuilder.DropColumn(
                name: "Tax",
                table: "PackageBookings");

            migrationBuilder.DropColumn(
                name: "Tax",
                table: "HotelBookings");

            migrationBuilder.DropColumn(
                name: "Tax",
                table: "AssistanceBookings");
        }
    }
}
