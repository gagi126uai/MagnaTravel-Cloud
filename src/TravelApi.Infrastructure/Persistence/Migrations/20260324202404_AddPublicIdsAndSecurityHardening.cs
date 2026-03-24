using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicIdsAndSecurityHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""CREATE EXTENSION IF NOT EXISTS pgcrypto;""");

            var publicIdTargets = new (string Table, string Index)[]
            {
                ("Customers", "IX_Customers_PublicId"),
                ("FlightSegments", "IX_FlightSegments_PublicId"),
                ("HotelBookings", "IX_HotelBookings_PublicId"),
                ("Invoices", "IX_Invoices_PublicId"),
                ("LeadActivities", "IX_LeadActivities_PublicId"),
                ("Leads", "IX_Leads_PublicId"),
                ("ManualCashMovements", "IX_ManualCashMovements_PublicId"),
                ("PackageBookings", "IX_PackageBookings_PublicId"),
                ("Passengers", "IX_Passengers_PublicId"),
                ("PaymentReceipts", "IX_PaymentReceipts_PublicId"),
                ("Payments", "IX_Payments_PublicId"),
                ("Quotes", "IX_Quotes_PublicId"),
                ("QuoteItems", "IX_QuoteItems_PublicId"),
                ("Reservations", "IX_Reservations_PublicId"),
                ("ReservaAttachments", "IX_ReservaAttachments_PublicId"),
                ("Suppliers", "IX_Suppliers_PublicId"),
                ("SupplierPayments", "IX_SupplierPayments_PublicId"),
                ("TransferBookings", "IX_TransferBookings_PublicId"),
                ("TravelFiles", "IX_TravelFiles_PublicId")
            };

            foreach (var (table, index) in publicIdTargets)
            {
                migrationBuilder.Sql($"""ALTER TABLE "{table}" ADD COLUMN IF NOT EXISTS "PublicId" uuid NOT NULL DEFAULT gen_random_uuid();""");
                migrationBuilder.Sql($"""CREATE UNIQUE INDEX IF NOT EXISTS "{index}" ON "{table}" ("PublicId");""");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var publicIdTargets = new (string Table, string Index)[]
            {
                ("Customers", "IX_Customers_PublicId"),
                ("FlightSegments", "IX_FlightSegments_PublicId"),
                ("HotelBookings", "IX_HotelBookings_PublicId"),
                ("Invoices", "IX_Invoices_PublicId"),
                ("LeadActivities", "IX_LeadActivities_PublicId"),
                ("Leads", "IX_Leads_PublicId"),
                ("ManualCashMovements", "IX_ManualCashMovements_PublicId"),
                ("PackageBookings", "IX_PackageBookings_PublicId"),
                ("Passengers", "IX_Passengers_PublicId"),
                ("PaymentReceipts", "IX_PaymentReceipts_PublicId"),
                ("Payments", "IX_Payments_PublicId"),
                ("Quotes", "IX_Quotes_PublicId"),
                ("QuoteItems", "IX_QuoteItems_PublicId"),
                ("Reservations", "IX_Reservations_PublicId"),
                ("ReservaAttachments", "IX_ReservaAttachments_PublicId"),
                ("Suppliers", "IX_Suppliers_PublicId"),
                ("SupplierPayments", "IX_SupplierPayments_PublicId"),
                ("TransferBookings", "IX_TransferBookings_PublicId"),
                ("TravelFiles", "IX_TravelFiles_PublicId")
            };

            foreach (var (table, index) in publicIdTargets)
            {
                migrationBuilder.Sql($"""DROP INDEX IF EXISTS "{index}";""");
                migrationBuilder.Sql($"""ALTER TABLE "{table}" DROP COLUMN IF EXISTS "PublicId";""");
            }
        }
    }
}
