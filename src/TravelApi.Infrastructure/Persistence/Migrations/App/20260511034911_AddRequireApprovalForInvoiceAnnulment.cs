using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// B1.15 Fase D (2026-05-11): setting <c>RequireApprovalForInvoiceAnnulment</c>
    /// que controla si la anulacion de factura requiere ApprovalRequest previo.
    /// Default <c>true</c> para que el flujo sea opt-out en lugar de opt-in (la
    /// recomendacion fiscal es siempre requerir aprobacion). Admin bypassa.
    /// </summary>
    public partial class AddRequireApprovalForInvoiceAnnulment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequireApprovalForInvoiceAnnulment",
                table: "OperationalFinanceSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequireApprovalForInvoiceAnnulment",
                table: "OperationalFinanceSettings");
        }
    }
}
