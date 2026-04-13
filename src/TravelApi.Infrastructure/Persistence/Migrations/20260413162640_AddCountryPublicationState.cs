using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCountryPublicationState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPublished",
                table: "Countries",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PublishedAt",
                table: "Countries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "Countries"
                SET "PublishedAt" = COALESCE("UpdatedAt", "CreatedAt")
                WHERE "IsPublished" = TRUE
                  AND "PublishedAt" IS NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Countries_IsPublished_Slug",
                table: "Countries",
                columns: new[] { "IsPublished", "Slug" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Countries_IsPublished_Slug",
                table: "Countries");

            migrationBuilder.DropColumn(
                name: "IsPublished",
                table: "Countries");

            migrationBuilder.DropColumn(
                name: "PublishedAt",
                table: "Countries");
        }
    }
}
