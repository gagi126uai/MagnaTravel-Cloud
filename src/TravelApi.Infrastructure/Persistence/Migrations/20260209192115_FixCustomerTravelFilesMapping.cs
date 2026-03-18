using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FixCustomerTravelFilesMapping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TravelFiles_Customers_CustomerId",
                table: "Reservas");

            migrationBuilder.DropIndex(
                name: "IX_TravelFiles_CustomerId",
                table: "Reservas");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "Reservas");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CustomerId",
                table: "Reservas",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TravelFiles_CustomerId",
                table: "Reservas",
                column: "CustomerId");

            migrationBuilder.AddForeignKey(
                name: "FK_TravelFiles_Customers_CustomerId",
                table: "Reservas",
                column: "CustomerId",
                principalTable: "Customers",
                principalColumn: "Id");
        }
    }
}
