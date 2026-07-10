using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// (2026-07-10, bug de plata confirmado en prod, reserva F-2026-1038) REPARACIÓN DE DATOS —
    /// da de baja (soft-delete) los asientos de "reversión económica" de una Nota de Crédito que
    /// quedaron creados SIN que la reserva tuviera ningún cobro real que revertir.
    ///
    /// <para><b>Qué problema arregla</b>: <c>AfipService.ApplyPartialCreditNoteReversalAsync</c> y
    /// <c>ApplyTotalCreditNoteReversalAsync</c> creaban SIEMPRE un <c>Payment</c> de reversión
    /// (<c>EntryType = "CreditNoteReversal"</c>) por el <c>ImporteTotal</c> completo de la NC, sin
    /// mirar si la reserva habia cobrado algo alguna vez. Facturar sin cobrar es legitimo (ADR-037,
    /// facturación y cobranza son carriles separados): una factura puede tener CAE y CERO Payments
    /// de cobro. Caso real que confirmó el bug: reserva F-2026-1038, Invoice 61 / NC 62, $726.000,
    /// CERO Payments de cobro en TODA la reserva — la reversión bajaba el saldo por plata que el
    /// cliente nunca puso, dejando una deuda 100% fantasma y permanente. El código ya se corrigió
    /// (<c>AfipService.CalculateCreditNoteReversalCapAsync</c> topea la reversión contra lo
    /// REALMENTE cobrado por la reserva en la moneda del comprobante, y si el tope da 0 no crea el
    /// Payment); esta migración es la reparación de los datos que ya quedaron mal escritos en
    /// producción ANTES del fix.</para>
    ///
    /// <para><b>Alcance CONSERVADOR (a propósito, ENDURECIDO tras validar contra prod el 2026-07-10)</b>:
    /// esta migración SOLO toca el caso más claro — un <c>CreditNoteReversal</c> vivo cuya reserva NO
    /// tiene NINGÚN <c>Payment</c> vivo de cobro en NINGUNA moneda (cero plata del cliente en toda la
    /// reserva, exactamente el caso F-2026-1038). La primera versión comparaba "sin cobro vivo EN LA
    /// MISMA MONEDA del reversal", pero la validación contra prod encontró un falso positivo real: la
    /// reserva interna 22 tiene un cobro VIVO de 2.030 anotado en ARS (sin ImputedCurrency, dato viejo
    /// pre-ADR-021) contra una factura en USD — su reversal en USD tiene plata real detrás, solo que
    /// mal etiquetada de moneda; borrarlo habría destruido la reversión de un cobro genuino. Ese tipo
    /// de cruce de monedas inconsistente NO se resuelve a ciegas con un UPDATE masivo: lo reporta el
    /// vigía nocturno de coherencia (<c>CoherenceWatchdogJob</c>) y se juzga caso por caso. Tampoco se
    /// toca el exceso PARCIAL (reserva que cobró algo pero menos que lo revertido), por el mismo motivo.</para>
    ///
    /// <para><b>Cómo se identifica el Payment roto</b>: un <c>Payment</c> es un <c>CreditNoteReversal</c>
    /// "vivo" (<c>IsDeleted = false</c>) cuya reserva (<c>Payments."TravelFileId"</c> — el rename
    /// Reserva→TravelFile nunca se aplicó a la columna real de la base, mismo desalineo que ya
    /// documentó <c>RepairPhantomCurrencyCreditNoteReversal</c> del 2026-07-08) NO tiene ningún otro
    /// <c>Payment</c> con <c>EntryType = 'Payment'</c> (cobro real) EN TODA SU HISTORIA — se cuentan
    /// también los cobros borrados o anulados a propósito (evidencia HISTÓRICA, hallazgo bloqueante
    /// del review de riesgo 2026-07-10): "no tiene cobro vivo hoy" NO distingue "nunca hubo cobro"
    /// (el bug, lo que hay que borrar) de "hubo un cobro real que después se anuló por el camino
    /// legítimo (<c>PaymentService.AnnulPaymentAsync</c>)" — en ese segundo caso el reversal fue
    /// correcto en su momento y NO se toca. Cinturón extra: <c>OriginalPaymentId IS NULL</c> (si el
    /// reversal matcheó un cobro original al crearse, hubo plata real, se excluye).</para>
    ///
    /// <para><b>Qué hace esta migración (mismo patrón en 2 pasos que
    /// <c>RepairPhantomCurrencyCreditNoteReversal</c> del 2026-07-08 y
    /// <c>RepairLegacyAnnulledReservaServices</c> del 2026-07-05)</b>:
    /// <list type="bullet">
    /// <item>PASO 0 — backup idempotente (<c>CREATE TABLE IF NOT EXISTS ... AS</c>) de los Payments
    ///   que se van a dar de baja, con su estado ANTES de tocar nada (para poder revertir a mano si
    ///   hiciera falta).</item>
    /// <item>PASO 1 — <c>UPDATE</c> que marca <c>IsDeleted = true</c> y <c>DeletedAt = NOW()</c> SOLO
    ///   en los Payments que cumplen el criterio de arriba. Es idempotente: en una segunda corrida el
    ///   filtro <c>"IsDeleted" = false</c> ya no encuentra esas filas (quedaron dadas de baja) y no
    ///   vuelve a tocarlas.</item>
    /// <item>NO recalcula el saldo/venta confirmada de la reserva en SQL crudo (eso es lógica de
    ///   dominio, no se reescribe acá). Al dar de baja el Payment de reversión (que ya no cuenta como
    ///   "vivo" para <c>ReservaMoneyCalculator.AccumulatePayments</c>, que filtra por
    ///   <c>!IsDeleted</c>), la proyección guardada de la reserva queda desactualizada hasta que el
    ///   vigía nocturno de coherencia (<c>CoherenceWatchdogJob</c> → W3 →
    ///   <c>CoherenceChecks.RepairStaleMoneyProjectionAsync</c>, corre TODAS las noches a las 6am UTC,
    ///   <c>Cron.Daily(6)</c> en <c>Program.cs</c>) compare la proyección contra el cálculo fresco y la
    ///   reescriba con el escritor canónico (<c>ReservaMoneyPersister.PersistAsync</c>). Si se quiere
    ///   destrabar antes de esa hora, existe el endpoint admin
    ///   <c>POST /api/admin/maintenance/coherence/run-watchdog</c> (barre TODAS las reservas no
    ///   archivadas, cubre este caso).</item>
    /// </list></para>
    ///
    /// <para><b>Consulta de validación (para correr en PROD, SOLO LECTURA, antes de aplicar la
    /// migración)</b> — cuenta exactamente las filas que el PASO 1 va a tocar:
    /// <code>
    /// SELECT COUNT(*)
    /// FROM "Payments" p
    /// WHERE p."EntryType" = 'CreditNoteReversal'
    ///   AND p."IsDeleted" = false
    ///   AND p."OriginalPaymentId" IS NULL
    ///   AND NOT EXISTS (
    ///         SELECT 1
    ///         FROM "Payments" cobro
    ///         WHERE cobro."TravelFileId" = p."TravelFileId"
    ///           AND cobro."EntryType" = 'Payment'
    ///       );
    /// -- Corrida en prod el 2026-07-10 (solo lectura): da 1 fila = el Payment #102 de F-2026-1038.
    /// -- (Variantes descartadas: con filtro de moneda daba 2 e incluía el falso positivo de la
    /// --  reserva 22; con "cobro vivo hoy" no distinguía el caso "cobro anulado después", hallazgo
    /// --  bloqueante del review de riesgo.)
    /// </code></para>
    ///
    /// <para><b>Por qué es SEGURA</b>: 0 cambios de esquema (el <c>ModelSnapshot</c> no se modifica).
    /// Backup previo en <c>_repair_20260710_phantom_debt_backup</c> (se deja en la base como red de
    /// seguridad forense, no se dropea). El <c>UPDATE</c> es idempotente. Solo toca
    /// <c>EntryType = 'CreditNoteReversal'</c> — nunca un Payment real de cobro/pago a proveedor. Un
    /// soft-delete es reversible (a diferencia de un DELETE físico): si algún caso resultara ser un
    /// falso positivo, alcanza con volver <c>IsDeleted</c> a <c>false</c> usando el backup como guía.</para>
    /// </summary>
    public partial class RepairPhantomDebtCreditNoteReversalNoCollection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ─────────────────────────────────────────────────────────────────────────────────────────
            // PASO 0 — BACKUP del estado previo (idempotente vía CREATE TABLE IF NOT EXISTS ... AS).
            // Guarda el Payment completo (Id, reserva, factura relacionada, monto, moneda) ANTES de
            // marcarlo IsDeleted, para poder auditar o revertir a mano si hiciera falta.
            // ─────────────────────────────────────────────────────────────────────────────────────────
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""_repair_20260710_phantom_debt_backup"" AS
                SELECT
                    p.""Id"" AS ""PaymentId"",
                    p.""TravelFileId"",
                    p.""RelatedInvoiceId"",
                    p.""OriginalPaymentId"",
                    p.""Amount"",
                    p.""Currency"",
                    p.""PaidAt"",
                    p.""IsDeleted"" AS ""IsDeletedBeforeRepair""
                FROM ""Payments"" p
                WHERE p.""EntryType"" = 'CreditNoteReversal'
                  AND p.""IsDeleted"" = false
                  AND p.""OriginalPaymentId"" IS NULL
                  AND NOT EXISTS (
                        SELECT 1
                        FROM ""Payments"" cobro
                        WHERE cobro.""TravelFileId"" = p.""TravelFileId""
                          AND cobro.""EntryType"" = 'Payment'
                      );
            ");

            // ─────────────────────────────────────────────────────────────────────────────────────────
            // PASO 1 — soft-delete de los Payments de reversión de reservas SIN NINGÚN cobro vivo en
            // NINGUNA moneda (criterio más duro que el del fix de código a propósito: la reparación
            // masiva solo toca lo indiscutible; el cruce de monedas inconsistente se juzga a mano).
            // ─────────────────────────────────────────────────────────────────────────────────────────
            migrationBuilder.Sql(@"
                UPDATE ""Payments"" p
                SET ""IsDeleted"" = true,
                    ""DeletedAt"" = NOW()
                WHERE p.""EntryType"" = 'CreditNoteReversal'
                  AND p.""IsDeleted"" = false
                  AND p.""OriginalPaymentId"" IS NULL
                  AND NOT EXISTS (
                        SELECT 1
                        FROM ""Payments"" cobro
                        WHERE cobro.""TravelFileId"" = p.""TravelFileId""
                          AND cobro.""EntryType"" = 'Payment'
                      );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op deliberado, mismo criterio que RepairPhantomCurrencyCreditNoteReversal y
            // RepairLegacyAnnulledReservaServices: esta migración NO toca esquema (nada estructural
            // que revertir) y la reversión exacta, de necesitarse, se hace a mano usando
            // "_repair_20260710_phantom_debt_backup" (columna IsDeletedBeforeRepair) como guía, que se
            // deja a propósito en la base como red de seguridad forense.
        }
    }
}
