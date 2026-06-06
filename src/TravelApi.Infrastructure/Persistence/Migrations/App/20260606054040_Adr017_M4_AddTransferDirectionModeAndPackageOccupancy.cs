using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// Ficha de carga F2 (guia-ux-gaston): da columna propia a 3 campos estructurados que el front
    /// hasta ahora metia a la fuerza en <c>Notes</c> (lo que PISABA la nota real del usuario — bug).
    ///
    /// <para>Que agrega (todo ADITIVO, varchar(20) nullable):
    /// <list type="bullet">
    ///   <item><c>TransferBookings.Direction</c>: sentido del traslado ("in" = llegada / "out" = salida).</item>
    ///   <item><c>TransferBookings.ServiceMode</c>: modalidad ("private" = privado / "shared" = compartido).</item>
    ///   <item><c>PackageBookings.OccupancyBase</c>: base de ocupacion ("double"/"triple"/etc).</item>
    /// </list></para>
    ///
    /// <para>Son metadatos OPERATIVOS: no tocan costos, saldo, factura ni nada fiscal. Las filas
    /// existentes quedan en NULL (= legacy / no informado), por eso no hay backfill ni default.</para>
    ///
    /// <para><b>Orden de deploy / R8</b>: M4, encolada DETRAS de la cola pendiente del VPS (la ultima
    /// era Adr017_M3, timestamp 20260606033220), sin tocar ni reordenar ninguna. NO se aplica desde aca.</para>
    /// </summary>
    public partial class Adr017_M4_AddTransferDirectionModeAndPackageOccupancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Direction",
                table: "TransferBookings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ServiceMode",
                table: "TransferBookings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OccupancyBase",
                table: "PackageBookings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Direction",
                table: "TransferBookings");

            migrationBuilder.DropColumn(
                name: "ServiceMode",
                table: "TransferBookings");

            migrationBuilder.DropColumn(
                name: "OccupancyBase",
                table: "PackageBookings");
        }
    }
}
