using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogPackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CatalogPackages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Tagline = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Destination = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    HeroImageFileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: true),
                    HeroImageStoredFileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: true),
                    HeroImageContentType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    HeroImageFileSize = table.Column<long>(type: "bigint", nullable: true),
                    GeneralInfo = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    IsPublished = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogPackages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CatalogPackageDepartures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    CatalogPackageId = table.Column<int>(type: "integer", nullable: false),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Nights = table.Column<int>(type: "integer", nullable: false),
                    TransportLabel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    HotelName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MealPlan = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RoomBase = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    SalePrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogPackageDepartures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CatalogPackageDepartures_CatalogPackages_CatalogPackageId",
                        column: x => x.CatalogPackageId,
                        principalTable: "CatalogPackages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogPackageDepartures_CatalogPackageId_StartDate",
                table: "CatalogPackageDepartures",
                columns: new[] { "CatalogPackageId", "StartDate" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogPackageDepartures_PublicId",
                table: "CatalogPackageDepartures",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatalogPackages_IsPublished_Slug",
                table: "CatalogPackages",
                columns: new[] { "IsPublished", "Slug" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogPackages_PublicId",
                table: "CatalogPackages",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatalogPackages_Slug",
                table: "CatalogPackages",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CatalogPackageDepartures");

            migrationBuilder.DropTable(
                name: "CatalogPackages");
        }
    }
}
