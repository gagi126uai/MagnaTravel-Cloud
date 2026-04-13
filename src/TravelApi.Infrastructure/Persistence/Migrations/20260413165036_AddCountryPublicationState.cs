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
            // Idempotent: only add columns if they don't exist yet
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_name = 'Countries' AND column_name = 'IsPublished'
                    ) THEN
                        ALTER TABLE "Countries" ADD COLUMN "IsPublished" boolean NOT NULL DEFAULT TRUE;
                    END IF;

                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_name = 'Countries' AND column_name = 'PublishedAt'
                    ) THEN
                        ALTER TABLE "Countries" ADD COLUMN "PublishedAt" timestamp with time zone;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql(
                """
                UPDATE "Countries"
                SET "PublishedAt" = COALESCE("UpdatedAt", "CreatedAt")
                WHERE "IsPublished" = TRUE
                  AND "PublishedAt" IS NULL;
                """);

            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS "IX_Countries_IsPublished_Slug"
                ON "Countries" ("IsPublished", "Slug");
                """);
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
