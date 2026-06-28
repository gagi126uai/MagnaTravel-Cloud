using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class Adr041_M1_AddBankAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADR-041 (2026-06-27): tabla de cuentas bancarias polimorficas (Agencia / Cliente / Proveedor).
            // ADITIVA PURA: solo crea una tabla nueva, no toca ni migra datos existentes. OwnerType/OwnerId
            // NO tienen FK fisica (una columna no puede apuntar a 3 tablas) — la integridad la valida el
            // servicio. El CHECK chk_BankAccounts_cbu_or_alias garantiza que ninguna fila quede sin CBU y sin
            // alias (sin eso no identifica un destino de plata). Indice (OwnerType, OwnerId, IsActive) para el
            // listado de cuentas activas por dueño. Down dropea la tabla entera (rollback limpio).
            migrationBuilder.CreateTable(
                name: "BankAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerType = table.Column<int>(type: "integer", nullable: false),
                    OwnerId = table.Column<int>(type: "integer", nullable: false),
                    Cbu = table.Column<string>(type: "character varying(22)", maxLength: 22, nullable: true),
                    Alias = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    HolderName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Bank = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AccountType = table.Column<int>(type: "integer", nullable: true),
                    HolderTaxId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankAccounts", x => x.Id);
                    table.CheckConstraint("chk_BankAccounts_cbu_or_alias", "\"Cbu\" IS NOT NULL OR \"Alias\" IS NOT NULL");
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankAccounts_OwnerType_OwnerId_IsActive",
                table: "BankAccounts",
                columns: new[] { "OwnerType", "OwnerId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_BankAccounts_PublicId",
                table: "BankAccounts",
                column: "PublicId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BankAccounts");
        }
    }
}
