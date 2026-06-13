using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// Auditoria ERP 2026-06-12 (items 5 y 8, decision del dueño). Tanda "VENCIMIENTOS / ALARMAS".
    /// Migracion 100% ADITIVA: agrega columnas nullable, no toca ni borra datos existentes.
    ///
    /// <list type="bullet">
    ///   <item><b>OperatorPaymentDeadline</b> (fecha limite de pago al operador) en los 6 tipos de
    ///   servicio con costo/proveedor: Hotel, Aereo, Traslado, Paquete, Asistencia y el generico
    ///   (tabla "Reservations" por HasColumnName historico de ServicioReserva). Antes de ADR-019 solo
    ///   Hotel y Paquete lo tenian; la auditoria pide cobertura en todo servicio con proveedor.</item>
    ///   <item><b>TicketingDeadline</b> (time-limit de emision) en FlightSegments — especifico del aereo.</item>
    ///   <item><b>PassportExpiry</b> (vencimiento de pasaporte) en Passengers.</item>
    /// </list>
    ///
    /// <para><b>Por que vuelven OperatorPaymentDeadline/TicketingDeadline tras ADR-019</b>: ADR-019 D7 los
    /// dropeo porque la VIEJA campanita de fechas limite manuales (ADR-017 F1.4) fue reemplazada por el
    /// aviso automatico "Proximos inicios" (que mira el INICIO del viaje). El concepto "pago al operador"
    /// y "time-limit de emision" NUNCA fueron cubiertos por ese aviso — son fechas distintas del inicio
    /// del viaje. El dueño los reintrodujo para alimentar 3 alarmas nuevas. Las filas existentes quedan
    /// en NULL (sin fecha informada), igual que cuando se crearon originalmente.</para>
    ///
    /// <para><b>Down (forward-only, igual que el resto del repo)</b>: dropea las columnas. No restaura
    /// datos (no habia que restaurar: nacen vacias). En VPS la politica es roll-forward; el Down existe
    /// para el loop de desarrollo local.</para>
    /// </summary>
    public partial class Adr025_M1_ReintroduceOperationalDeadlinesAndPassportExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "OperatorPaymentDeadline",
                table: "TransferBookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OperatorPaymentDeadline",
                table: "Reservations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PassportExpiry",
                table: "Passengers",
                type: "timestamp with time zone",
                nullable: true);

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
                name: "OperatorPaymentDeadline",
                table: "FlightSegments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TicketingDeadline",
                table: "FlightSegments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OperatorPaymentDeadline",
                table: "AssistanceBookings",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OperatorPaymentDeadline",
                table: "TransferBookings");

            migrationBuilder.DropColumn(
                name: "OperatorPaymentDeadline",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "PassportExpiry",
                table: "Passengers");

            migrationBuilder.DropColumn(
                name: "OperatorPaymentDeadline",
                table: "PackageBookings");

            migrationBuilder.DropColumn(
                name: "OperatorPaymentDeadline",
                table: "HotelBookings");

            migrationBuilder.DropColumn(
                name: "OperatorPaymentDeadline",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "TicketingDeadline",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "OperatorPaymentDeadline",
                table: "AssistanceBookings");
        }
    }
}
