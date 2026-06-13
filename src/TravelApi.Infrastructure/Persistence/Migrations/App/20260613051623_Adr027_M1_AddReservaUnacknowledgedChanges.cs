using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// Auditoria ERP 2026-06-12 (hallazgo #10, decision del dueño): "confirmada con cambios". Cuando el
    /// operador confirma un servicio con otro precio/condicion, el vendedor edita el servicio y la reserva
    /// queda MARCADA para que el dueño la revise. Migracion 100% ADITIVA (EF puro, sin SQL crudo — leccion
    /// del trap M2 de ADR-020 que rompio prod):
    ///
    /// <list type="bullet">
    ///   <item><b>HasUnacknowledgedChanges</b> (bool, default false): bandera de "hay cambios sin revisar".
    ///   Las filas existentes quedan en false (ninguna reserva nace marcada).</item>
    ///   <item><b>ChangesPendingSince</b> (timestamptz, nullable): desde cuando esta pendiente la primera vez.</item>
    ///   <item><b>ChangesAckByUserId / ChangesAckByUserName / ChangesAckAt</b>: auditoria de quien dio el OK.</item>
    /// </list>
    ///
    /// <para>Tabla "TravelFiles" = la entidad Reserva (ToTable historico). <b>Down (forward-only, igual que
    /// el resto del repo)</b>: dropea las 5 columnas; no hay datos que restaurar (nacen vacias). En VPS la
    /// politica es roll-forward; el Down es para desarrollo local.</para>
    /// </summary>
    public partial class Adr027_M1_AddReservaUnacknowledgedChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ChangesAckAt",
                table: "TravelFiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ChangesAckByUserId",
                table: "TravelFiles",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ChangesAckByUserName",
                table: "TravelFiles",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ChangesPendingSince",
                table: "TravelFiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasUnacknowledgedChanges",
                table: "TravelFiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChangesAckAt",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "ChangesAckByUserId",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "ChangesAckByUserName",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "ChangesPendingSince",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "HasUnacknowledgedChanges",
                table: "TravelFiles");
        }
    }
}
