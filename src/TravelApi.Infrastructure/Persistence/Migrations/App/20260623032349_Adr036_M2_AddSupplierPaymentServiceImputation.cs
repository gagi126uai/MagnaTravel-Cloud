using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class Adr036_M2_AddSupplierPaymentServiceImputation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ServicePublicId",
                table: "SupplierPayments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ServiceRecordKind",
                table: "SupplierPayments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayment_Supplier_ServicePublicId",
                table: "SupplierPayments",
                columns: new[] { "SupplierId", "ServicePublicId" },
                filter: "\"ServicePublicId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SupplierPayment_Supplier_ServicePublicId",
                table: "SupplierPayments");

            migrationBuilder.DropColumn(
                name: "ServicePublicId",
                table: "SupplierPayments");

            migrationBuilder.DropColumn(
                name: "ServiceRecordKind",
                table: "SupplierPayments");
        }
    }
}
