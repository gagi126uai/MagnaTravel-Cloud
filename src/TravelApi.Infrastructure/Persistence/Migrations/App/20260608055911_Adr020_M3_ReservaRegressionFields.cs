using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// ADR-020 (decision #6): agrega LastRegressionReason + LastRegressionAt a TravelFiles para que
    /// el frontend muestre la franja naranja cuando una reserva confirmada vuelve sola a En gestion.
    /// Aditiva (2 columnas nullable, sin default ni backfill): segura de correr sobre datos existentes.
    /// Va detras de M1/M2 en el mismo tren de deploy ADR-020. NO edita M1/M2 (aun sin aplicar).
    /// </summary>
    public partial class Adr020_M3_ReservaRegressionFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastRegressionAt",
                table: "TravelFiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastRegressionReason",
                table: "TravelFiles",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastRegressionAt",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "LastRegressionReason",
                table: "TravelFiles");
        }
    }
}
