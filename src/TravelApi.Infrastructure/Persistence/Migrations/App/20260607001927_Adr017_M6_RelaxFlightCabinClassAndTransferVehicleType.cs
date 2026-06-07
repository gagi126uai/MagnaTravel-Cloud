using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// ADR-018 Ronda 7 (guia UX, 2026-06-06): "Cabina" (Aereo) y "Tipo de vehiculo" (Traslado) pasan a
    /// OPCIONALES — el sistema deja de exigirlos. La ficha manda null cuando el vendedor elige
    /// "Sin especificar" y el backend lo persiste tal cual (se derogo el coalesce a "Economy"/"Sedan"
    /// de ADR-018 §2), asi que las columnas deben aceptar NULL.
    ///
    /// <para>Que hace (TODO metadata-only, sin backfill): DROP NOT NULL en
    /// <c>FlightSegments.CabinClass</c> y <c>TransferBookings.VehicleType</c>. Las filas existentes
    /// conservan su valor ("Economy"/"Sedan"/etc.); solo las cargas nuevas pueden quedar en null.</para>
    ///
    /// <para><b>Por que es segura</b>: <c>ALTER COLUMN ... DROP NOT NULL</c> en Postgres es metadata-only
    /// (no reescribe la tabla; ACCESS EXCLUSIVE de milisegundos; no hay CHECK sobre estas columnas).
    /// Mismo patron que Adr017_M5.</para>
    ///
    /// <para><b>Orden de deploy / R8</b>: M6, encolada DETRAS de Adr017_M5 (20260606060000), sin tocar
    /// ni reordenar ninguna migracion previa. NO se aplica desde aca.</para>
    /// </summary>
    public partial class Adr017_M6_RelaxFlightCabinClassAndTransferVehicleType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "VehicleType",
                table: "TransferBookings",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.AlterColumn<string>(
                name: "CabinClass",
                table: "FlightSegments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // forward-only (mismo criterio que Adr017_M5): no re-imponer NOT NULL — fallaria con
            // filas nuevas que ya tengan cabina/vehiculo en null ("Sin especificar").
        }
    }
}
