using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// FC1.2.1 v3 §10.1 (BR-V2-01, 2026-05-17): trazabilidad del escape hatch
    /// manual cuando AFIP confirmo la NC pero el callback automatico fallo.
    ///
    /// Agrega 3 columnas nullable a <c>BookingCancellations</c>:
    ///   - <c>ArcaConfirmedManuallyAt</c>: timestamp UTC del Force.
    ///   - <c>ArcaConfirmedManuallyByUserId</c>: Admin que ejecuto el Force.
    ///   - <c>ArcaErrorMessage</c>: mensaje AFIP cuando el BC queda en
    ///     <c>ArcaRejected</c> (sirve al back-office para diagnosticar sin
    ///     bucear logs Hangfire).
    ///
    /// **Rollback**: aditiva — las 3 columnas son nullable, el <c>Down()</c> las
    /// dropea sin perder datos preexistentes. Las migraciones FC1.x posteriores
    /// no dependen de estos campos como NOT NULL, asi que el rollback parcial
    /// no rompe.
    /// </summary>
    public partial class FC1_2_1_AddBookingCancellationArcaFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ArcaConfirmedManuallyAt",
                table: "BookingCancellations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArcaConfirmedManuallyByUserId",
                table: "BookingCancellations",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArcaErrorMessage",
                table: "BookingCancellations",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArcaConfirmedManuallyAt",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "ArcaConfirmedManuallyByUserId",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "ArcaErrorMessage",
                table: "BookingCancellations");
        }
    }
}
