using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TravelApi.Infrastructure.Persistence;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260429234500_AddVoucherRevocation")]
    public partial class AddVoucherRevocation : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "Vouchers" ADD COLUMN IF NOT EXISTS "RevokedAt" timestamp with time zone NULL;
                ALTER TABLE "Vouchers" ADD COLUMN IF NOT EXISTS "RevokedByUserId" character varying(200) NULL;
                ALTER TABLE "Vouchers" ADD COLUMN IF NOT EXISTS "RevokedByUserName" character varying(200) NULL;
                ALTER TABLE "Vouchers" ADD COLUMN IF NOT EXISTS "RevocationReason" character varying(1000) NULL;
                """);

            var permissions = new[] { "vouchers.revoke" };
            var roles = new[] { "Admin", "Colaborador" };

            foreach (var role in roles)
            {
                foreach (var permission in permissions)
                {
                    migrationBuilder.Sql($"""
                        INSERT INTO "RolePermissions" ("RoleName", "Permission")
                        SELECT '{role}', '{permission}'
                        WHERE NOT EXISTS (
                            SELECT 1 FROM "RolePermissions"
                            WHERE "RoleName" = '{role}' AND "Permission" = '{permission}'
                        );
                        """);
                }
            }
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM "RolePermissions" WHERE "Permission" = 'vouchers.revoke';
                ALTER TABLE "Vouchers" DROP COLUMN IF EXISTS "RevocationReason";
                ALTER TABLE "Vouchers" DROP COLUMN IF EXISTS "RevokedByUserName";
                ALTER TABLE "Vouchers" DROP COLUMN IF EXISTS "RevokedByUserId";
                ALTER TABLE "Vouchers" DROP COLUMN IF EXISTS "RevokedAt";
                """);
        }
    }
}
