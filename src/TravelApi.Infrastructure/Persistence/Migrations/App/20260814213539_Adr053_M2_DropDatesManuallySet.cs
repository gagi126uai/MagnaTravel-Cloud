using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// ADR-053 (2026-08-13, D5/D6.2): migración DESTRUCTIVA, dropea la columna vieja
    /// <c>TravelFiles."DatesManuallySet"</c> (el candado invisible que frenaba el recálculo automático de
    /// <c>StartDate</c>/<c>EndDate</c>, reemplazado por el estado VISIBLE
    /// <c>Reserva.NeedsDateRecalculation</c> + botón "volver a calcular").
    ///
    /// <para><b>Por qué va SOLA, en un release APARTE</b>: si este <c>DROP COLUMN</c> se deployara junto
    /// con las columnas nuevas de <c>Adr053_M1</c>, habría una ventana real de rotura durante el deploy —
    /// los contenedores <c>api</c>/<c>worker</c> VIEJOS siguen sirviendo tráfico un rato después de que
    /// <c>migrate</c> termina de correr, y ese código viejo todavía lee/escribe
    /// <c>DatesManuallySet</c> (ver <c>deploy.sh</c> y D6.2 del ADR). Separarla la elimina: para cuando esta
    /// migración corre, <c>Adr053_M1</c> y el código de F1 (que ya NO tocan esa columna) llevan tiempo
    /// deployados y estables en PROD.</para>
    ///
    /// <para><b>Down</b>: recrea la columna como <c>boolean NOT NULL DEFAULT false</c> — vuelve al estado
    /// previo a nivel de esquema, pero el código que la leía ya se retiró en F1, así que un rollback de
    /// esta migración sola no restaura el comportamiento viejo.</para>
    /// </summary>
    public partial class Adr053_M2_DropDatesManuallySet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DatesManuallySet",
                table: "TravelFiles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DatesManuallySet",
                table: "TravelFiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
