using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// ADR-018 (2026-06-06): reconcilia la ficha "producto-primero" (un solo campo de busqueda por
    /// servicio) con el esquema estructurado de los bookings no-Hotel. Antes, crear vuelo/traslado/
    /// paquete desde la ficha tiraba HTTP 500 (violacion NOT NULL) porque la ficha no carga
    /// aerolinea/origen/destino/pickup/dropoff/destino-fin por separado.
    ///
    /// <para>Que hace (TODO metadata-only, sin backfill):
    /// <list type="bullet">
    ///   <item>ADD COLUMN <c>ProductName</c> varchar(200) NULL en <c>FlightSegments</c> y
    ///   <c>TransferBookings</c>: identidad visible (el texto que vio el vendedor).</item>
    ///   <item>DROP NOT NULL en Flight <c>AirlineCode/FlightNumber/Origin/Destination</c>,
    ///   Transfer <c>PickupLocation/DropoffLocation</c> y Package <c>Destination/EndDate</c>.</item>
    /// </list></para>
    ///
    /// <para><b>Por que es segura</b>: <c>ALTER COLUMN ... DROP NOT NULL</c> en Postgres es metadata-only
    /// (no reescribe la tabla; toma un ACCESS EXCLUSIVE de milisegundos; no toca las filas existentes;
    /// no hay CHECK sobre estas columnas). <c>ADD COLUMN ... NULL</c> sin default tampoco reescribe.</para>
    ///
    /// <para><b>Orden de deploy / R8</b>: M5, encolada DETRAS de la cola pendiente del VPS (la ultima
    /// era Adr017_M4, timestamp 20260606054040), sin tocar ni reordenar ninguna. NO se aplica desde aca.</para>
    /// </summary>
    public partial class Adr017_M5_RelaxNonHotelStructuredFieldsAndAddProductName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // --- Identidad visible "producto-primero" (columnas nuevas, nullable) ---
            migrationBuilder.AddColumn<string>(
                name: "ProductName",
                table: "FlightSegments",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProductName",
                table: "TransferBookings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            // --- Relajar NOT NULL en los campos estructurados que la ficha ya no carga por separado ---
            // FlightSegments
            migrationBuilder.AlterColumn<string>(
                name: "AirlineCode",
                table: "FlightSegments",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(3)",
                oldMaxLength: 3);

            migrationBuilder.AlterColumn<string>(
                name: "FlightNumber",
                table: "FlightSegments",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10);

            migrationBuilder.AlterColumn<string>(
                name: "Origin",
                table: "FlightSegments",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(3)",
                oldMaxLength: 3);

            migrationBuilder.AlterColumn<string>(
                name: "Destination",
                table: "FlightSegments",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(3)",
                oldMaxLength: 3);

            // TransferBookings
            migrationBuilder.AlterColumn<string>(
                name: "PickupLocation",
                table: "TransferBookings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<string>(
                name: "DropoffLocation",
                table: "TransferBookings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            // PackageBookings
            migrationBuilder.AlterColumn<string>(
                name: "Destination",
                table: "PackageBookings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.AlterColumn<DateTime>(
                name: "EndDate",
                table: "PackageBookings",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // forward-only: no re-imponer NOT NULL (fallaria con filas de catalogo null).
        }
    }
}
