using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// Configuracion de multas de cancelacion (2026-07-14): migracion 100% ADITIVA. Agrega
    /// <c>Suppliers.PenaltyBehavior</c> = int NOT NULL default <c>0</c> (= <see cref="Domain.Entities.SupplierPenaltyBehavior.Unknown"/>
    /// = "no se sabe"). Los operadores existentes quedan sin pista configurada; el paso de la multa de
    /// cancelacion no sugiere ningun camino hasta que alguien configure el operador a mano.
    ///
    /// <para><b>Rollback</b>: dropear la columna no pierde ningun dato de negocio real (es una pista, no un
    /// hecho fiscal) — solo se perderia la configuracion de "que tan seguido cobra" que alguien haya cargado.</para>
    /// </summary>
    public partial class Adr045_M1_AddSupplierPenaltyBehavior : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PenaltyBehavior",
                table: "Suppliers",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PenaltyBehavior",
                table: "Suppliers");
        }
    }
}
