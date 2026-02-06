using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProfessionalRateFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Rates_Suppliers_SupplierId",
                table: "Rates");

            migrationBuilder.AlterColumn<DateTime>(
                name: "ValidTo",
                table: "Rates",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<DateTime>(
                name: "ValidFrom",
                table: "Rates",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<int>(
                name: "SupplierId",
                table: "Rates",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "Rates",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Airline",
                table: "Rates",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AirlineCode",
                table: "Rates",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BaggageIncluded",
                table: "Rates",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CabinClass",
                table: "Rates",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "Rates",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Commission",
                table: "Rates",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Destination",
                table: "Rates",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DropoffLocation",
                table: "Rates",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DurationDays",
                table: "Rates",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HotelName",
                table: "Rates",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IncludesExcursions",
                table: "Rates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IncludesFlight",
                table: "Rates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IncludesHotel",
                table: "Rates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IncludesInsurance",
                table: "Rates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IncludesTransfer",
                table: "Rates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "InternalNotes",
                table: "Rates",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsRoundTrip",
                table: "Rates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Itinerary",
                table: "Rates",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxPassengers",
                table: "Rates",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MealPlan",
                table: "Rates",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "Rates",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PickupLocation",
                table: "Rates",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PriceUnit",
                table: "Rates",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RoomType",
                table: "Rates",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StarRating",
                table: "Rates",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Tax",
                table: "Rates",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "VehicleType",
                table: "Rates",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Rates_Suppliers_SupplierId",
                table: "Rates",
                column: "SupplierId",
                principalTable: "Suppliers",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Rates_Suppliers_SupplierId",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "Airline",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "AirlineCode",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "BaggageIncluded",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "CabinClass",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "City",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "Commission",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "Destination",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "DropoffLocation",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "DurationDays",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "HotelName",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "IncludesExcursions",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "IncludesFlight",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "IncludesHotel",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "IncludesInsurance",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "IncludesTransfer",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "InternalNotes",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "IsRoundTrip",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "Itinerary",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "MaxPassengers",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "MealPlan",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "PickupLocation",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "PriceUnit",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "RoomType",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "StarRating",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "Tax",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "VehicleType",
                table: "Rates");

            migrationBuilder.AlterColumn<DateTime>(
                name: "ValidTo",
                table: "Rates",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified),
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "ValidFrom",
                table: "Rates",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified),
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "SupplierId",
                table: "Rates",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "Rates",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000,
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Rates_Suppliers_SupplierId",
                table: "Rates",
                column: "SupplierId",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
