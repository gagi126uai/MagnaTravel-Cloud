using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    [Migration("20260429000000_AddOperationalIndexes")]
    public partial class AddOperationalIndexes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_TravelFiles_PayerId_CreatedAt"
                    ON "TravelFiles" ("PayerId", "CreatedAt" DESC);

                CREATE INDEX IF NOT EXISTS "IX_TravelFiles_Status_Balance"
                    ON "TravelFiles" ("Status", "Balance");

                CREATE INDEX IF NOT EXISTS "IX_Passengers_TravelFileId_FullName"
                    ON "Passengers" ("TravelFileId", "FullName");

                CREATE INDEX IF NOT EXISTS "IX_Customers_DocumentNumber"
                    ON "Customers" ("DocumentNumber");

                CREATE INDEX IF NOT EXISTS "IX_Customers_Phone"
                    ON "Customers" ("Phone");

                CREATE INDEX IF NOT EXISTS "IX_Payments_TravelFileId_PaidAt"
                    ON "Payments" ("TravelFileId", "PaidAt" DESC);

                CREATE INDEX IF NOT EXISTS "IX_Payments_TravelFileId_Status"
                    ON "Payments" ("TravelFileId", "Status");

                CREATE INDEX IF NOT EXISTS "IX_Vouchers_Status_CreatedAt"
                    ON "Vouchers" ("Status", "CreatedAt" DESC);

                CREATE INDEX IF NOT EXISTS "IX_Vouchers_ReservaId_IsEnabledForSending"
                    ON "Vouchers" ("ReservaId", "IsEnabledForSending");

                CREATE INDEX IF NOT EXISTS "IX_VoucherAuditEntries_Action_OccurredAt"
                    ON "VoucherAuditEntries" ("Action", "OccurredAt" DESC);

                CREATE INDEX IF NOT EXISTS "IX_MessageDeliveries_Phone_CreatedAt"
                    ON "MessageDeliveries" ("Phone", "CreatedAt" DESC);

                CREATE INDEX IF NOT EXISTS "IX_MessageDeliveries_Status_Kind_CreatedAt"
                    ON "MessageDeliveries" ("Status", "Kind", "CreatedAt" DESC);

                CREATE INDEX IF NOT EXISTS "IX_ReservaAttachments_ReservaId_UploadedAt"
                    ON "ReservaAttachments" ("ReservaId", "UploadedAt" DESC);

                CREATE INDEX IF NOT EXISTS "IX_WhatsAppDeliveries_Phone_SentAt"
                    ON "WhatsAppDeliveries" ("Phone", "SentAt" DESC);
            """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_WhatsAppDeliveries_Phone_SentAt";
                DROP INDEX IF EXISTS "IX_ReservaAttachments_ReservaId_UploadedAt";
                DROP INDEX IF EXISTS "IX_MessageDeliveries_Status_Kind_CreatedAt";
                DROP INDEX IF EXISTS "IX_MessageDeliveries_Phone_CreatedAt";
                DROP INDEX IF EXISTS "IX_VoucherAuditEntries_Action_OccurredAt";
                DROP INDEX IF EXISTS "IX_Vouchers_ReservaId_IsEnabledForSending";
                DROP INDEX IF EXISTS "IX_Vouchers_Status_CreatedAt";
                DROP INDEX IF EXISTS "IX_Payments_TravelFileId_Status";
                DROP INDEX IF EXISTS "IX_Payments_TravelFileId_PaidAt";
                DROP INDEX IF EXISTS "IX_Customers_Phone";
                DROP INDEX IF EXISTS "IX_Customers_DocumentNumber";
                DROP INDEX IF EXISTS "IX_Passengers_TravelFileId_FullName";
                DROP INDEX IF EXISTS "IX_TravelFiles_Status_Balance";
                DROP INDEX IF EXISTS "IX_TravelFiles_PayerId_CreatedAt";
            """);
        }
    }
}
