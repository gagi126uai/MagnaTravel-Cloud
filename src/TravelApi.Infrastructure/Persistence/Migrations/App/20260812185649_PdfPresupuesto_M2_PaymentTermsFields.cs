using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class PdfPresupuesto_M2_PaymentTermsFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BudgetPaymentTermsText",
                table: "TravelFiles",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BudgetPaymentTermsTemplate",
                table: "AgencySettings",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BudgetPaymentTermsText",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "BudgetPaymentTermsTemplate",
                table: "AgencySettings");
        }
    }
}
