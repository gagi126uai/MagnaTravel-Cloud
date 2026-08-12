using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class PdfPresupuesto_M1_AgencyLogoConditionsAndServiceOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OptionGroup",
                table: "TransferBookings",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OptionLabel",
                table: "TransferBookings",
                type: "character varying(5)",
                maxLength: 5,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OptionGroup",
                table: "PackageBookings",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OptionLabel",
                table: "PackageBookings",
                type: "character varying(5)",
                maxLength: 5,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OptionGroup",
                table: "HotelBookings",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OptionLabel",
                table: "HotelBookings",
                type: "character varying(5)",
                maxLength: 5,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IncludesBackpack",
                table: "FlightSegments",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IncludesCarryOn",
                table: "FlightSegments",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IncludesCheckedBag",
                table: "FlightSegments",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDirect",
                table: "FlightSegments",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OptionGroup",
                table: "FlightSegments",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OptionLabel",
                table: "FlightSegments",
                type: "character varying(5)",
                maxLength: 5,
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "OutboundDepartureTime",
                table: "FlightSegments",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "ReturnDepartureTime",
                table: "FlightSegments",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OptionGroup",
                table: "AssistanceBookings",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OptionLabel",
                table: "AssistanceBookings",
                type: "character varying(5)",
                maxLength: 5,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgencyLicenseNumber",
                table: "AgencySettings",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogoContentType",
                table: "AgencySettings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogoFileName",
                table: "AgencySettings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LogoFileSize",
                table: "AgencySettings",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogoStoredFileName",
                table: "AgencySettings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PdfBandColorHex",
                table: "AgencySettings",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BudgetConditionBlocks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BudgetConditionBlocks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BudgetConditionBlocks_Kind",
                table: "BudgetConditionBlocks",
                column: "Kind",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BudgetConditionBlocks");

            migrationBuilder.DropColumn(
                name: "OptionGroup",
                table: "TransferBookings");

            migrationBuilder.DropColumn(
                name: "OptionLabel",
                table: "TransferBookings");

            migrationBuilder.DropColumn(
                name: "OptionGroup",
                table: "PackageBookings");

            migrationBuilder.DropColumn(
                name: "OptionLabel",
                table: "PackageBookings");

            migrationBuilder.DropColumn(
                name: "OptionGroup",
                table: "HotelBookings");

            migrationBuilder.DropColumn(
                name: "OptionLabel",
                table: "HotelBookings");

            migrationBuilder.DropColumn(
                name: "IncludesBackpack",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "IncludesCarryOn",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "IncludesCheckedBag",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "IsDirect",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "OptionGroup",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "OptionLabel",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "OutboundDepartureTime",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "ReturnDepartureTime",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "OptionGroup",
                table: "AssistanceBookings");

            migrationBuilder.DropColumn(
                name: "OptionLabel",
                table: "AssistanceBookings");

            migrationBuilder.DropColumn(
                name: "AgencyLicenseNumber",
                table: "AgencySettings");

            migrationBuilder.DropColumn(
                name: "LogoContentType",
                table: "AgencySettings");

            migrationBuilder.DropColumn(
                name: "LogoFileName",
                table: "AgencySettings");

            migrationBuilder.DropColumn(
                name: "LogoFileSize",
                table: "AgencySettings");

            migrationBuilder.DropColumn(
                name: "LogoStoredFileName",
                table: "AgencySettings");

            migrationBuilder.DropColumn(
                name: "PdfBandColorHex",
                table: "AgencySettings");
        }
    }
}
