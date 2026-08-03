using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class Adr053_DniExpiryAlert_AddDocumentExpiryGeographicScopeAndAgencyFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GeographicScope",
                table: "Reservations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "DocumentExpiry",
                table: "Passengers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EnableDomesticDniExpiryAlert",
                table: "OperationalFinanceSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "GeographicScope",
                table: "FlightSegments",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GeographicScope",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "DocumentExpiry",
                table: "Passengers");

            migrationBuilder.DropColumn(
                name: "EnableDomesticDniExpiryAlert",
                table: "OperationalFinanceSettings");

            migrationBuilder.DropColumn(
                name: "GeographicScope",
                table: "FlightSegments");
        }
    }
}
