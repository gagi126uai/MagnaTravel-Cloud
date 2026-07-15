using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class AddSupplierInvoiceApplicationReversals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SupplierInvoicePaymentApplications_SupplierInvoiceId_Suppli~",
                table: "SupplierInvoicePaymentApplications");

            migrationBuilder.AddColumn<bool>(
                name: "IsReversed",
                table: "SupplierInvoicePaymentApplications",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "SupplierInvoicePaymentApplicationReversals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierInvoicePaymentApplicationId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedByUserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierInvoicePaymentApplicationReversals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierInvoicePaymentApplicationReversals_SupplierInvoiceP~",
                        column: x => x.SupplierInvoicePaymentApplicationId,
                        principalTable: "SupplierInvoicePaymentApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierInvoicePaymentApplications_SupplierInvoiceId_Suppli~",
                table: "SupplierInvoicePaymentApplications",
                columns: new[] { "SupplierInvoiceId", "SupplierPaymentId" },
                unique: true,
                filter: "\"IsReversed\" = FALSE");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierInvoicePaymentApplicationReversals_SupplierInvoiceP~",
                table: "SupplierInvoicePaymentApplicationReversals",
                column: "SupplierInvoicePaymentApplicationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SupplierInvoicePaymentApplicationReversals");

            migrationBuilder.DropIndex(
                name: "IX_SupplierInvoicePaymentApplications_SupplierInvoiceId_Suppli~",
                table: "SupplierInvoicePaymentApplications");

            migrationBuilder.DropColumn(
                name: "IsReversed",
                table: "SupplierInvoicePaymentApplications");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierInvoicePaymentApplications_SupplierInvoiceId_Suppli~",
                table: "SupplierInvoicePaymentApplications",
                columns: new[] { "SupplierInvoiceId", "SupplierPaymentId" },
                unique: true);
        }
    }
}
