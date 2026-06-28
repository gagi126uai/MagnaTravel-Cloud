using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class Adr041_M3_AddSupplierDefaultPaymentTermDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADR-041 TANDA 5 (2026-06-27): plazo de pago por defecto del operador, en dias. ADITIVA PURA:
            // columna nullable sin default y sin backfill -> las filas existentes quedan en null = "sin plazo"
            // = comportamiento actual (no muestran vencimiento sugerido). Seguimos prepago: NO bloquea nada.
            migrationBuilder.AddColumn<int>(
                name: "DefaultPaymentTermDays",
                table: "Suppliers",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultPaymentTermDays",
                table: "Suppliers");
        }
    }
}
