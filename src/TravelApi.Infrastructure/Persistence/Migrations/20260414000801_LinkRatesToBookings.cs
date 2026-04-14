using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LinkRatesToBookings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RateId",
                table: "TransferBookings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RateId",
                table: "PackageBookings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RateId",
                table: "HotelBookings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RateId",
                table: "FlightSegments",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TransferBookings_RateId",
                table: "TransferBookings",
                column: "RateId");

            migrationBuilder.CreateIndex(
                name: "IX_PackageBookings_RateId",
                table: "PackageBookings",
                column: "RateId");

            migrationBuilder.CreateIndex(
                name: "IX_HotelBookings_RateId",
                table: "HotelBookings",
                column: "RateId");

            migrationBuilder.CreateIndex(
                name: "IX_FlightSegments_RateId",
                table: "FlightSegments",
                column: "RateId");

            migrationBuilder.AddForeignKey(
                name: "FK_FlightSegments_Rates_RateId",
                table: "FlightSegments",
                column: "RateId",
                principalTable: "Rates",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_HotelBookings_Rates_RateId",
                table: "HotelBookings",
                column: "RateId",
                principalTable: "Rates",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_PackageBookings_Rates_RateId",
                table: "PackageBookings",
                column: "RateId",
                principalTable: "Rates",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_TransferBookings_Rates_RateId",
                table: "TransferBookings",
                column: "RateId",
                principalTable: "Rates",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FlightSegments_Rates_RateId",
                table: "FlightSegments");

            migrationBuilder.DropForeignKey(
                name: "FK_HotelBookings_Rates_RateId",
                table: "HotelBookings");

            migrationBuilder.DropForeignKey(
                name: "FK_PackageBookings_Rates_RateId",
                table: "PackageBookings");

            migrationBuilder.DropForeignKey(
                name: "FK_TransferBookings_Rates_RateId",
                table: "TransferBookings");

            migrationBuilder.DropIndex(
                name: "IX_TransferBookings_RateId",
                table: "TransferBookings");

            migrationBuilder.DropIndex(
                name: "IX_PackageBookings_RateId",
                table: "PackageBookings");

            migrationBuilder.DropIndex(
                name: "IX_HotelBookings_RateId",
                table: "HotelBookings");

            migrationBuilder.DropIndex(
                name: "IX_FlightSegments_RateId",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "RateId",
                table: "TransferBookings");

            migrationBuilder.DropColumn(
                name: "RateId",
                table: "PackageBookings");

            migrationBuilder.DropColumn(
                name: "RateId",
                table: "HotelBookings");

            migrationBuilder.DropColumn(
                name: "RateId",
                table: "FlightSegments");
        }
    }
}
