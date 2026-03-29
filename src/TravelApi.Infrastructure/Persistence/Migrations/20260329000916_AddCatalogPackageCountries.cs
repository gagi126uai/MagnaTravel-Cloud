using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogPackageCountries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CountryName",
                table: "CatalogPackages",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CountrySlug",
                table: "CatalogPackages",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DestinationOrder",
                table: "CatalogPackages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_CatalogPackages_CountrySlug_Destination",
                table: "CatalogPackages",
                columns: new[] { "CountrySlug", "Destination" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogPackages_IsPublished_CountrySlug_DestinationOrder",
                table: "CatalogPackages",
                columns: new[] { "IsPublished", "CountrySlug", "DestinationOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CatalogPackages_CountrySlug_Destination",
                table: "CatalogPackages");

            migrationBuilder.DropIndex(
                name: "IX_CatalogPackages_IsPublished_CountrySlug_DestinationOrder",
                table: "CatalogPackages");

            migrationBuilder.DropColumn(
                name: "CountryName",
                table: "CatalogPackages");

            migrationBuilder.DropColumn(
                name: "CountrySlug",
                table: "CatalogPackages");

            migrationBuilder.DropColumn(
                name: "DestinationOrder",
                table: "CatalogPackages");
        }
    }
}
