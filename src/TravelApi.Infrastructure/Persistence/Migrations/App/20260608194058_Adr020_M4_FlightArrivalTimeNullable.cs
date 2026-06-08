using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// BUG 2 (2026-06-08): relaja FlightSegments.ArrivalTime a NULLABLE para soportar vuelos solo de
    /// ida (un segmento puede no tener hora de llegada). Es un AlterColumn NOT NULL -> NULL: NO toca
    /// datos existentes (las filas con hora de llegada la conservan), por eso es segura sobre la base
    /// en produccion. Generada por EF (no SQL crudo) para que el nombre de columna salga del modelo.
    /// El Down vuelve a NOT NULL: solo correria sin error si NO hay filas con ArrivalTime null.
    /// </summary>
    public partial class Adr020_M4_FlightArrivalTimeNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTime>(
                name: "ArrivalTime",
                table: "FlightSegments",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTime>(
                name: "ArrivalTime",
                table: "FlightSegments",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified),
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }
    }
}
