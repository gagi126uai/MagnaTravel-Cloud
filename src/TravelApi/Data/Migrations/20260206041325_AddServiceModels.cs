using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "ReservationId",
                table: "FlightSegments",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<string>(
                name: "AirlineName",
                table: "FlightSegments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Baggage",
                table: "FlightSegments",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CabinClass",
                table: "FlightSegments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "Commission",
                table: "FlightSegments",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "FlightSegments",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "DestinationCity",
                table: "FlightSegments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FareBase",
                table: "FlightSegments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "NetCost",
                table: "FlightSegments",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "FlightSegments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginCity",
                table: "FlightSegments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PNR",
                table: "FlightSegments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SalePrice",
                table: "FlightSegments",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "SupplierId",
                table: "FlightSegments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "Tax",
                table: "FlightSegments",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "TicketNumber",
                table: "FlightSegments",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TravelFileId",
                table: "FlightSegments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "HotelBookings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TravelFileId = table.Column<int>(type: "integer", nullable: false),
                    SupplierId = table.Column<int>(type: "integer", nullable: false),
                    HotelName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    StarRating = table.Column<int>(type: "integer", nullable: true),
                    Address = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    City = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Country = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CheckIn = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CheckOut = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Nights = table.Column<int>(type: "integer", nullable: false),
                    RoomType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    MealPlan = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Rooms = table.Column<int>(type: "integer", nullable: false),
                    Adults = table.Column<int>(type: "integer", nullable: false),
                    Children = table.Column<int>(type: "integer", nullable: false),
                    ConfirmationNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    NetCost = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    SalePrice = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Commission = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HotelBookings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HotelBookings_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HotelBookings_TravelFiles_TravelFileId",
                        column: x => x.TravelFileId,
                        principalTable: "TravelFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PackageBookings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TravelFileId = table.Column<int>(type: "integer", nullable: false),
                    SupplierId = table.Column<int>(type: "integer", nullable: false),
                    PackageName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Destination = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Nights = table.Column<int>(type: "integer", nullable: false),
                    IncludesHotel = table.Column<bool>(type: "boolean", nullable: false),
                    IncludesFlight = table.Column<bool>(type: "boolean", nullable: false),
                    IncludesTransfer = table.Column<bool>(type: "boolean", nullable: false),
                    IncludesExcursions = table.Column<bool>(type: "boolean", nullable: false),
                    IncludesMeals = table.Column<bool>(type: "boolean", nullable: false),
                    Adults = table.Column<int>(type: "integer", nullable: false),
                    Children = table.Column<int>(type: "integer", nullable: false),
                    Itinerary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ConfirmationNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    NetCost = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    SalePrice = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Commission = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageBookings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PackageBookings_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PackageBookings_TravelFiles_TravelFileId",
                        column: x => x.TravelFileId,
                        principalTable: "TravelFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Rates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SupplierId = table.Column<int>(type: "integer", nullable: false),
                    ServiceType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ProductName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    NetCost = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    SalePrice = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ValidTo = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Rates_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TransferBookings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TravelFileId = table.Column<int>(type: "integer", nullable: false),
                    SupplierId = table.Column<int>(type: "integer", nullable: false),
                    PickupLocation = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DropoffLocation = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PickupDateTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FlightNumber = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    VehicleType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Passengers = table.Column<int>(type: "integer", nullable: false),
                    IsRoundTrip = table.Column<bool>(type: "boolean", nullable: false),
                    ReturnDateTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConfirmationNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    NetCost = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    SalePrice = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Commission = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransferBookings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TransferBookings_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TransferBookings_TravelFiles_TravelFileId",
                        column: x => x.TravelFileId,
                        principalTable: "TravelFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FlightSegments_SupplierId",
                table: "FlightSegments",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_FlightSegments_TravelFileId",
                table: "FlightSegments",
                column: "TravelFileId");

            migrationBuilder.CreateIndex(
                name: "IX_HotelBookings_SupplierId",
                table: "HotelBookings",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_HotelBookings_TravelFileId",
                table: "HotelBookings",
                column: "TravelFileId");

            migrationBuilder.CreateIndex(
                name: "IX_PackageBookings_SupplierId",
                table: "PackageBookings",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_PackageBookings_TravelFileId",
                table: "PackageBookings",
                column: "TravelFileId");

            migrationBuilder.CreateIndex(
                name: "IX_Rates_SupplierId",
                table: "Rates",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_TransferBookings_SupplierId",
                table: "TransferBookings",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_TransferBookings_TravelFileId",
                table: "TransferBookings",
                column: "TravelFileId");

            migrationBuilder.AddForeignKey(
                name: "FK_FlightSegments_Suppliers_SupplierId",
                table: "FlightSegments",
                column: "SupplierId",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_FlightSegments_TravelFiles_TravelFileId",
                table: "FlightSegments",
                column: "TravelFileId",
                principalTable: "TravelFiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FlightSegments_Suppliers_SupplierId",
                table: "FlightSegments");

            migrationBuilder.DropForeignKey(
                name: "FK_FlightSegments_TravelFiles_TravelFileId",
                table: "FlightSegments");

            migrationBuilder.DropTable(
                name: "HotelBookings");

            migrationBuilder.DropTable(
                name: "PackageBookings");

            migrationBuilder.DropTable(
                name: "Rates");

            migrationBuilder.DropTable(
                name: "TransferBookings");

            migrationBuilder.DropIndex(
                name: "IX_FlightSegments_SupplierId",
                table: "FlightSegments");

            migrationBuilder.DropIndex(
                name: "IX_FlightSegments_TravelFileId",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "AirlineName",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "Baggage",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "CabinClass",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "Commission",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "DestinationCity",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "FareBase",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "NetCost",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "OriginCity",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "PNR",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "SalePrice",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "SupplierId",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "Tax",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "TicketNumber",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "TravelFileId",
                table: "FlightSegments");

            migrationBuilder.AlterColumn<int>(
                name: "ReservationId",
                table: "FlightSegments",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
