using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// G6 (caducidad de pre-venta, decision del dueño 2026-06-24): agrega dos enteros de configuracion de
    /// plataforma — los dias tras los cuales un Presupuesto (Budget) y una Cotizacion (Quotation) que no
    /// avanzan caducan a "Perdido" (Lost). Ambos NOT NULL con default 0 = DESACTIVADO (nada caduca hasta que
    /// el dueño elija un valor desde el panel). Aditiva y segura sobre datos existentes: las filas viejas
    /// quedan con 0 (caducidad apagada), igual que el default conservador del resto de los flags nuevos.
    /// </summary>
    public partial class Adr039_M1_AddPreSaleExpirationDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BudgetExpirationDays",
                table: "OperationalFinanceSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "QuotationExpirationDays",
                table: "OperationalFinanceSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BudgetExpirationDays",
                table: "OperationalFinanceSettings");

            migrationBuilder.DropColumn(
                name: "QuotationExpirationDays",
                table: "OperationalFinanceSettings");
        }
    }
}
