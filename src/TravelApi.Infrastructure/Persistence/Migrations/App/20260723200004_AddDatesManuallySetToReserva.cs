using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// FIX (2026-07-23): columna nueva para que una correccion manual de fechas de la reserva
    /// (ReservaService.UpdateDatesAsync) no se pierda la proxima vez que se guarda un servicio.
    /// Default false: las filas existentes quedan con el comportamiento de SIEMPRE (recalculo
    /// automatico), sin ningun cambio para reservas ya cargadas.
    /// </summary>
    public partial class AddDatesManuallySetToReserva : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DatesManuallySet",
                table: "TravelFiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DatesManuallySet",
                table: "TravelFiles");
        }
    }
}
