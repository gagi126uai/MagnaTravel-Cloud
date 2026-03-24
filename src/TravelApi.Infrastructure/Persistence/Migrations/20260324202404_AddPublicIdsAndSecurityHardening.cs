using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicIdsAndSecurityHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pgcrypto", ",,");

            migrationBuilder.AddColumn<Guid>(
                name: "PublicId",
                table: "TravelFiles",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<string>(
                name: "ResponsibleUserId",
                table: "TravelFiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PublicId",
                table: "TransferBookings",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<Guid>(
                name: "PublicId",
                table: "Suppliers",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<Guid>(
                name: "PublicId",
                table: "SupplierPayments",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<Guid>(
                name: "PublicId",
                table: "Reservations",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<Guid>(
                name: "PublicId",
                table: "ReservaAttachments",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<Guid>(
                name: "PublicId",
                table: "Quotes",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<Guid>(
                name: "PublicId",
                table: "QuoteItems",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<bool>(
                name: "AffectsCash",
                table: "Payments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "EntryType",
                table: "Payments",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "OriginalPaymentId",
                table: "Payments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PublicId",
                table: "Payments",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<int>(
                name: "RelatedInvoiceId",
                table: "Payments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PublicId",
                table: "Passengers",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<Guid>(
                name: "PublicId",
                table: "PackageBookings",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<Guid>(
                name: "PublicId",
                table: "Leads",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<Guid>(
                name: "PublicId",
                table: "LeadActivities",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<string>(
                name: "ForceReason",
                table: "Invoices",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ForcedAt",
                table: "Invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ForcedByUserId",
                table: "Invoices",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ForcedByUserName",
                table: "Invoices",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OutstandingBalanceAtIssuance",
                table: "Invoices",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "PublicId",
                table: "Invoices",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<bool>(
                name: "WasForced",
                table: "Invoices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "PublicId",
                table: "HotelBookings",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<Guid>(
                name: "PublicId",
                table: "FlightSegments",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<Guid>(
                name: "PublicId",
                table: "Customers",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.CreateTable(
                name: "ManualCashMovements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Direction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Method = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsVoided = table.Column<bool>(type: "boolean", nullable: false),
                    VoidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RelatedReservaId = table.Column<int>(type: "integer", nullable: true),
                    RelatedSupplierId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualCashMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManualCashMovements_Suppliers_RelatedSupplierId",
                        column: x => x.RelatedSupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ManualCashMovements_TravelFiles_RelatedReservaId",
                        column: x => x.RelatedReservaId,
                        principalTable: "TravelFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "OperationalFinanceSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RequireFullPaymentForOperativeStatus = table.Column<bool>(type: "boolean", nullable: false),
                    RequireFullPaymentForVoucher = table.Column<bool>(type: "boolean", nullable: false),
                    AfipInvoiceControlMode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    EnableUpcomingUnpaidReservationNotifications = table.Column<bool>(type: "boolean", nullable: false),
                    UpcomingUnpaidReservationAlertDays = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationalFinanceSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaymentReceipts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PaymentId = table.Column<int>(type: "integer", nullable: false),
                    ReservaId = table.Column<int>(type: "integer", nullable: false),
                    ReceiptNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    IssuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    VoidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentReceipts_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentReceipts_TravelFiles_ReservaId",
                        column: x => x.ReservaId,
                        principalTable: "TravelFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TravelFiles_PublicId",
                table: "TravelFiles",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TravelFiles_ResponsibleUserId",
                table: "TravelFiles",
                column: "ResponsibleUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TransferBookings_PublicId",
                table: "TransferBookings",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_PublicId",
                table: "Suppliers",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayments_PublicId",
                table: "SupplierPayments",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_PublicId",
                table: "Reservations",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReservaAttachments_PublicId",
                table: "ReservaAttachments",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_PublicId",
                table: "Quotes",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuoteItems_PublicId",
                table: "QuoteItems",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_OriginalPaymentId",
                table: "Payments",
                column: "OriginalPaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_PublicId",
                table: "Payments",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_RelatedInvoiceId",
                table: "Payments",
                column: "RelatedInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Passengers_PublicId",
                table: "Passengers",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PackageBookings_PublicId",
                table: "PackageBookings",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Leads_PublicId",
                table: "Leads",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeadActivities_PublicId",
                table: "LeadActivities",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_PublicId",
                table: "Invoices",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HotelBookings_PublicId",
                table: "HotelBookings",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FlightSegments_PublicId",
                table: "FlightSegments",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Customers_PublicId",
                table: "Customers",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManualCashMovements_PublicId",
                table: "ManualCashMovements",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManualCashMovements_RelatedReservaId",
                table: "ManualCashMovements",
                column: "RelatedReservaId");

            migrationBuilder.CreateIndex(
                name: "IX_ManualCashMovements_RelatedSupplierId",
                table: "ManualCashMovements",
                column: "RelatedSupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReceipts_PaymentId",
                table: "PaymentReceipts",
                column: "PaymentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReceipts_PublicId",
                table: "PaymentReceipts",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReceipts_ReservaId",
                table: "PaymentReceipts",
                column: "ReservaId");

            migrationBuilder.AddForeignKey(
                name: "FK_Payments_Invoices_RelatedInvoiceId",
                table: "Payments",
                column: "RelatedInvoiceId",
                principalTable: "Invoices",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Payments_Payments_OriginalPaymentId",
                table: "Payments",
                column: "OriginalPaymentId",
                principalTable: "Payments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_TravelFiles_AspNetUsers_ResponsibleUserId",
                table: "TravelFiles",
                column: "ResponsibleUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Payments_Invoices_RelatedInvoiceId",
                table: "Payments");

            migrationBuilder.DropForeignKey(
                name: "FK_Payments_Payments_OriginalPaymentId",
                table: "Payments");

            migrationBuilder.DropForeignKey(
                name: "FK_TravelFiles_AspNetUsers_ResponsibleUserId",
                table: "TravelFiles");

            migrationBuilder.DropTable(
                name: "ManualCashMovements");

            migrationBuilder.DropTable(
                name: "OperationalFinanceSettings");

            migrationBuilder.DropTable(
                name: "PaymentReceipts");

            migrationBuilder.DropIndex(
                name: "IX_TravelFiles_PublicId",
                table: "TravelFiles");

            migrationBuilder.DropIndex(
                name: "IX_TravelFiles_ResponsibleUserId",
                table: "TravelFiles");

            migrationBuilder.DropIndex(
                name: "IX_TransferBookings_PublicId",
                table: "TransferBookings");

            migrationBuilder.DropIndex(
                name: "IX_Suppliers_PublicId",
                table: "Suppliers");

            migrationBuilder.DropIndex(
                name: "IX_SupplierPayments_PublicId",
                table: "SupplierPayments");

            migrationBuilder.DropIndex(
                name: "IX_Reservations_PublicId",
                table: "Reservations");

            migrationBuilder.DropIndex(
                name: "IX_ReservaAttachments_PublicId",
                table: "ReservaAttachments");

            migrationBuilder.DropIndex(
                name: "IX_Quotes_PublicId",
                table: "Quotes");

            migrationBuilder.DropIndex(
                name: "IX_QuoteItems_PublicId",
                table: "QuoteItems");

            migrationBuilder.DropIndex(
                name: "IX_Payments_OriginalPaymentId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_PublicId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_RelatedInvoiceId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Passengers_PublicId",
                table: "Passengers");

            migrationBuilder.DropIndex(
                name: "IX_PackageBookings_PublicId",
                table: "PackageBookings");

            migrationBuilder.DropIndex(
                name: "IX_Leads_PublicId",
                table: "Leads");

            migrationBuilder.DropIndex(
                name: "IX_LeadActivities_PublicId",
                table: "LeadActivities");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_PublicId",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_HotelBookings_PublicId",
                table: "HotelBookings");

            migrationBuilder.DropIndex(
                name: "IX_FlightSegments_PublicId",
                table: "FlightSegments");

            migrationBuilder.DropIndex(
                name: "IX_Customers_PublicId",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "ResponsibleUserId",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "TransferBookings");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "SupplierPayments");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "ReservaAttachments");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "QuoteItems");

            migrationBuilder.DropColumn(
                name: "AffectsCash",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "EntryType",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "OriginalPaymentId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "RelatedInvoiceId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "Passengers");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "PackageBookings");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "LeadActivities");

            migrationBuilder.DropColumn(
                name: "ForceReason",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ForcedAt",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ForcedByUserId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ForcedByUserName",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "OutstandingBalanceAtIssuance",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "WasForced",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "HotelBookings");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "Customers");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:pgcrypto", ",,");
        }
    }
}
