using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// Auditoria ERP 2026-06-13 (dos features del dueño en una migracion 100% ADITIVA y EF PURA, sin SQL crudo
    /// — leccion del trap M2 de ADR-020 que rompio prod):
    ///
    /// <list type="bullet">
    ///   <item><b>SellerCommissionPercent</b> (numeric(5,2), default 0): porcentaje UNICO de comision del
    ///   vendedor, parejo para todas las reservas (el dueño saco las reglas por operador/tipo). Las filas
    ///   existentes quedan en 0 (no se devenga nada hasta que el dueño elija un numero). Tabla
    ///   "OperationalFinanceSettings" (singleton).</item>
    ///   <item><b>ReservaPendingChanges</b> (tabla nueva): detalle de los cambios de precio/costo que dejan la
    ///   reserva "confirmada con cambios" (que servicio, que campo, antes/despues, moneda, quien/cuando). FK
    ///   Cascade a "TravelFiles" (= entidad Reserva): al borrar la reserva se borran sus cambios. No toca
    ///   ninguna columna existente.</item>
    /// </list>
    ///
    /// <para><b>Rollback</b>: <c>Down</c> dropea la tabla y la columna. Sin perdida de dato critico: ninguna
    /// reserva nace con cambios pendientes y la comision se recalcula sola en el proximo movimiento de plata.
    /// En VPS la politica es roll-forward; el Down es para desarrollo local.</para>
    /// </summary>
    public partial class Adr027_M2_AddReservaPendingChangesAndCommissionPercent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "SellerCommissionPercent",
                table: "OperationalFinanceSettings",
                type: "numeric(5,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "ReservaPendingChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReservaId = table.Column<int>(type: "integer", nullable: false),
                    ServiceType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ServiceDescription = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ServicePublicId = table.Column<Guid>(type: "uuid", nullable: true),
                    Field = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OldValue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    NewValue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValue: "ARS"),
                    ChangedByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ChangedByUserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReservaPendingChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReservaPendingChanges_TravelFiles_ReservaId",
                        column: x => x.ReservaId,
                        principalTable: "TravelFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReservaPendingChanges_ReservaId",
                table: "ReservaPendingChanges",
                column: "ReservaId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReservaPendingChanges");

            migrationBuilder.DropColumn(
                name: "SellerCommissionPercent",
                table: "OperationalFinanceSettings");
        }
    }
}
