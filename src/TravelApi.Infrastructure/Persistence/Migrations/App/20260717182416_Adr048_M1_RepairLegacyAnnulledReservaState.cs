using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// ADR-048 (modelo de estados derivados, 2026-07-17) — REPARACIÓN DE DATOS LEGACY, corre UNA SOLA VEZ.
    ///
    /// <para><b>Qué problema arregla</b>: hasta este deploy, cancelar servicios uno por uno (partial
    /// cancellation, <c>BookingCancellationService.CancelServiceAsync</c>) nunca movía el <c>Status</c> de
    /// la RESERVA — solo el flujo de anulación TOTAL lo hacía. Consecuencia real y verificada
    /// (F-2026-1046): una reserva con el 100% de sus servicios anulados podía seguir mostrando el cartel
    /// "Confirmada" para siempre, con "Pago: sin movimientos" y "Sin facturar" aunque hubiera una Nota de
    /// Crédito emitida. Este archivo repara esas reservas ya existentes; el motor en vivo (que a partir de
    /// este mismo deploy corre en <c>ReservaMoneyPersister</c>) evita que la mentira vuelva a aparecer hacia
    /// adelante.</para>
    ///
    /// <para><b>Qué hace (mismo criterio que el código en vivo, sin duplicar la regla en dos lugares
    /// distintos salvo por necesidad de SQL)</b>: para cada reserva en <c>{InManagement, Confirmed,
    /// Traveling}</c> que "tuvo servicios y los tiene TODOS anulados" (mismo detector de cancelado que
    /// <c>ServiceResolutionRules.IsCancelled</c>: aéreo por código IATA, el resto por texto que empieza con
    /// "cancel"), calcula el terminal del PAR igual que <c>ReservaTerminalDerivation.DetermineTerminalStatus</c>
    /// en C# (si el operador todavía debe algún reembolso en CUALQUIERA de las cancelaciones de la reserva
    /// → "Esperando reembolso del operador"; si no → "Anulada") y lo aplica.</para>
    ///
    /// <para><b>Por qué SQL crudo y no una migración que invoca C#</b>: el patrón establecido en este
    /// repositorio para reparaciones de datos es SQL puro con backup previo (ver el precedente
    /// <c>RepairLegacyAnnulledReservaServices</c>, 2026-07-05) — no hay wiring de DI dentro de migraciones EF
    /// en este proyecto. El criterio de negocio se REPLICA acá con las mismas reglas ya verificadas en el
    /// precedente (mismos literales de estado cancelado por tabla).</para>
    ///
    /// <para><b>Por qué es SEGURA</b>:
    /// <list type="bullet">
    /// <item>NO cambia el esquema (0 columnas/índices nuevos; el <c>ModelSnapshot</c> no cambia).</item>
    /// <item>PASO 0 calcula los candidatos UNA SOLA VEZ en una tabla de respaldo/foto
    ///   (<c>CREATE TABLE IF NOT EXISTS ... AS</c>, idempotente): si esta migración se corriera dos veces
    ///   (no debería — EF la marca en <c>__EFMigrationsHistory</c>), la foto de candidatos NO se recalcula,
    ///   y los pasos siguientes verifican el <c>Status</c> VIVO antes de tocar nada, así una segunda corrida
    ///   no vuelve a auditar/reparar lo que ya se reparó.</item>
    /// <item>Deja rastro auditable real en <c>ReservaStatusChangeLogs</c> (regla 10, actor "sistema"), igual
    ///   que la transición automática en vivo — no un backup aparte que nadie lee.</item>
    /// <item>Limpia las marcas de revisión ("confirmada con cambios") con el MISMO criterio que
    ///   <c>ReservaStateCleanupRules</c> aplica al entrar a un terminal de anulación.</item>
    /// </list></para>
    ///
    /// <para><b>M5 (ADR-036 "en viaje inmutable")</b>: el motor EN VIVO nunca toca <c>Traveling</c> — pero
    /// esta reparación ÚNICA sí lo barre, porque la mentira puede existir en datos legacy de antes de que
    /// existiera el candado de edición en viaje. Corregir una etiqueta mentirosa una vez no es "editar un
    /// viaje": es sanear un dato, y queda auditado.</para>
    /// </summary>
    public partial class Adr048_M1_RepairLegacyAnnulledReservaState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ─────────────────────────────────────────────────────────────────────────────────────────
            // PASO 0 — candidatos (backup + selección, idempotente vía CREATE TABLE IF NOT EXISTS ... AS).
            //
            // OJO nombres REALES en Postgres (ver lección "db naming travelfiles"):
            //   - la reserva es la tabla "TravelFiles" (NO "Reservas"); su PK es "Id".
            //   - los 6 tipos de servicio (incluido el genérico, tabla "Reservations") referencian la
            //     reserva por la columna "TravelFileId".
            //   - las cancelaciones ("BookingCancellations") SÍ usan la columna "ReservaId" (distinto de
            //     "TravelFileId" — asimetría real del modelo, no un error de tipeo).
            //
            // "tuvo servicios y todos anulados" = total_count >= 1 AND live_count = 0, mismo criterio que
            // ReservaDerivedState.HadServicesAndAllCancelled en C#. El detector de "está cancelado" por
            // tabla es EXACTAMENTE el mismo que usa el precedente RepairLegacyAnnulledReservaServices
            // (2026-07-05), que a su vez espeja ServiceResolutionRules.IsCancelled.
            // ─────────────────────────────────────────────────────────────────────────────────────────
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""_repair_20260717_reserva_terminal_candidates"" AS
                WITH service_flags AS (
                    SELECT s.""TravelFileId"" AS reserva_id, (UPPER(s.""Status"") IN ('UN','UC','HX','NO')) AS is_cancelled
                    FROM ""FlightSegments"" s
                    UNION ALL
                    SELECT s.""TravelFileId"", (LOWER(s.""Status"") LIKE 'cancel%')
                    FROM ""HotelBookings"" s
                    UNION ALL
                    SELECT s.""TravelFileId"", (LOWER(s.""Status"") LIKE 'cancel%')
                    FROM ""TransferBookings"" s
                    UNION ALL
                    SELECT s.""TravelFileId"", (LOWER(s.""Status"") LIKE 'cancel%')
                    FROM ""PackageBookings"" s
                    UNION ALL
                    SELECT s.""TravelFileId"", (LOWER(s.""Status"") LIKE 'cancel%')
                    FROM ""AssistanceBookings"" s
                    UNION ALL
                    SELECT s.""TravelFileId"", (LOWER(s.""Status"") LIKE 'cancel%')
                    FROM ""Reservations"" s
                ),
                reserva_service_summary AS (
                    SELECT reserva_id,
                           COUNT(*) AS total_count,
                           COUNT(*) FILTER (WHERE NOT is_cancelled) AS live_count
                    FROM service_flags
                    GROUP BY reserva_id
                ),
                -- B1 (nivel RESERVA, N cancelaciones): el operador todavia debe algun reembolso si CUALQUIER
                -- linea de CUALQUIER cancelacion de la reserva tiene RefundCap>0 y no llego a Settled (2).
                reserva_operator_refund_pending AS (
                    SELECT DISTINCT bc.""ReservaId"" AS reserva_id
                    FROM ""BookingCancellationLines"" l
                    JOIN ""BookingCancellations"" bc ON bc.""Id"" = l.""BookingCancellationId""
                    WHERE l.""RefundCap"" > 0 AND l.""RefundStatus"" <> 2
                )
                SELECT tf.""Id"" AS reserva_id,
                       tf.""Status"" AS from_status,
                       CASE WHEN rp.reserva_id IS NOT NULL THEN 'PendingOperatorRefund' ELSE 'Cancelled' END AS to_status
                FROM ""TravelFiles"" tf
                JOIN reserva_service_summary rss ON rss.reserva_id = tf.""Id""
                LEFT JOIN reserva_operator_refund_pending rp ON rp.reserva_id = tf.""Id""
                WHERE tf.""Status"" IN ('InManagement', 'Confirmed', 'Traveling')
                  AND rss.total_count >= 1
                  AND rss.live_count = 0;
            ");

            // ─────────────────────────────────────────────────────────────────────────────────────────
            // PASO 1 — rastro auditable (regla 10), ANTES de tocar el Status: guarda el Status previo REAL
            // (leído en vivo, no el congelado en el paso 0) para que una corrida accidental doble no vuelva
            // a auditar una reserva que ya se reparó (el WHERE exige que siga en su estado de origen).
            // ─────────────────────────────────────────────────────────────────────────────────────────
            migrationBuilder.Sql(@"
                INSERT INTO ""ReservaStatusChangeLogs""
                    (""PublicId"", ""ReservaId"", ""FromStatus"", ""ToStatus"", ""Direction"", ""ByUserId"", ""ByUserName"", ""Reason"", ""OccurredAt"")
                SELECT
                    gen_random_uuid(),
                    c.reserva_id,
                    tf.""Status"",
                    c.to_status,
                    'Forward',
                    'system:auto-state',
                    'Sistema (motor de estados)',
                    CASE WHEN c.to_status = 'PendingOperatorRefund'
                         THEN 'Reparacion de datos historicos: todos los servicios de la reserva ya estaban anulados y el operador todavia debe algun reembolso. La reserva queda esperando ese reembolso (sistema).'
                         ELSE 'Reparacion de datos historicos: todos los servicios de la reserva ya estaban anulados y no hay reembolso del operador pendiente. La reserva queda anulada (sistema).'
                    END,
                    now()
                FROM ""_repair_20260717_reserva_terminal_candidates"" c
                JOIN ""TravelFiles"" tf ON tf.""Id"" = c.reserva_id
                WHERE tf.""Status"" = c.from_status;
            ");

            // ─────────────────────────────────────────────────────────────────────────────────────────
            // PASO 2 — aplicar el terminal + limpiar la marca "confirmada con cambios" (mismo criterio que
            // ReservaStateCleanupRules.For(Cancelled/PendingOperatorRefund): apaga HasUnacknowledgedChanges,
            // NO toca LastRegression* — queda como historial informativo, no molesta).
            // ─────────────────────────────────────────────────────────────────────────────────────────
            migrationBuilder.Sql(@"
                UPDATE ""TravelFiles"" tf
                SET ""Status"" = c.to_status,
                    ""HasUnacknowledgedChanges"" = false,
                    ""ChangesPendingSince"" = NULL
                FROM ""_repair_20260717_reserva_terminal_candidates"" c
                WHERE tf.""Id"" = c.reserva_id
                  AND tf.""Status"" = c.from_status;
            ");

            // ─────────────────────────────────────────────────────────────────────────────────────────
            // PASO 3 — borra el detalle de "que precio cambio" de las reservas recien reparadas: ya no
            // tiene sentido revisar cambios de una reserva anulada (mismo criterio, ClearPendingChangeRows).
            // ─────────────────────────────────────────────────────────────────────────────────────────
            migrationBuilder.Sql(@"
                DELETE FROM ""ReservaPendingChanges""
                WHERE ""ReservaId"" IN (
                    SELECT c.reserva_id
                    FROM ""_repair_20260717_reserva_terminal_candidates"" c
                    JOIN ""TravelFiles"" tf ON tf.""Id"" = c.reserva_id
                    WHERE tf.""Status"" IN ('Cancelled', 'PendingOperatorRefund')
                );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op deliberado (mismo criterio que el precedente RepairLegacyAnnulledReservaServices): esta
            // migracion NO cambia el esquema (nada de columnas/indices que revertir) y "des-anular" una
            // reserva no es un rollback seguro (puede haber comprobantes u operaciones nuevas encima). Si
            // hiciera falta reconstruir el Status previo de una reserva puntual, esta en
            // "_repair_20260717_reserva_terminal_candidates" (columna from_status) y en
            // "ReservaStatusChangeLogs" — se dejan a proposito sin dropear, como red de seguridad forense.
        }
    }
}
