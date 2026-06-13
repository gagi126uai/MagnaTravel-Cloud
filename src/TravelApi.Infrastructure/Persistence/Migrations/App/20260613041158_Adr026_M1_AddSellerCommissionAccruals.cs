using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// Auditoria ERP 2026-06-12 (hallazgo #1, decision del dueño): comision del vendedor.
    /// Migracion 100% ADITIVA (EF puro, sin SQL crudo — leccion del trap M2 que rompio prod):
    ///
    /// <list type="bullet">
    ///   <item><b>EnableSellerCommissions</b> (bool, default false) en OperationalFinanceSettings: el
    ///   interruptor de negocio. Las filas existentes quedan en false (feature apagada), igual que hoy.</item>
    ///   <item><b>CommissionAccruals</b> (tabla nueva): comision devengada por (Reserva + Vendedor + Moneda).
    ///   FK a "TravelFiles" (Reserva) con Cascade. Indice unico (ReservaId, SellerUserId, Currency) que hace
    ///   idempotente el upsert del devengo; indice (SellerUserId, Status) para el listado de liquidacion.</item>
    /// </list>
    ///
    /// <para><b>Down (forward-only, igual que el resto del repo)</b>: dropea la tabla y la columna. No
    /// restaura datos (nacen vacios). En VPS la politica es roll-forward; el Down es para desarrollo local.</para>
    /// </summary>
    public partial class Adr026_M1_AddSellerCommissionAccruals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnableSellerCommissions",
                table: "OperationalFinanceSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "CommissionAccruals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SellerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ReservaId = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValue: "ARS"),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    RatePercent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommissionAccruals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommissionAccruals_TravelFiles_ReservaId",
                        column: x => x.ReservaId,
                        principalTable: "TravelFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommissionAccruals_ReservaId_SellerUserId_Currency",
                table: "CommissionAccruals",
                columns: new[] { "ReservaId", "SellerUserId", "Currency" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommissionAccruals_SellerUserId_Status",
                table: "CommissionAccruals",
                columns: new[] { "SellerUserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommissionAccruals");

            migrationBuilder.DropColumn(
                name: "EnableSellerCommissions",
                table: "OperationalFinanceSettings");
        }
    }
}
