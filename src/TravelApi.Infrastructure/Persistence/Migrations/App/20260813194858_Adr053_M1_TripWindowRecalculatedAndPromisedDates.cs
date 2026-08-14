using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using TravelApi.Infrastructure.Reservations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// ADR-053 (2026-08-13, "fechas del viaje calculadas y de solo lectura" — D5): migración 100%
    /// ADITIVA, SIN riesgo de ventana de deploy (el código VIEJO simplemente ignora las columnas nuevas
    /// que no conoce). El único paso DESTRUCTIVO de esta obra (<c>DROP COLUMN "DatesManuallySet"</c>) va
    /// en <c>Adr053_M2_DropDatesManuallySet</c>, deployada en un release APARTE — ver D6.2 del ADR para el
    /// motivo (ventana real de rotura verificada contra <c>deploy.sh</c>: los contenedores <c>api</c>/
    /// <c>worker</c> viejos siguen sirviendo tráfico un rato después de que <c>migrate</c> termina).
    ///
    /// <para><b>Qué agrega</b>: <c>PromisedStartDate</c>/<c>PromisedEndDate</c> (par manual, "fecha
    /// prometida", D3), <c>NeedsDateRecalculation</c> (estado visible que reemplaza al candado invisible
    /// <c>DatesManuallySet</c>, D4), <c>PendingScheduleWarning</c>/<c>PendingScheduleWarningByUserId</c>
    /// (aviso suave efímero, D2.1), y la tabla <c>Adr053TripWindowBackfillLogs</c> (rastro DURABLE del
    /// backfill, se conserva para siempre — D5).</para>
    ///
    /// <para><b>BACKFILL, una sola vez (marcador = <c>__EFMigrationsHistory</c>)</b>: decisión EXPLÍCITA
    /// del dueño (2026-08-11, contra la recomendación del arquitecto de dejar las reservas viejas como
    /// están): recalcula <c>TravelFiles."StartDate"</c>/<c>"EndDate"</c> de TODAS las reservas existentes,
    /// con el predicado CANÓNICO de "vigente" (D1.1 — el mismo que <c>ReservaScheduleCalculator.ComputeAsync</c>
    /// en C#, NO el literal case-sensitive de <c>UpcomingStartCalculator</c>). El texto SQL vive en
    /// <see cref="Adr053BackfillSql"/> (NO inline acá) para que el test de integración
    /// <c>Adr053BackfillSqlIntegrationTests</c> corra el MISMO SQL que esta migración — si alguien edita
    /// uno sin tocar el otro, compila igual pero el test corre el SQL VIEJO.</para>
    ///
    /// <para><b>Por qué es SEGURA/reversible</b>: las 5 columnas nuevas nacen <c>NULL</c>/<c>false</c> por
    /// el <c>AddColumn</c> de arriba; el backfill solo RELLENA <c>StartDate</c>/<c>EndDate</c> (puede
    /// CAMBIAR un valor que ya existía — a diferencia de otros backfills aditivos de este repo, acá es
    /// intencional: una reserva vieja con un servicio anulado que hoy contaba en el MIN/MAX puede terminar
    /// con otra ventana). El <c>Down</c> dropea las columnas y la tabla nueva — NO recrea
    /// <c>DatesManuallySet</c> (esa columna sigue viva hasta <c>Adr053_M2</c>).</para>
    /// </summary>
    public partial class Adr053_M1_TripWindowRecalculatedAndPromisedDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NeedsDateRecalculation",
                table: "TravelFiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PendingScheduleWarning",
                table: "TravelFiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingScheduleWarningByUserId",
                table: "TravelFiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PromisedEndDate",
                table: "TravelFiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PromisedStartDate",
                table: "TravelFiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Adr053TripWindowBackfillLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReservaId = table.Column<int>(type: "integer", nullable: false),
                    OldStartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OldEndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NewStartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NewEndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MigratedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Adr053TripWindowBackfillLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Adr053TripWindowBackfillLogs_TravelFiles_ReservaId",
                        column: x => x.ReservaId,
                        principalTable: "TravelFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Adr053TripWindowBackfillLogs_ReservaId",
                table: "Adr053TripWindowBackfillLogs",
                column: "ReservaId");

            // ────────────────────────────────────────────────────────────────────────────────────────
            // BACKFILL, 2 sentencias (log durable + UPDATE de la ventana). El TEXTO de las 2 vive en
            // Adr053BackfillSql (Reservations), NO inline acá — así el test de integración
            // Adr053BackfillSqlIntegrationTests corre el MISMO SQL que esta migración en vez de una copia
            // que se puede desincronizar (mismo patrón de Adr048_M2/Adr048T5BackfillSql). El INSERT del log
            // corre PRIMERO — necesita leer el StartDate/EndDate VIEJO antes de que el UPDATE lo pise.
            // ────────────────────────────────────────────────────────────────────────────────────────
            migrationBuilder.Sql(Adr053BackfillSql.InsertBackfillLog);
            migrationBuilder.Sql(Adr053BackfillSql.UpdateTravelFilesWindow);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Adr053TripWindowBackfillLogs");

            migrationBuilder.DropColumn(
                name: "NeedsDateRecalculation",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "PendingScheduleWarning",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "PendingScheduleWarningByUserId",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "PromisedEndDate",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "PromisedStartDate",
                table: "TravelFiles");
        }
    }
}
