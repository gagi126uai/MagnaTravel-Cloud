using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCommissionRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUsers_Agencies_AgencyId",
                table: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "AccountingLines");

            migrationBuilder.DropTable(
                name: "Agencies");

            migrationBuilder.DropTable(
                name: "BspImportRawRecords");

            migrationBuilder.DropTable(
                name: "BspReconciliationEntries");

            migrationBuilder.DropTable(
                name: "CupoAssignments");

            migrationBuilder.DropTable(
                name: "QuoteVersions");

            migrationBuilder.DropTable(
                name: "TariffValidities");

            migrationBuilder.DropTable(
                name: "TreasuryApplications");

            migrationBuilder.DropTable(
                name: "AccountingEntries");

            migrationBuilder.DropTable(
                name: "BspNormalizedRecords");

            migrationBuilder.DropTable(
                name: "Cupos");

            migrationBuilder.DropTable(
                name: "Quotes");

            migrationBuilder.DropTable(
                name: "Tariffs");

            migrationBuilder.DropTable(
                name: "TreasuryReceipts");

            migrationBuilder.DropTable(
                name: "BspImportBatches");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_AgencyId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "BasePrice",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "ReferenceCode",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "TotalAmount",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                table: "AspNetUsers");

            migrationBuilder.AddColumn<decimal>(
                name: "Balance",
                table: "TravelFiles",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "TravelFiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EndDate",
                table: "TravelFiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StartDate",
                table: "TravelFiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TotalCost",
                table: "TravelFiles",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TotalSale",
                table: "TravelFiles",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AlterColumn<string>(
                name: "TaxId",
                table: "Suppliers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Suppliers",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "Suppliers",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Address",
                table: "Suppliers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Suppliers",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "TaxCondition",
                table: "Suppliers",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ProductType",
                table: "Reservations",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.AlterColumn<int>(
                name: "CustomerId",
                table: "Reservations",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<decimal>(
                name: "Commission",
                table: "Reservations",
                type: "numeric(18,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,2)",
                oldPrecision: 12,
                oldScale: 2);

            migrationBuilder.AddColumn<string>(
                name: "ConfirmationNumber",
                table: "Reservations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Reservations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "NetCost",
                table: "Reservations",
                type: "numeric(18,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "SalePrice",
                table: "Reservations",
                type: "numeric(18,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "ServiceDetailsJson",
                table: "Reservations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ServiceType",
                table: "Reservations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SupplierId",
                table: "Reservations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Tax",
                table: "Reservations",
                type: "numeric(18,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AlterColumn<int>(
                name: "ReservationId",
                table: "Payments",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<decimal>(
                name: "Amount",
                table: "Payments",
                type: "numeric(18,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,2)",
                oldPrecision: 12,
                oldScale: 2);

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "Payments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TravelFileId",
                table: "Payments",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AgencySettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AgencyName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TaxId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Phone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DefaultCommissionPercent = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgencySettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CommissionRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SupplierId = table.Column<int>(type: "integer", nullable: true),
                    ServiceType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CommissionPercent = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommissionRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommissionRules_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Passengers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TravelFileId = table.Column<int>(type: "integer", nullable: false),
                    FullName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    DocumentNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    BirthDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Nationality = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Gender = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Passengers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Passengers_TravelFiles_TravelFileId",
                        column: x => x.TravelFileId,
                        principalTable: "TravelFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SupplierPayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SupplierId = table.Column<int>(type: "integer", nullable: false),
                    TravelFileId = table.Column<int>(type: "integer", nullable: true),
                    ReservationId = table.Column<int>(type: "integer", nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    PaidAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Method = table.Column<string>(type: "text", nullable: false),
                    Reference = table.Column<string>(type: "text", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierPayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierPayments_Reservations_ReservationId",
                        column: x => x.ReservationId,
                        principalTable: "Reservations",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SupplierPayments_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SupplierPayments_TravelFiles_TravelFileId",
                        column: x => x.TravelFileId,
                        principalTable: "TravelFiles",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_SupplierId",
                table: "Reservations",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_TravelFileId",
                table: "Payments",
                column: "TravelFileId");

            migrationBuilder.CreateIndex(
                name: "IX_CommissionRules_SupplierId",
                table: "CommissionRules",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_Passengers_TravelFileId",
                table: "Passengers",
                column: "TravelFileId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayments_ReservationId",
                table: "SupplierPayments",
                column: "ReservationId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayments_SupplierId",
                table: "SupplierPayments",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayments_TravelFileId",
                table: "SupplierPayments",
                column: "TravelFileId");

            migrationBuilder.AddForeignKey(
                name: "FK_Payments_TravelFiles_TravelFileId",
                table: "Payments",
                column: "TravelFileId",
                principalTable: "TravelFiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Reservations_Suppliers_SupplierId",
                table: "Reservations",
                column: "SupplierId",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Payments_TravelFiles_TravelFileId",
                table: "Payments");

            migrationBuilder.DropForeignKey(
                name: "FK_Reservations_Suppliers_SupplierId",
                table: "Reservations");

            migrationBuilder.DropTable(
                name: "AgencySettings");

            migrationBuilder.DropTable(
                name: "CommissionRules");

            migrationBuilder.DropTable(
                name: "Passengers");

            migrationBuilder.DropTable(
                name: "SupplierPayments");

            migrationBuilder.DropIndex(
                name: "IX_Reservations_SupplierId",
                table: "Reservations");

            migrationBuilder.DropIndex(
                name: "IX_Payments_TravelFileId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "Balance",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "EndDate",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "StartDate",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "TotalCost",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "TotalSale",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "Address",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "TaxCondition",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "ConfirmationNumber",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "NetCost",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "SalePrice",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "ServiceDetailsJson",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "ServiceType",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "SupplierId",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "Tax",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "TravelFileId",
                table: "Payments");

            migrationBuilder.AlterColumn<string>(
                name: "TaxId",
                table: "Suppliers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Suppliers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "Suppliers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ProductType",
                table: "Reservations",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<int>(
                name: "CustomerId",
                table: "Reservations",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "Commission",
                table: "Reservations",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 12,
                oldScale: 2);

            migrationBuilder.AddColumn<decimal>(
                name: "BasePrice",
                table: "Reservations",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "ReferenceCode",
                table: "Reservations",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "TotalAmount",
                table: "Reservations",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AlterColumn<int>(
                name: "ReservationId",
                table: "Payments",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "Amount",
                table: "Payments",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 12,
                oldScale: 2);

            migrationBuilder.AddColumn<int>(
                name: "AgencyId",
                table: "AspNetUsers",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccountingEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    EntryDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SourceReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Agencies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Address = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreditLimit = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    CurrentBalance = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    Email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    TaxId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agencies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BspImportBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FileName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Format = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ImportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BspImportBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Cupos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Capacity = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OverbookingLimit = table.Column<int>(type: "integer", nullable: false),
                    ProductType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Reserved = table.Column<int>(type: "integer", nullable: false),
                    RowVersion = table.Column<Guid>(type: "uuid", nullable: false),
                    TravelDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cupos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Quotes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CustomerId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReferenceCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Quotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Quotes_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Tariffs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    DefaultPrice = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProductType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tariffs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TreasuryReceipts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    Method = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TreasuryReceipts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccountingLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AccountingEntryId = table.Column<int>(type: "integer", nullable: false),
                    AccountCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Credit = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Debit = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountingLines_AccountingEntries_AccountingEntryId",
                        column: x => x.AccountingEntryId,
                        principalTable: "AccountingEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BspImportRawRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BspImportBatchId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    RawContent = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BspImportRawRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BspImportRawRecords_BspImportBatches_BspImportBatchId",
                        column: x => x.BspImportBatchId,
                        principalTable: "BspImportBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BspNormalizedRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BspImportBatchId = table.Column<int>(type: "integer", nullable: false),
                    BaseAmount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    IssueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReservationReference = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    TicketNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BspNormalizedRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BspNormalizedRecords_BspImportBatches_BspImportBatchId",
                        column: x => x.BspImportBatchId,
                        principalTable: "BspImportBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CupoAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CupoId = table.Column<int>(type: "integer", nullable: false),
                    ReservationId = table.Column<int>(type: "integer", nullable: true),
                    AssignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CupoAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CupoAssignments_Cupos_CupoId",
                        column: x => x.CupoId,
                        principalTable: "Cupos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CupoAssignments_Reservations_ReservationId",
                        column: x => x.ReservationId,
                        principalTable: "Reservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "QuoteVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    QuoteId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ProductType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    ValidUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuoteVersions_Quotes_QuoteId",
                        column: x => x.QuoteId,
                        principalTable: "Quotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TariffValidities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TariffId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TariffValidities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TariffValidities_Tariffs_TariffId",
                        column: x => x.TariffId,
                        principalTable: "Tariffs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TreasuryApplications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReservationId = table.Column<int>(type: "integer", nullable: false),
                    TreasuryReceiptId = table.Column<int>(type: "integer", nullable: false),
                    AmountApplied = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    AppliedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TreasuryApplications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TreasuryApplications_Reservations_ReservationId",
                        column: x => x.ReservationId,
                        principalTable: "Reservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TreasuryApplications_TreasuryReceipts_TreasuryReceiptId",
                        column: x => x.TreasuryReceiptId,
                        principalTable: "TreasuryReceipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BspReconciliationEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BspImportBatchId = table.Column<int>(type: "integer", nullable: false),
                    BspNormalizedRecordId = table.Column<int>(type: "integer", nullable: false),
                    ReservationId = table.Column<int>(type: "integer", nullable: true),
                    DifferenceAmount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    ReconciledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BspReconciliationEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BspReconciliationEntries_BspImportBatches_BspImportBatchId",
                        column: x => x.BspImportBatchId,
                        principalTable: "BspImportBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BspReconciliationEntries_BspNormalizedRecords_BspNormalized~",
                        column: x => x.BspNormalizedRecordId,
                        principalTable: "BspNormalizedRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BspReconciliationEntries_Reservations_ReservationId",
                        column: x => x.ReservationId,
                        principalTable: "Reservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_AgencyId",
                table: "AspNetUsers",
                column: "AgencyId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingLines_AccountingEntryId",
                table: "AccountingLines",
                column: "AccountingEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_BspImportRawRecords_BspImportBatchId",
                table: "BspImportRawRecords",
                column: "BspImportBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_BspNormalizedRecords_BspImportBatchId",
                table: "BspNormalizedRecords",
                column: "BspImportBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_BspReconciliationEntries_BspImportBatchId",
                table: "BspReconciliationEntries",
                column: "BspImportBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_BspReconciliationEntries_BspNormalizedRecordId",
                table: "BspReconciliationEntries",
                column: "BspNormalizedRecordId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BspReconciliationEntries_ReservationId",
                table: "BspReconciliationEntries",
                column: "ReservationId");

            migrationBuilder.CreateIndex(
                name: "IX_CupoAssignments_CupoId",
                table: "CupoAssignments",
                column: "CupoId");

            migrationBuilder.CreateIndex(
                name: "IX_CupoAssignments_ReservationId",
                table: "CupoAssignments",
                column: "ReservationId");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_CustomerId",
                table: "Quotes",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_QuoteVersions_QuoteId",
                table: "QuoteVersions",
                column: "QuoteId");

            migrationBuilder.CreateIndex(
                name: "IX_TariffValidities_TariffId",
                table: "TariffValidities",
                column: "TariffId");

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryApplications_ReservationId",
                table: "TreasuryApplications",
                column: "ReservationId");

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryApplications_TreasuryReceiptId",
                table: "TreasuryApplications",
                column: "TreasuryReceiptId");

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUsers_Agencies_AgencyId",
                table: "AspNetUsers",
                column: "AgencyId",
                principalTable: "Agencies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
