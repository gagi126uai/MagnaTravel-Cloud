using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class Adr011_M1_AddExchangeRateQuotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "ExchangeRateFchCotiz",
                table: "Invoices",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExchangeRateQuoteId",
                table: "Invoices",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExchangeRateQuotes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    QuoteDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    Rate = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    ProviderName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ArcaFchCotiz = table.Column<DateOnly>(type: "date", nullable: true),
                    IsProductionSource = table.Column<bool>(type: "boolean", nullable: false),
                    SupersededByQuoteId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeRateQuotes", x => x.Id);
                    table.CheckConstraint("ck_ExchangeRateQuotes_rate_positive", "\"Rate\" > 0");
                    table.ForeignKey(
                        name: "FK_ExchangeRateQuotes_ExchangeRateQuotes_SupersededByQuoteId",
                        column: x => x.SupersededByQuoteId,
                        principalTable: "ExchangeRateQuotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_ExchangeRateQuoteId",
                table: "Invoices",
                column: "ExchangeRateQuoteId");

            migrationBuilder.CreateIndex(
                name: "ix_ExchangeRateQuotes_lookup",
                table: "ExchangeRateQuotes",
                columns: new[] { "Currency", "IsProductionSource", "QuoteDate" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRateQuotes_SupersededByQuoteId",
                table: "ExchangeRateQuotes",
                column: "SupersededByQuoteId");

            migrationBuilder.CreateIndex(
                name: "ux_ExchangeRateQuotes_currency_date_source_env",
                table: "ExchangeRateQuotes",
                columns: new[] { "Currency", "QuoteDate", "Source", "IsProductionSource" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Invoices_ExchangeRateQuotes_ExchangeRateQuoteId",
                table: "Invoices",
                column: "ExchangeRateQuoteId",
                principalTable: "ExchangeRateQuotes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Invoices_ExchangeRateQuotes_ExchangeRateQuoteId",
                table: "Invoices");

            migrationBuilder.DropTable(
                name: "ExchangeRateQuotes");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_ExchangeRateQuoteId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ExchangeRateFchCotiz",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ExchangeRateQuoteId",
                table: "Invoices");
        }
    }
}
