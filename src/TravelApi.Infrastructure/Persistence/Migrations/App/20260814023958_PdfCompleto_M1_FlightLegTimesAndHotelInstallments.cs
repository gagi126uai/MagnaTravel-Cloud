using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class PdfCompleto_M1_FlightLegTimesAndHotelInstallments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "InstallmentAmount",
                table: "HotelBookings",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InstallmentsCount",
                table: "HotelBookings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "OutboundArrivalTime",
                table: "FlightSegments",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "ReturnArrivalTime",
                table: "FlightSegments",
                type: "time without time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InstallmentAmount",
                table: "HotelBookings");

            migrationBuilder.DropColumn(
                name: "InstallmentsCount",
                table: "HotelBookings");

            migrationBuilder.DropColumn(
                name: "OutboundArrivalTime",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "ReturnArrivalTime",
                table: "FlightSegments");
        }
    }
}
