using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class Adr044_M_T5a_AddTargetInvoiceIdAndConfirmedGrossCreditAmountToLine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ConfirmedGrossCreditAmount",
                table: "BookingCancellationLines",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreditAmountConfirmedAt",
                table: "BookingCancellationLines",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreditAmountConfirmedByUserId",
                table: "BookingCancellationLines",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreditAmountConfirmedByUserName",
                table: "BookingCancellationLines",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetInvoiceId",
                table: "BookingCancellationLines",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellationLines_TargetInvoiceId",
                table: "BookingCancellationLines",
                column: "TargetInvoiceId");

            migrationBuilder.AddForeignKey(
                name: "FK_BookingCancellationLines_Invoices_TargetInvoiceId",
                table: "BookingCancellationLines",
                column: "TargetInvoiceId",
                principalTable: "Invoices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BookingCancellationLines_Invoices_TargetInvoiceId",
                table: "BookingCancellationLines");

            migrationBuilder.DropIndex(
                name: "IX_BookingCancellationLines_TargetInvoiceId",
                table: "BookingCancellationLines");

            migrationBuilder.DropColumn(
                name: "ConfirmedGrossCreditAmount",
                table: "BookingCancellationLines");

            migrationBuilder.DropColumn(
                name: "CreditAmountConfirmedAt",
                table: "BookingCancellationLines");

            migrationBuilder.DropColumn(
                name: "CreditAmountConfirmedByUserId",
                table: "BookingCancellationLines");

            migrationBuilder.DropColumn(
                name: "CreditAmountConfirmedByUserName",
                table: "BookingCancellationLines");

            migrationBuilder.DropColumn(
                name: "TargetInvoiceId",
                table: "BookingCancellationLines");
        }
    }
}
