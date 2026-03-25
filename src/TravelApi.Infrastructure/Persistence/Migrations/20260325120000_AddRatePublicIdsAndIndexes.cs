using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TravelApi.Infrastructure.Persistence;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260325120000_AddRatePublicIdsAndIndexes")]
    public partial class AddRatePublicIdsAndIndexes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""CREATE EXTENSION IF NOT EXISTS pgcrypto;""");

            migrationBuilder.Sql("""
                ALTER TABLE "Rates"
                ADD COLUMN IF NOT EXISTS "PublicId" uuid NOT NULL DEFAULT gen_random_uuid();
                """);

            migrationBuilder.Sql("""
                UPDATE "Rates"
                SET "PublicId" = gen_random_uuid()
                WHERE "PublicId" = '00000000-0000-0000-0000-000000000000';
                """);

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_Rates_PublicId"
                ON "Rates" ("PublicId");
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Rates_ServiceType_IsActive_ValidTo_SupplierId"
                ON "Rates" ("ServiceType", "IsActive", "ValidTo", "SupplierId");
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Rates_HotelName_City_ValidTo"
                ON "Rates" ("HotelName", "City", "ValidTo");
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Rates_ProductName"
                ON "Rates" ("ProductName");
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "Rates"
                ALTER COLUMN "PublicId" DROP DEFAULT;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Rates_ProductName";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Rates_HotelName_City_ValidTo";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Rates_ServiceType_IsActive_ValidTo_SupplierId";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Rates_PublicId";""");
            migrationBuilder.Sql("""ALTER TABLE "Rates" DROP COLUMN IF EXISTS "PublicId";""");
        }
    }
}
