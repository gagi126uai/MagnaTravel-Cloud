using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class Phase3and4_BudgetCountsAndCustomerDocType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AdultCount",
                table: "TravelFiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ChildCount",
                table: "TravelFiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "InfantCount",
                table: "TravelFiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DocumentType",
                table: "Customers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Customers_DocumentType_DocumentNumber",
                table: "Customers",
                columns: new[] { "DocumentType", "DocumentNumber" },
                unique: true,
                filter: "\"DocumentNumber\" IS NOT NULL AND \"DocumentType\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Customers_DocumentType_DocumentNumber",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "AdultCount",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "ChildCount",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "InfantCount",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "DocumentType",
                table: "Customers");
        }
    }
}
