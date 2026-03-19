using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTotalPaidToReserva : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ReservaAttachments_TravelFiles_TravelFileId",
                table: "ReservaAttachments");

            migrationBuilder.RenameColumn(
                name: "TravelFileId",
                table: "ReservaAttachments",
                newName: "ReservaId");

            migrationBuilder.RenameIndex(
                name: "IX_ReservaAttachments_TravelFileId",
                table: "ReservaAttachments",
                newName: "IX_ReservaAttachments_ReservaId");

            migrationBuilder.AddColumn<decimal>(
                name: "TotalPaid",
                table: "TravelFiles",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddForeignKey(
                name: "FK_ReservaAttachments_TravelFiles_ReservaId",
                table: "ReservaAttachments",
                column: "ReservaId",
                principalTable: "TravelFiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ReservaAttachments_TravelFiles_ReservaId",
                table: "ReservaAttachments");

            migrationBuilder.DropColumn(
                name: "TotalPaid",
                table: "TravelFiles");

            migrationBuilder.RenameColumn(
                name: "ReservaId",
                table: "ReservaAttachments",
                newName: "TravelFileId");

            migrationBuilder.RenameIndex(
                name: "IX_ReservaAttachments_ReservaId",
                table: "ReservaAttachments",
                newName: "IX_ReservaAttachments_TravelFileId");

            migrationBuilder.AddForeignKey(
                name: "FK_ReservaAttachments_TravelFiles_TravelFileId",
                table: "ReservaAttachments",
                column: "TravelFileId",
                principalTable: "TravelFiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
