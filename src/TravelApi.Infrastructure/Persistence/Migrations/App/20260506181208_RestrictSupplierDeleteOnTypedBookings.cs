using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class RestrictSupplierDeleteOnTypedBookings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FlightSegments_Suppliers_SupplierId",
                table: "FlightSegments");

            migrationBuilder.DropForeignKey(
                name: "FK_HotelBookings_Suppliers_SupplierId",
                table: "HotelBookings");

            migrationBuilder.DropForeignKey(
                name: "FK_PackageBookings_Suppliers_SupplierId",
                table: "PackageBookings");

            migrationBuilder.DropForeignKey(
                name: "FK_TransferBookings_Suppliers_SupplierId",
                table: "TransferBookings");

            migrationBuilder.AddForeignKey(
                name: "FK_FlightSegments_Suppliers_SupplierId",
                table: "FlightSegments",
                column: "SupplierId",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_HotelBookings_Suppliers_SupplierId",
                table: "HotelBookings",
                column: "SupplierId",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PackageBookings_Suppliers_SupplierId",
                table: "PackageBookings",
                column: "SupplierId",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TransferBookings_Suppliers_SupplierId",
                table: "TransferBookings",
                column: "SupplierId",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FlightSegments_Suppliers_SupplierId",
                table: "FlightSegments");

            migrationBuilder.DropForeignKey(
                name: "FK_HotelBookings_Suppliers_SupplierId",
                table: "HotelBookings");

            migrationBuilder.DropForeignKey(
                name: "FK_PackageBookings_Suppliers_SupplierId",
                table: "PackageBookings");

            migrationBuilder.DropForeignKey(
                name: "FK_TransferBookings_Suppliers_SupplierId",
                table: "TransferBookings");

            migrationBuilder.AddForeignKey(
                name: "FK_FlightSegments_Suppliers_SupplierId",
                table: "FlightSegments",
                column: "SupplierId",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_HotelBookings_Suppliers_SupplierId",
                table: "HotelBookings",
                column: "SupplierId",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PackageBookings_Suppliers_SupplierId",
                table: "PackageBookings",
                column: "SupplierId",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_TransferBookings_Suppliers_SupplierId",
                table: "TransferBookings",
                column: "SupplierId",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
