using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// (2026-07-04, hallazgo A1 de la auditoría) REPARACIÓN DE DATOS LEGACY — cancela los servicios que
    /// quedaron VIVOS en reservas ya ANULADAS antes del 2026-06-24.
    ///
    /// <para><b>Qué problema arregla</b>: hasta el 2026-06-24, anular una reserva (<c>BookingCancellationService</c>)
    /// cambiaba el ESTADO de la reserva a <c>Cancelled</c>/<c>PendingOperatorRefund</c> pero NO cancelaba sus
    /// servicios (hotel, aéreo, traslado, etc.). Ese barrido de servicios recién entró el 2026-06-24 con
    /// <c>CancelAllReservaServicesAsync</c>. Consecuencia: las reservas anuladas viejas siguen con servicios en
    /// Confirmado/Solicitado → su venta confirmada (<c>ConfirmedSale</c>) queda &gt; 0 → su saldo (<c>Balance</c>)
    /// queda &gt; 0 para siempre → aparecen falsamente "con deuda". Nunca hubo un backfill que las limpiara.</para>
    ///
    /// <para><b>Qué hace esta migración (paso 1 de 2)</b>: cancela EN SQL los servicios vivos de esas reservas,
    /// espejo EXACTO de lo que hace <c>CancelAllReservaServicesAsync</c> en C# (mismos literales de estado por tipo:
    /// aéreo = <c>'UN'</c>; el resto = <c>'Cancelado'</c>). NO recalcula la plata: eso lo hace el PASO 2, el endpoint
    /// admin <c>POST /api/admin/maintenance/coherence/recalculate-money</c> (servicio <c>CoherenceMoneyRecalculator</c>),
    /// que corre los persisters canónicos y deja el saldo como lo dejaría una anulación moderna. Se separan a
    /// propósito: cancelar servicios es un <c>UPDATE</c> puro e idempotente; recalcular la plata necesita la lógica
    /// de dominio (calculadores) que no se puede ni se debe reescribir en SQL crudo.</para>
    ///
    /// <para><b>Por qué es SEGURA</b>:
    /// <list type="bullet">
    /// <item>NO cambia el esquema (0 columnas/índices; por eso el <c>ModelSnapshot</c> no cambia).</item>
    /// <item>Antes de tocar nada, hace BACKUP (paso 0) en tablas <c>_repair_20260705_*</c> del estado previo de los
    ///   servicios vivos y de la plata (proyección por moneda + escalares), para poder auditar/reconstruir si hiciera
    ///   falta. El <c>CREATE TABLE IF NOT EXISTS ... AS</c> hace el backup idempotente: en una segunda corrida la
    ///   tabla ya existe y no se re-crea (conserva la foto original pre-reparación).</item>
    /// <item>Los <c>UPDATE</c> son idempotentes: el <c>WHERE</c> excluye los servicios YA cancelados (mismo criterio
    ///   que <c>ServiceResolutionRules.IsCancelled</c>: aéreo con código IATA de cancelación; el resto con estado que
    ///   empieza con "cancel"). Correrla dos veces no vuelve a tocar filas ya canceladas.</item>
    /// <item>Solo toca reservas <c>Cancelled</c>/<c>PendingOperatorRefund</c>. Una anulación MODERNA ya dejó sus
    ///   servicios cancelados → el <c>WHERE</c> idempotente los saltea; solo se reparan las legacy.</item>
    /// </list></para>
    /// </summary>
    public partial class RepairLegacyAnnulledReservaServices : Migration
    {
        // Rastro de auditoría del servicio (CancelledByUserName). Este valor SÍ es visible para el usuario:
        // se proyecta en los 6 DTOs de servicio y ServiceList.jsx lo muestra como "Cancelado por {nombre}".
        // Por eso NO puede llevar jerga técnica (nada de "legacy", "A1", "migración"): debe leerse como negocio.
        // Deja claro en la ficha que lo canceló un ajuste del sistema, no una persona.
        private const string RepairActorName = "Ajuste del sistema";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ─────────────────────────────────────────────────────────────────────────────────────────
            // PASO 0 — BACKUP del estado previo (idempotente vía CREATE TABLE IF NOT EXISTS ... AS).
            // OJO nombres REALES de tablas/columnas en Postgres:
            //   - la reserva/booking es la tabla "TravelFiles" (NO "Reservas"); su PK es "Id".
            //   - los 6 tipos de servicio referencian la reserva por la columna "TravelFileId" (NO "ReservaId").
            //   - el servicio GENÉRICo (entidad ServicioReserva) vive en la tabla llamada "Reservations"
            //     (nombre legacy confuso), con FK "TravelFileId".
            //   - la proyección de plata por moneda es "ReservaMoneyByCurrency" y ahí SÍ la FK se llama "ReservaId".
            // ─────────────────────────────────────────────────────────────────────────────────────────

            // Backup 0.a — una fila por cada servicio VIVO (aún no cancelado) de una reserva anulada. La columna
            // "src" guarda de qué tabla salió. Guarda las columnas que el UPDATE va a pisar (Status/CancelledAt/
            // CancelledByUserName) para poder reconstruir el estado exacto previo.
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""_repair_20260705_services_backup"" AS
                SELECT 'FlightSegments'::text AS src, s.""Id"", s.""TravelFileId"", s.""Status"",
                       s.""CancelledAt"", s.""CancelledByUserName""
                FROM ""FlightSegments"" s
                JOIN ""TravelFiles"" tf ON tf.""Id"" = s.""TravelFileId""
                WHERE tf.""Status"" IN ('Cancelled', 'PendingOperatorRefund')
                  AND UPPER(s.""Status"") NOT IN ('UN', 'UC', 'HX', 'NO')
                UNION ALL
                SELECT 'HotelBookings'::text, s.""Id"", s.""TravelFileId"", s.""Status"",
                       s.""CancelledAt"", s.""CancelledByUserName""
                FROM ""HotelBookings"" s
                JOIN ""TravelFiles"" tf ON tf.""Id"" = s.""TravelFileId""
                WHERE tf.""Status"" IN ('Cancelled', 'PendingOperatorRefund')
                  AND LOWER(s.""Status"") NOT LIKE 'cancel%'
                UNION ALL
                SELECT 'TransferBookings'::text, s.""Id"", s.""TravelFileId"", s.""Status"",
                       s.""CancelledAt"", s.""CancelledByUserName""
                FROM ""TransferBookings"" s
                JOIN ""TravelFiles"" tf ON tf.""Id"" = s.""TravelFileId""
                WHERE tf.""Status"" IN ('Cancelled', 'PendingOperatorRefund')
                  AND LOWER(s.""Status"") NOT LIKE 'cancel%'
                UNION ALL
                SELECT 'PackageBookings'::text, s.""Id"", s.""TravelFileId"", s.""Status"",
                       s.""CancelledAt"", s.""CancelledByUserName""
                FROM ""PackageBookings"" s
                JOIN ""TravelFiles"" tf ON tf.""Id"" = s.""TravelFileId""
                WHERE tf.""Status"" IN ('Cancelled', 'PendingOperatorRefund')
                  AND LOWER(s.""Status"") NOT LIKE 'cancel%'
                UNION ALL
                SELECT 'AssistanceBookings'::text, s.""Id"", s.""TravelFileId"", s.""Status"",
                       s.""CancelledAt"", s.""CancelledByUserName""
                FROM ""AssistanceBookings"" s
                JOIN ""TravelFiles"" tf ON tf.""Id"" = s.""TravelFileId""
                WHERE tf.""Status"" IN ('Cancelled', 'PendingOperatorRefund')
                  AND LOWER(s.""Status"") NOT LIKE 'cancel%'
                UNION ALL
                SELECT 'Reservations'::text, s.""Id"", s.""TravelFileId"", s.""Status"",
                       s.""CancelledAt"", s.""CancelledByUserName""
                FROM ""Reservations"" s
                JOIN ""TravelFiles"" tf ON tf.""Id"" = s.""TravelFileId""
                WHERE tf.""Status"" IN ('Cancelled', 'PendingOperatorRefund')
                  AND LOWER(s.""Status"") NOT LIKE 'cancel%';
            ");

            // Backup 0.b — proyección de plata por moneda (tabla hija) de las reservas anuladas.
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""_repair_20260705_money_backup"" AS
                SELECT m.""Id"", m.""ReservaId"", m.""Currency"",
                       m.""TotalSale"", m.""ConfirmedSale"", m.""TotalCost"", m.""TotalPaid"", m.""Balance""
                FROM ""ReservaMoneyByCurrency"" m
                JOIN ""TravelFiles"" tf ON tf.""Id"" = m.""ReservaId""
                WHERE tf.""Status"" IN ('Cancelled', 'PendingOperatorRefund');
            ");

            // Backup 0.c — escalares de plata de las reservas anuladas (la tabla hija y el escalar tienen GRANOS
            // distintos: por eso van en tablas de backup separadas en vez de forzarlos en una sola).
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""_repair_20260705_travelfile_backup"" AS
                SELECT tf.""Id"", tf.""Status"",
                       tf.""TotalSale"", tf.""ConfirmedSale"", tf.""TotalCost"", tf.""TotalPaid"", tf.""Balance""
                FROM ""TravelFiles"" tf
                WHERE tf.""Status"" IN ('Cancelled', 'PendingOperatorRefund');
            ");

            // ─────────────────────────────────────────────────────────────────────────────────────────
            // PASO 1 — cancelar los servicios vivos de las reservas anuladas. Un UPDATE por tabla, espejo EXACTO
            // de CancelAllReservaServicesAsync (mismos campos, mismos literales). CancelledByUserId se deja como
            // está (NULL en un servicio no cancelado): el rastro de quién lo canceló va en CancelledByUserName.
            // ─────────────────────────────────────────────────────────────────────────────────────────

            // Aéreo: el estado cancelado es el código IATA 'UN' (igual que el path C#). El WHERE excluye los ya
            // cancelados con el MISMO set que ServiceResolutionRules.IsCancelled(flight) (MapFlightStatus).
            migrationBuilder.Sql($@"
                UPDATE ""FlightSegments"" s
                SET ""Status"" = 'UN',
                    ""CancelledAt"" = now(),
                    ""CancelledByUserName"" = '{RepairActorName}'
                FROM ""TravelFiles"" tf
                WHERE tf.""Id"" = s.""TravelFileId""
                  AND tf.""Status"" IN ('Cancelled', 'PendingOperatorRefund')
                  AND UPPER(s.""Status"") NOT IN ('UN', 'UC', 'HX', 'NO');
            ");

            // Tipos con estado de texto: 'Cancelado' literal. El WHERE excluye los ya cancelados con el MISMO
            // criterio que ServiceResolutionRules.IsCancelled genérico (MapGenericStatus: empieza con 'cancel').
            foreach (var tableName in new[]
            {
                "HotelBookings", "TransferBookings", "PackageBookings", "AssistanceBookings", "Reservations"
            })
            {
                migrationBuilder.Sql($@"
                    UPDATE ""{tableName}"" s
                    SET ""Status"" = 'Cancelado',
                        ""CancelledAt"" = now(),
                        ""CancelledByUserName"" = '{RepairActorName}'
                    FROM ""TravelFiles"" tf
                    WHERE tf.""Id"" = s.""TravelFileId""
                      AND tf.""Status"" IN ('Cancelled', 'PendingOperatorRefund')
                      AND LOWER(s.""Status"") NOT LIKE 'cancel%';
                ");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op deliberado. Esta migración NO toca el esquema (no hay nada de estructura que revertir) y NO se
            // puede "des-cancelar" un servicio: no guardamos el estado destino previo fila-por-fila en la propia
            // reserva. La reversión, de necesitarse, se hace a mano desde las tablas de backup "_repair_20260705_*",
            // que se dejan A PROPÓSITO en la base (no se dropean acá) como red de seguridad forense.
        }
    }
}
