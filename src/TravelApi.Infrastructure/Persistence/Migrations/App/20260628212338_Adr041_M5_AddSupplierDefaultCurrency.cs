using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class Adr041_M5_AddSupplierDefaultCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rediseño alta de operador (2026-06-28): moneda por defecto del operador (ISO ARS/USD). ADITIVA
            // PURA: columna nullable con default 'ARS' a nivel BD -> las filas existentes quedan en pesos (la
            // moneda por defecto del sistema) SIN backfill destructivo y SIN NOT NULL sobre tabla poblada. La
            // validacion de "moneda soportada" la hace SupplierService (server-side). Down dropea la columna
            // (rollback limpio, no se pierde dato fuera de esta columna nueva).
            migrationBuilder.AddColumn<string>(
                name: "DefaultCurrency",
                table: "Suppliers",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true,
                defaultValue: "ARS");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultCurrency",
                table: "Suppliers");
        }
    }
}
