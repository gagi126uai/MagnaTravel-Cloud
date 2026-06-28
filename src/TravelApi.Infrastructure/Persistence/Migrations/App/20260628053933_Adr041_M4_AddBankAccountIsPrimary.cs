using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class Adr041_M4_AddBankAccountIsPrimary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADR-041 TANDA 6 (2026-06-28): cuenta PRINCIPAL por dueño+moneda. ADITIVA PURA: agrega una columna
            // bool NOT NULL con default false -> las filas existentes quedan en false (ninguna principal) sin
            // backfill. La regla "una sola principal por (OwnerType, OwnerId, Currency)" la coordina el servicio
            // (no hay constraint fisica). Down dropea la columna (rollback limpio).
            migrationBuilder.AddColumn<bool>(
                name: "IsPrimary",
                table: "BankAccounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPrimary",
                table: "BankAccounts");
        }
    }
}
