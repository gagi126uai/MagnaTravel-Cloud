using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class Adr050_UndoAnnulment_AddAnnulledAtAndStatusBeforeCancellation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AnnulledAt",
                table: "TravelFiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AnnulledByUserId",
                table: "TravelFiles",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatusBeforeCancellation",
                table: "TransferBookings",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatusBeforeCancellation",
                table: "Reservations",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatusBeforeCancellation",
                table: "PackageBookings",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatusBeforeCancellation",
                table: "HotelBookings",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatusBeforeCancellation",
                table: "FlightSegments",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceBridgePaymentId",
                table: "ClientCreditEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatusBeforeCancellation",
                table: "AssistanceBookings",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            // FIX B3 (review de seguridad, 2026-07-24): indice UNICO parcial en vez de un indice simple — a lo
            // sumo UN credito por puente (ver el comentario gemelo en AppDbContext sobre
            // IX_ClientCreditEntries_SourceDebitNoteAnnulment_OnePerEvent). Esta migracion es la que introduce la
            // columna, asi que el indice nace unico desde el dia uno; no hace falta backfill previo (todavia no
            // hay filas con esta columna poblada al momento de crear el indice).
            migrationBuilder.CreateIndex(
                name: "IX_ClientCreditEntries_SourceBridgePayment_OnePerBridge",
                table: "ClientCreditEntries",
                column: "SourceBridgePaymentId",
                unique: true,
                filter: "\"SourceBridgePaymentId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_ClientCreditEntries_Payments_SourceBridgePaymentId",
                table: "ClientCreditEntries",
                column: "SourceBridgePaymentId",
                principalTable: "Payments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // ADR-050 (2026-07-24): backfill de SourceBridgePaymentId para los creditos "anular sin factura" ya
            // existentes en PROD (creados por CancellationToClientCreditConverter ANTES de que esta columna
            // existiera). Sin este backfill, el gate D2/undo de RevertStatusAsync no podria encontrar el puente
            // de esas anulaciones historicas (la correlacion es EXCLUSIVAMENTE por esta FK).
            //
            // Discriminador de origen "anular sin factura" (ver el XML-doc de ClientCreditEntry):
            // BookingCancellationId IS NULL (no es de una cancelacion formal con NC) Y SourcePaymentId IS NULL
            // (no es de un sobrepago) Y SourceReservaId IS NOT NULL (viene de una reserva anulada) Y
            // SourceDebitNoteAnnulmentId IS NULL (no es de deshacer una multa). Antes de esta obra, a lo sumo
            // UN puente vivo por (reserva, moneda) podia existir (el undo todavia no existia), asi que el match
            // por reserva+Method+Currency es inambiguo. OJO nombres fisicos: la FK de Payments a la reserva se
            // llama ""TravelFileId"" en la DB (HasColumnName en AppDbContext), NO ""ReservaId"";
            // ClientCreditEntries.SourceReservaId en cambio NO esta renombrada. Verificado contra
            // AppDbContextModelSnapshot. T-8.
            migrationBuilder.Sql(@"
                UPDATE ""ClientCreditEntries"" cce
                SET ""SourceBridgePaymentId"" = p.""Id""
                FROM ""Payments"" p
                WHERE cce.""BookingCancellationId"" IS NULL
                  AND cce.""SourcePaymentId"" IS NULL
                  AND cce.""SourceReservaId"" IS NOT NULL
                  AND cce.""SourceDebitNoteAnnulmentId"" IS NULL
                  AND cce.""SourceBridgePaymentId"" IS NULL
                  AND p.""TravelFileId"" = cce.""SourceReservaId""
                  AND p.""Method"" = 'SaldoAFavorPorAnulacion'
                  AND p.""IsDeleted"" = FALSE
                  AND p.""Currency"" = cce.""Currency"";
            ");

            // FIX B1 (review de backend, 2026-07-24): backfill de AnnulledAt para las reservas que HOY (antes de
            // esta obra) ya quedaron Canceladas por un acto de anular — la obra "anular sin factura" esta viva en
            // PROD desde 2026-07-23, asi que ya existen filas asi. Sin este backfill, UndoAnnulmentAsync (gate
            // "reserva.AnnulledAt.HasValue") las saltea y "Volver atras" cae al flip generico de estado, que NO
            // revive servicios ni destacha el saldo a favor — EXACTAMENTE el bug "Volver atras deja huerfanos"
            // que esta obra promete cerrar.
            //
            // El momento EXACTO del acto de anular no se guardo para estas filas legacy (la columna no existia).
            // Lo aproximamos con el MAX(CancelledAt) de los servicios cancelados de esa reserva, en las 6 tablas
            // de servicio (FlightSegments, HotelBookings, TransferBookings, PackageBookings, AssistanceBookings,
            // Reservations — "Reservations" es la tabla fisica de ServicioReserva, NO confundir con
            // TravelFiles/Reserva; en TODAS la FK fisica a la reserva se llama ""TravelFileId"", no
            // ""ReservaId"" — verificado contra AppDbContextModelSnapshot). El
            // anular-total estampa CancelledAt a TODOS los servicios en el MISMO acto, asi que su MAX coincide
            // con el instante de la anulacion. Un servicio cancelado ANTES, uno por uno (fuera del acto de
            // anular), queda con un CancelledAt MENOR al de los servicios anulados en el acto -> el filtro
            // "CancelledAt >= AnnulledAt" de ReviveServicesCancelledDuringAnnulment lo EXCLUYE del revive, que es
            // EXACTAMENTE la decision firmada del dueño ("una cancelacion uno-por-uno no revive"). Si una reserva
            // Cancelada no tiene NINGUN servicio cancelado (caso raro, p.ej. reserva vacia que se anulo sin
            // servicios), no hay fila que matchee -> AnnulledAt queda en NULL y el revert sigue cayendo al flip
            // generico, que es lo correcto ahi (no hay nada que revivir).
            //
            // Solo toca reservas Cancelled o PendingOperatorRefund (los 2 estados terminales de "anulada", ver
            // ReservaTerminalDerivation) con AnnulledAt todavia NULL; jamas pisa una fila que esta obra misma ya
            // dejo poblada (no deberia pasar en un Up() que corre una sola vez, pero el guard es gratis).
            migrationBuilder.Sql(@"
                WITH cancelled_service_events AS (
                    SELECT ""TravelFileId"" AS reserva_id, ""CancelledAt"" AS cancelled_at
                    FROM ""FlightSegments"" WHERE ""CancelledAt"" IS NOT NULL
                    UNION ALL
                    SELECT ""TravelFileId"", ""CancelledAt""
                    FROM ""HotelBookings"" WHERE ""CancelledAt"" IS NOT NULL
                    UNION ALL
                    SELECT ""TravelFileId"", ""CancelledAt""
                    FROM ""TransferBookings"" WHERE ""CancelledAt"" IS NOT NULL
                    UNION ALL
                    SELECT ""TravelFileId"", ""CancelledAt""
                    FROM ""PackageBookings"" WHERE ""CancelledAt"" IS NOT NULL
                    UNION ALL
                    SELECT ""TravelFileId"", ""CancelledAt""
                    FROM ""AssistanceBookings"" WHERE ""CancelledAt"" IS NOT NULL
                    UNION ALL
                    SELECT ""TravelFileId"", ""CancelledAt""
                    FROM ""Reservations"" WHERE ""CancelledAt"" IS NOT NULL
                ),
                last_cancellation_per_reserva AS (
                    SELECT reserva_id, MAX(cancelled_at) AS max_cancelled_at
                    FROM cancelled_service_events
                    GROUP BY reserva_id
                )
                UPDATE ""TravelFiles"" tf
                SET ""AnnulledAt"" = lc.max_cancelled_at
                FROM last_cancellation_per_reserva lc
                WHERE tf.""Id"" = lc.reserva_id
                  AND tf.""Status"" IN ('Cancelled', 'PendingOperatorRefund')
                  AND tf.""AnnulledAt"" IS NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ClientCreditEntries_Payments_SourceBridgePaymentId",
                table: "ClientCreditEntries");

            migrationBuilder.DropIndex(
                name: "IX_ClientCreditEntries_SourceBridgePayment_OnePerBridge",
                table: "ClientCreditEntries");

            migrationBuilder.DropColumn(
                name: "AnnulledAt",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "AnnulledByUserId",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "StatusBeforeCancellation",
                table: "TransferBookings");

            migrationBuilder.DropColumn(
                name: "StatusBeforeCancellation",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "StatusBeforeCancellation",
                table: "PackageBookings");

            migrationBuilder.DropColumn(
                name: "StatusBeforeCancellation",
                table: "HotelBookings");

            migrationBuilder.DropColumn(
                name: "StatusBeforeCancellation",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "SourceBridgePaymentId",
                table: "ClientCreditEntries");

            migrationBuilder.DropColumn(
                name: "StatusBeforeCancellation",
                table: "AssistanceBookings");
        }
    }
}
