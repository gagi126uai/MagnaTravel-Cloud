using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIsActiveToCustomer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CustomerId",
                table: "TravelFiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ProductType",
                table: "Reservations",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Customers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_TravelFiles_CustomerId",
                table: "TravelFiles",
                column: "CustomerId");

            migrationBuilder.AddForeignKey(
                name: "FK_TravelFiles_Customers_CustomerId",
                table: "TravelFiles",
                column: "CustomerId",
                principalTable: "Customers",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TravelFiles_Customers_CustomerId",
                table: "TravelFiles");

            migrationBuilder.DropIndex(
                name: "IX_TravelFiles_CustomerId",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "Customers");

            migrationBuilder.AlterColumn<string>(
                name: "ProductType",
                table: "Reservations",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
