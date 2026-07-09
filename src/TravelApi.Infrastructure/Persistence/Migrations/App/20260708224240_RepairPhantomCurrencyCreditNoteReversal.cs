using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// (2026-07-08, bug de plata confirmado con datos de prod, ej. reserva F-2026-1044) REPARACIÓN DE DATOS —
    /// corrige la moneda de los asientos de "reversión económica" que quedaron mal etiquetados cuando una Nota de
    /// Crédito en DÓLARES obtuvo CAE.
    ///
    /// <para><b>Qué problema arregla</b>: en <c>AfipService.ApplyPartialCreditNoteReversalAsync</c> y
    /// <c>ApplyTotalCreditNoteReversalAsync</c>, el <c>Payment</c> que revierte económicamente una NC (marca
    /// <c>EntryType = "CreditNoteReversal"</c>) nunca seteaba <c>Currency</c> explícitamente, así que quedaba
    /// pegado al default del entity ("ARS") SIEMPRE — aunque la NC fuera en dólares. Ejemplo real: la NC #97 de
    /// la reserva F-2026-1044, en DOL por 13.200, generó un Payment de reversión por −13.200 <b>ARS</b>. Resultado:
    /// el bucket de pesos bajaba 13.200 (que nunca había subido → quedaba "deuda fantasma" en ARS) mientras la
    /// plata real (USD) seguía mostrando saldo a favor sin descontar. Eso rompía el cartel de plata de la ficha y
    /// alimentaba los avisos del vigía de "anulada con deuda sin comprobante". El código ya se corrigió (setea
    /// <c>Currency</c> con la moneda real de la NC vía <c>ArcaCurrencyMapper.ToIso(invoice.MonId)</c>); esta
    /// migración es la reparación de los datos que ya quedaron mal escritos en producción ANTES del fix.</para>
    ///
    /// <para><b>Cómo se identifica el Payment roto</b>: <c>Payments."RelatedInvoiceId"</c> es la FK REAL (no texto
    /// libre) a la Nota de Crédito que generó la reversión — es el mismo campo que usa el propio
    /// <c>ApplyCreditNoteEconomicReversalAsync</c> para buscar si ya existe el reversal (idempotencia). Un Payment
    /// está roto si es un <c>CreditNoteReversal</c> cuya <c>Currency</c> NO coincide con la moneda ISO real de la
    /// factura que referencia (<c>Invoices."MonId"</c>: "DOL" → debería ser "USD"; "PES"/null/vacío → "ARS", mismo
    /// mapeo que <c>ArcaCurrencyMapper.ToIso</c>).</para>
    ///
    /// <para><b>Qué hace esta migración (paso 1 de 2, mismo patrón que <c>RepairLegacyAnnulledReservaServices</c>
    /// del 2026-07-05)</b>:
    /// <list type="bullet">
    /// <item>PASO 0 — backup idempotente (<c>CREATE TABLE IF NOT EXISTS ... AS</c>) de los Payments que se van a
    ///   corregir, con su <c>Currency</c> VIEJA, antes de tocar nada.</item>
    /// <item>PASO 1 — <c>UPDATE</c> que corrige <c>Currency</c> a la moneda real de la factura, SOLO en los
    ///   Payments <c>CreditNoteReversal</c> cuya moneda no coincide (criterio conservador: si
    ///   <c>RelatedInvoiceId</c> no matchea ninguna factura, o la fila ya tiene la moneda correcta, el <c>WHERE</c>
    ///   la deja intacta — no se toca nada "por las dudas").</item>
    /// <item>NO recalcula el saldo/venta confirmada de la reserva en SQL crudo (eso es lógica de dominio, no
    ///   se reescribe acá). Ese PASO 2 lo hace el vigía nocturno de coherencia
    ///   (<c>CoherenceWatchdogJob</c> → <c>CoherenceChecks.RepairStaleMoneyProjectionAsync</c>, W3), que YA corre
    ///   todas las noches a las 6am UTC (<c>Cron.Daily(6)</c>, registrado en <c>Program.cs</c>) sobre TODAS las
    ///   reservas no archivadas (no solo las anuladas): compara la proyección guardada contra el cálculo fresco
    ///   (que lee <c>Payment.Currency</c> vía <c>ReservaMoneyCalculator.AccumulatePayments</c>) y, si difiere,
    ///   reescribe escalar + tabla hija con el escritor canónico (<c>ReservaMoneyPersister.PersistAsync</c>). Una
    ///   vez que esta migración corrige la <c>Currency</c> del Payment, la PRÓXIMA corrida del vigía (o el endpoint
    ///   admin manual <c>POST /api/admin/maintenance/coherence/run-watchdog</c>, si se quiere destrabar antes de
    ///   esa hora) va a ver la diferencia y reacomodar el saldo por moneda solo. OJO (hallazgo del review
    ///   2026-07-09): el otro endpoint admin, <c>recalculate-money</c>, NO sirve como atajo acá — corre
    ///   <c>CoherenceMoneyRecalculator.RecalculateAnnulledReservasMoneyAsync</c>, que solo barre reservas ANULADAS,
    ///   así que una NC parcial sobre una reserva viva quedaría afuera; <c>run-watchdog</c> (W3) barre TODAS las no
    ///   archivadas y cubre los dos casos. VERIFICADO leyendo
    ///   <c>CoherenceChecks.RepairStaleMoneyProjectionAsync</c> y <c>ReservaMoneyCalculator.AccumulatePayments</c>
    ///   antes de confiar en este mecanismo (no es una suposición).</item>
    /// </list></para>
    ///
    /// <para><b>Por qué es SEGURA</b>: 0 cambios de esquema (el <c>ModelSnapshot</c> no se modifica). Backup previo
    /// en <c>_repair_20260708_phantom_currency_backup</c> (se deja en la base como red de seguridad forense, no se
    /// dropea). El <c>UPDATE</c> es idempotente (una segunda corrida no encuentra filas para tocar, porque el
    /// <c>WHERE</c> excluye las que ya tienen la moneda correcta). Solo toca <c>EntryType = 'CreditNoteReversal'</c>
    /// — nunca un Payment real de cobro/pago a proveedor.</para>
    /// </summary>
    public partial class RepairPhantomCurrencyCreditNoteReversal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ─────────────────────────────────────────────────────────────────────────────────────────
            // PASO 0 — BACKUP del estado previo (idempotente vía CREATE TABLE IF NOT EXISTS ... AS).
            // Guarda la Currency VIEJA (la rota) de cada Payment que el PASO 1 va a corregir, más los datos
            // mínimos para poder ubicarlo (ReservaId, RelatedInvoiceId, Amount) si hiciera falta auditar.
            // ─────────────────────────────────────────────────────────────────────────────────────────
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""_repair_20260708_phantom_currency_backup"" AS
                SELECT
                    p.""Id"" AS ""PaymentId"",
                    p.""ReservaId"",
                    p.""RelatedInvoiceId"",
                    p.""Amount"",
                    p.""Currency"" AS ""CurrencyBeforeRepair"",
                    i.""MonId"" AS ""InvoiceMonId"",
                    CASE WHEN UPPER(TRIM(COALESCE(i.""MonId"", ''))) = 'DOL' THEN 'USD' ELSE 'ARS' END
                        AS ""CurrencyAfterRepair""
                FROM ""Payments"" p
                JOIN ""Invoices"" i ON i.""Id"" = p.""RelatedInvoiceId""
                WHERE p.""EntryType"" = 'CreditNoteReversal'
                  AND p.""Currency"" IS DISTINCT FROM (
                        CASE WHEN UPPER(TRIM(COALESCE(i.""MonId"", ''))) = 'DOL' THEN 'USD' ELSE 'ARS' END
                      );
            ");

            // ─────────────────────────────────────────────────────────────────────────────────────────
            // PASO 1 — corregir la Currency del Payment de reversión a la moneda REAL de la factura/NC que
            // referencia. Mismo mapeo que ArcaCurrencyMapper.ToIso: "DOL" -> "USD"; cualquier otra cosa
            // (incluido "PES", null o vacío) -> "ARS" (regla legacy: sin moneda explícita se lee como pesos,
            // igual que Monedas.Normalizar).
            // ─────────────────────────────────────────────────────────────────────────────────────────
            migrationBuilder.Sql(@"
                UPDATE ""Payments"" p
                SET ""Currency"" = CASE
                        WHEN UPPER(TRIM(COALESCE(i.""MonId"", ''))) = 'DOL' THEN 'USD'
                        ELSE 'ARS'
                    END
                FROM ""Invoices"" i
                WHERE i.""Id"" = p.""RelatedInvoiceId""
                  AND p.""EntryType"" = 'CreditNoteReversal'
                  AND p.""Currency"" IS DISTINCT FROM (
                        CASE WHEN UPPER(TRIM(COALESCE(i.""MonId"", ''))) = 'DOL' THEN 'USD' ELSE 'ARS' END
                      );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op deliberado, mismo criterio que RepairLegacyAnnulledReservaServices: esta migración NO toca
            // esquema (nada estructural que revertir) y la reversión exacta, de necesitarse, se hace a mano desde
            // "_repair_20260708_phantom_currency_backup" (columna CurrencyBeforeRepair), que se deja a propósito en
            // la base como red de seguridad forense.
        }
    }
}
