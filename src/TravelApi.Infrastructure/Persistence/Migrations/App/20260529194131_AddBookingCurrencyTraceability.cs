using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class AddBookingCurrencyTraceability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "TransferBookings",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "PackageBookings",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "HotelBookings",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "FlightSegments",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Currency",
                table: "TransferBookings");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "PackageBookings");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "HotelBookings");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "FlightSegments");
        }
    }
}
