using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCountriesAndDestinationsRefactor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Countries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Slug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Countries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Destinations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    CountryId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Tagline = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("PK_Destinations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Destinations_Countries_CountryId",
                        column: x => x.CountryId,
                        principalTable: "Countries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DestinationDepartures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinationId = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("PK_DestinationDepartures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DestinationDepartures_Destinations_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "Destinations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Countries_PublicId",
                table: "Countries",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Countries_Slug",
                table: "Countries",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DestinationDepartures_DestinationId_StartDate",
                table: "DestinationDepartures",
                columns: new[] { "DestinationId", "StartDate" });

            migrationBuilder.CreateIndex(
                name: "IX_DestinationDepartures_PublicId",
                table: "DestinationDepartures",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Destinations_CountryId_DisplayOrder",
                table: "Destinations",
                columns: new[] { "CountryId", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_Destinations_CountryId_Name",
                table: "Destinations",
                columns: new[] { "CountryId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_Destinations_IsPublished_CountryId_DisplayOrder",
                table: "Destinations",
                columns: new[] { "IsPublished", "CountryId", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_Destinations_PublicId",
                table: "Destinations",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Destinations_Slug",
                table: "Destinations",
                column: "Slug",
                unique: true);

            migrationBuilder.Sql(
                """
                WITH package_source AS (
                    SELECT
                        cp."Id",
                        cp."PublicId",
                        cp."Title",
                        cp."Slug",
                        COALESCE(NULLIF(BTRIM(cp."Destination"), ''), cp."Title") AS destination_name,
                        cp."Tagline",
                        cp."DestinationOrder",
                        cp."HeroImageFileName",
                        cp."HeroImageStoredFileName",
                        cp."HeroImageContentType",
                        cp."HeroImageFileSize",
                        cp."GeneralInfo",
                        cp."IsPublished",
                        cp."PublishedAt",
                        cp."CreatedAt",
                        cp."UpdatedAt",
                        COALESCE(NULLIF(BTRIM(cp."CountryName"), ''), 'Sin pais') AS country_name,
                        COALESCE(
                            NULLIF(BTRIM(cp."CountrySlug"), ''),
                            NULLIF(BTRIM(REGEXP_REPLACE(LOWER(COALESCE(cp."CountryName", 'Sin pais')), '[^a-z0-9]+', '-', 'g')), '-'),
                            'sin-pais'
                        ) AS country_slug
                    FROM "CatalogPackages" cp
                ),
                distinct_countries AS (
                    SELECT DISTINCT ON (country_slug)
                        country_slug,
                        country_name,
                        MIN("CreatedAt") OVER (PARTITION BY country_slug) AS created_at,
                        MAX("UpdatedAt") OVER (PARTITION BY country_slug) AS updated_at
                    FROM package_source
                    ORDER BY country_slug, country_name
                )
                INSERT INTO "Countries" ("PublicId", "Name", "Slug", "CreatedAt", "UpdatedAt")
                SELECT
                    gen_random_uuid(),
                    dc.country_name,
                    dc.country_slug,
                    dc.created_at,
                    dc.updated_at
                FROM distinct_countries dc;
                """);

            migrationBuilder.Sql(
                """
                WITH package_source AS (
                    SELECT
                        cp."PublicId",
                        cp."Title",
                        cp."Slug",
                        COALESCE(NULLIF(BTRIM(cp."Destination"), ''), cp."Title") AS destination_name,
                        cp."Tagline",
                        cp."DestinationOrder",
                        cp."HeroImageFileName",
                        cp."HeroImageStoredFileName",
                        cp."HeroImageContentType",
                        cp."HeroImageFileSize",
                        cp."GeneralInfo",
                        cp."IsPublished",
                        cp."PublishedAt",
                        cp."CreatedAt",
                        cp."UpdatedAt",
                        COALESCE(
                            NULLIF(BTRIM(cp."CountrySlug"), ''),
                            NULLIF(BTRIM(REGEXP_REPLACE(LOWER(COALESCE(cp."CountryName", 'Sin pais')), '[^a-z0-9]+', '-', 'g')), '-'),
                            'sin-pais'
                        ) AS country_slug
                    FROM "CatalogPackages" cp
                )
                INSERT INTO "Destinations" (
                    "PublicId",
                    "CountryId",
                    "Name",
                    "Title",
                    "Slug",
                    "Tagline",
                    "DisplayOrder",
                    "HeroImageFileName",
                    "HeroImageStoredFileName",
                    "HeroImageContentType",
                    "HeroImageFileSize",
                    "GeneralInfo",
                    "IsPublished",
                    "CreatedAt",
                    "UpdatedAt",
                    "PublishedAt"
                )
                SELECT
                    ps."PublicId",
                    c."Id",
                    ps.destination_name,
                    ps."Title",
                    ps."Slug",
                    ps."Tagline",
                    ps."DestinationOrder",
                    ps."HeroImageFileName",
                    ps."HeroImageStoredFileName",
                    ps."HeroImageContentType",
                    ps."HeroImageFileSize",
                    ps."GeneralInfo",
                    ps."IsPublished",
                    ps."CreatedAt",
                    ps."UpdatedAt",
                    ps."PublishedAt"
                FROM package_source ps
                INNER JOIN "Countries" c ON c."Slug" = ps.country_slug;
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO "DestinationDepartures" (
                    "PublicId",
                    "DestinationId",
                    "StartDate",
                    "Nights",
                    "TransportLabel",
                    "HotelName",
                    "MealPlan",
                    "RoomBase",
                    "Currency",
                    "SalePrice",
                    "IsPrimary",
                    "IsActive",
                    "CreatedAt",
                    "UpdatedAt"
                )
                SELECT
                    cpd."PublicId",
                    d."Id",
                    cpd."StartDate",
                    cpd."Nights",
                    cpd."TransportLabel",
                    cpd."HotelName",
                    cpd."MealPlan",
                    cpd."RoomBase",
                    cpd."Currency",
                    cpd."SalePrice",
                    cpd."IsPrimary",
                    cpd."IsActive",
                    cpd."CreatedAt",
                    cpd."UpdatedAt"
                FROM "CatalogPackageDepartures" cpd
                INNER JOIN "CatalogPackages" cp ON cp."Id" = cpd."CatalogPackageId"
                INNER JOIN "Destinations" d ON d."Slug" = cp."Slug";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DestinationDepartures");

            migrationBuilder.DropTable(
                name: "Destinations");

            migrationBuilder.DropTable(
                name: "Countries");
        }
    }
}
