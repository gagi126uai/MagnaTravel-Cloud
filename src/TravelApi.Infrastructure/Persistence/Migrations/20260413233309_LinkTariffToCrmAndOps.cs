using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LinkTariffToCrmAndOps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RateId",
                table: "Reservations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RateId",
                table: "QuoteItems",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_RateId",
                table: "Reservations",
                column: "RateId");

            migrationBuilder.CreateIndex(
                name: "IX_QuoteItems_RateId",
                table: "QuoteItems",
                column: "RateId");

            migrationBuilder.AddForeignKey(
                name: "FK_QuoteItems_Rates_RateId",
                table: "QuoteItems",
                column: "RateId",
                principalTable: "Rates",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Reservations_Rates_RateId",
                table: "Reservations",
                column: "RateId",
                principalTable: "Rates",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_QuoteItems_Rates_RateId",
                table: "QuoteItems");

            migrationBuilder.DropForeignKey(
                name: "FK_Reservations_Rates_RateId",
                table: "Reservations");

            migrationBuilder.DropIndex(
                name: "IX_Reservations_RateId",
                table: "Reservations");

            migrationBuilder.DropIndex(
                name: "IX_QuoteItems_RateId",
                table: "QuoteItems");

            migrationBuilder.DropColumn(
                name: "RateId",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "RateId",
                table: "QuoteItems");
        }
    }
}
