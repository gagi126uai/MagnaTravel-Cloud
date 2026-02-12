using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReferenceToPayment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Reference",
                table: "Payments",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Reference",
                table: "Payments");
        }
    }
}
