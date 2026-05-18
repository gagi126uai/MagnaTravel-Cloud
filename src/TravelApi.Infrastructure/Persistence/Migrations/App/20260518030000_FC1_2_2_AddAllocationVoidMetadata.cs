using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// FC1.2.2 (2026-05-18): trazabilidad del soft-void / reassociate de
    /// <c>OperatorRefundAllocation</c>.
    ///
    /// Agrega 3 columnas nullable:
    ///   - <c>VoidedAt</c>: UTC en que se marco <c>IsVoided=true</c>.
    ///   - <c>VoidedByUserId</c>: cashier/admin que ejecuto el void.
    ///   - <c>VoidedReason</c>: razon textual (min 20 chars validado en service).
    ///
    /// Por que separar de <c>IsVoided</c>: el flag operativo se mantiene para
    /// que el unique partial index (<c>WHERE IsVoided = false</c>) siga
    /// funcionando; estas 3 columnas guardan el contexto humano del cambio.
    ///
    /// **Rollback**: aditiva. <c>Down</c> dropea las 3 columnas sin perder datos
    /// preexistentes (FC1.2.2 todavia no commiteo en prod).
    /// </summary>
    public partial class FC1_2_2_AddAllocationVoidMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "VoidedAt",
                table: "OperatorRefundAllocations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VoidedByUserId",
                table: "OperatorRefundAllocations",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VoidedReason",
                table: "OperatorRefundAllocations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VoidedAt",
                table: "OperatorRefundAllocations");

            migrationBuilder.DropColumn(
                name: "VoidedByUserId",
                table: "OperatorRefundAllocations");

            migrationBuilder.DropColumn(
                name: "VoidedReason",
                table: "OperatorRefundAllocations");
        }
    }
}
