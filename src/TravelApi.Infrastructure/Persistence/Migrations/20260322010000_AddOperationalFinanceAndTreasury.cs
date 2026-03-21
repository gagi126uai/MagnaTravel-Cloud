using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260322010000_AddOperationalFinanceAndTreasury")]
    public partial class AddOperationalFinanceAndTreasury : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResponsibleUserId",
                table: "TravelFiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AffectsCash",
                table: "Payments",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "EntryType",
                table: "Payments",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Payment");

            migrationBuilder.AddColumn<int>(
                name: "OriginalPaymentId",
                table: "Payments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RelatedInvoiceId",
                table: "Payments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WasForced",
                table: "Invoices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ForceReason",
                table: "Invoices",
                type: "character varying(1000)",
                maxLength: 1000,
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

            migrationBuilder.AddColumn<DateTime>(
                name: "ForcedAt",
                table: "Invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OutstandingBalanceAtIssuance",
                table: "Invoices",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "ManualCashMovements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
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

            migrationBuilder.InsertData(
                table: "OperationalFinanceSettings",
                columns: new[]
                {
                    "Id",
                    "RequireFullPaymentForOperativeStatus",
                    "RequireFullPaymentForVoucher",
                    "AfipInvoiceControlMode",
                    "EnableUpcomingUnpaidReservationNotifications",
                    "UpcomingUnpaidReservationAlertDays",
                    "CreatedAt",
                    "UpdatedAt"
                },
                values: new object[]
                {
                    1,
                    true,
                    true,
                    "AllowAgentOverrideWithReason",
                    true,
                    7,
                    new DateTime(2026, 3, 22, 0, 0, 0, DateTimeKind.Utc),
                    new DateTime(2026, 3, 22, 0, 0, 0, DateTimeKind.Utc)
                });

            migrationBuilder.CreateIndex(
                name: "IX_TravelFiles_ResponsibleUserId",
                table: "TravelFiles",
                column: "ResponsibleUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_OriginalPaymentId",
                table: "Payments",
                column: "OriginalPaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_RelatedInvoiceId",
                table: "Payments",
                column: "RelatedInvoiceId");

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
                name: "IX_TravelFiles_ResponsibleUserId",
                table: "TravelFiles");

            migrationBuilder.DropIndex(
                name: "IX_Payments_OriginalPaymentId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_RelatedInvoiceId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ResponsibleUserId",
                table: "TravelFiles");

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
                name: "RelatedInvoiceId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "WasForced",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ForceReason",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ForcedByUserId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ForcedByUserName",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ForcedAt",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "OutstandingBalanceAtIssuance",
                table: "Invoices");
        }
    }
}
