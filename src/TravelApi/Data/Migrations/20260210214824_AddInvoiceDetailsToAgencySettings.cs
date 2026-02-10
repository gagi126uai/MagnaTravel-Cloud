using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceDetailsToAgencySettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ActivityStartDate",
                table: "AgencySettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegalName",
                table: "AgencySettings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxCondition",
                table: "AgencySettings",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActivityStartDate",
                table: "AgencySettings");

            migrationBuilder.DropColumn(
                name: "LegalName",
                table: "AgencySettings");

            migrationBuilder.DropColumn(
                name: "TaxCondition",
                table: "AgencySettings");
        }
    }
}
