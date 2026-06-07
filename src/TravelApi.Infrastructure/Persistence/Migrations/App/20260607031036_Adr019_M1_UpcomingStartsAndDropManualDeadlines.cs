using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// ADR-019 D8 (avisos "Proximos inicios", 2026-06-06). Una sola migracion, dos cosas:
    ///
    /// <list type="number">
    ///   <item><b>CREATE TABLE UpcomingStartAlertDismissals</b>: el "Listo" global de la campanita.
    ///   UNIQUE(ReservaId) — a lo sumo una fila por reserva — + FK CASCADE a TravelFiles (Reservas):
    ///   borrar la reserva se lleva el descarte.</item>
    ///   <item><b>DROP de las 3 columnas de fecha limite MANUAL de ADR-017 F1.4</b>
    ///   (HotelBookings.OperatorPaymentDeadline, PackageBookings.OperatorPaymentDeadline,
    ///   FlightSegments.TicketingDeadline). El flag nunca estuvo prendido en prod; las columnas
    ///   existen en VPS (Adr017 M1-M5 aplicadas el 2026-06-06) y pueden tener DATOS DE PRUEBA —
    ///   el dueño confirmo explicitamente borrarlos ("Si, borralas").</item>
    /// </list>
    ///
    /// <para><b>Sobre el Down (forward-only)</b>: recrea las 3 columnas NULLABLE y VACIAS y dropea la
    /// tabla — NO restaura datos (perdida irreversible por diseño, aceptada por el dueño). En VPS la
    /// politica operativa es roll-forward (nunca se corre Down en prod); el Down existe solo para
    /// mantener la cadena de migraciones reversible en el loop de desarrollo local. El rollback REAL
    /// en prod es apagar el flag EnableServiceDeadlineAlerts, no esta migracion.</para>
    ///
    /// <para><b>Pre-check informativo opcional en VPS (no bloqueante)</b>: antes de aplicar,
    /// <c>SELECT COUNT(*)</c> de no-null en las 3 columnas, solo para dejar constancia de cuantas
    /// filas de prueba se pierden (ADR-019 D7).</para>
    /// </summary>
    public partial class Adr019_M1_UpcomingStartsAndDropManualDeadlines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OperatorPaymentDeadline",
                table: "PackageBookings");

            migrationBuilder.DropColumn(
                name: "OperatorPaymentDeadline",
                table: "HotelBookings");

            migrationBuilder.DropColumn(
                name: "TicketingDeadline",
                table: "FlightSegments");

            migrationBuilder.CreateTable(
                name: "UpcomingStartAlertDismissals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReservaId = table.Column<int>(type: "integer", nullable: false),
                    DismissedFirstStartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DismissedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    DismissedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpcomingStartAlertDismissals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UpcomingStartAlertDismissals_TravelFiles_ReservaId",
                        column: x => x.ReservaId,
                        principalTable: "TravelFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UpcomingStartAlertDismissals_ReservaId",
                table: "UpcomingStartAlertDismissals",
                column: "ReservaId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UpcomingStartAlertDismissals");

            migrationBuilder.AddColumn<DateTime>(
                name: "OperatorPaymentDeadline",
                table: "PackageBookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OperatorPaymentDeadline",
                table: "HotelBookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TicketingDeadline",
                table: "FlightSegments",
                type: "timestamp with time zone",
                nullable: true);
        }
    }
}
