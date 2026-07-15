using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class AddSupplierInvoiceOpenItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SupplierInvoices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierId = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    IssuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedByUserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    VoidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VoidReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierInvoices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierInvoices_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierInvoiceLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierInvoiceId = table.Column<int>(type: "integer", nullable: false),
                    ReservaId = table.Column<int>(type: "integer", nullable: false),
                    ServiceRecordKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ServicePublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierInvoiceLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierInvoiceLines_SupplierInvoices_SupplierInvoiceId",
                        column: x => x.SupplierInvoiceId,
                        principalTable: "SupplierInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SupplierInvoiceLines_TravelFiles_ReservaId",
                        column: x => x.ReservaId,
                        principalTable: "TravelFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierInvoicePaymentApplications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierInvoiceId = table.Column<int>(type: "integer", nullable: false),
                    SupplierPaymentId = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedByUserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierInvoicePaymentApplications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierInvoicePaymentApplications_SupplierInvoices_Supplie~",
                        column: x => x.SupplierInvoiceId,
                        principalTable: "SupplierInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SupplierInvoicePaymentApplications_SupplierPayments_Supplie~",
                        column: x => x.SupplierPaymentId,
                        principalTable: "SupplierPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierInvoiceLines_ReservaId",
                table: "SupplierInvoiceLines",
                column: "ReservaId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierInvoiceLines_ServiceRecordKind_ServicePublicId",
                table: "SupplierInvoiceLines",
                columns: new[] { "ServiceRecordKind", "ServicePublicId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierInvoiceLines_SupplierInvoiceId_ServiceRecordKind_Se~",
                table: "SupplierInvoiceLines",
                columns: new[] { "SupplierInvoiceId", "ServiceRecordKind", "ServicePublicId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierInvoicePaymentApplications_SupplierInvoiceId_Suppli~",
                table: "SupplierInvoicePaymentApplications",
                columns: new[] { "SupplierInvoiceId", "SupplierPaymentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierInvoicePaymentApplications_SupplierPaymentId",
                table: "SupplierInvoicePaymentApplications",
                column: "SupplierPaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierInvoices_SupplierId_Currency_Status_DueDate",
                table: "SupplierInvoices",
                columns: new[] { "SupplierId", "Currency", "Status", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierInvoices_SupplierId_Number",
                table: "SupplierInvoices",
                columns: new[] { "SupplierId", "Number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SupplierInvoiceLines");

            migrationBuilder.DropTable(
                name: "SupplierInvoicePaymentApplications");

            migrationBuilder.DropTable(
                name: "SupplierInvoices");
        }
    }
}
