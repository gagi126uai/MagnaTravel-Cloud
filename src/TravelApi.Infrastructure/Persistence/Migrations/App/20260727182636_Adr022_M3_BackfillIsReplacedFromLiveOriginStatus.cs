using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// Firma de Gaston (adenda 2026-07-27 tarde, docs/ux/guia-ux-gaston.md) — BACKFILL DE DATOS, corre
    /// UNA SOLA VEZ. Completa <c>CashLedgerEntry.IsReplaced</c> para la historia VIEJA del Libro de Caja
    /// (todo lo que se editó/anuló ANTES de la migración que agregó la columna,
    /// <c>20260727165909_Adr022_M2_AddIsReplacedToCashLedgerEntry</c>, que nace de <c>IsReplaced=false</c>
    /// para todo lo existente).
    ///
    /// <para><b>Que problema resuelve</b>: el Libro de Caja distingue el par (asiento viejo + su reversa)
    /// de una EDICION ("Reemplazado") del par de una ANULACION real ("Anulado") — ver el código en vivo en
    /// <c>TreasuryService.UpdateManualMovementAsync</c>/<c>DeleteManualMovementAsync</c>,
    /// <c>PaymentService.UpdatePaymentAsync</c>/<c>DeletePaymentCoreAsync</c>,
    /// <c>SupplierService.UpdateSupplierPaymentAsync</c>/<c>DeleteSupplierPaymentAsync</c> y sus espejos
    /// legacy en <c>ReservaService</c>. Ese código SOLO sabe distinguir "Reemplazado" de "Anulado" para
    /// pares que se crean DE ACA EN ADELANTE — toda la historia anterior a este deploy quedó con
    /// <c>IsReplaced=false</c> ("Anulado") aunque haya nacido de una edición. Este backfill corrige esa
    /// historia.</para>
    ///
    /// <para><b>Criterio REAL (no es simplemente "el mismo del código en vivo" — el código en vivo NUNCA
    /// tuvo que lidiar con un origen que revive después de anulado, porque marca <c>IsReplaced</c> en el
    /// mismo instante en que decide editar vs. anular; este backfill mira una FOTO del estado actual, que
    /// puede ser el resultado de VARIAS transiciones histéricas encadenadas)</b>: un asiento pertenece a un
    /// "par" cuando <c>IsReversed=true</c> (el original) o <c>IsReversal=true</c> (su reversa, vinculada por
    /// <c>ReversedEntryId</c> al original). El par se marca <c>IsReplaced=true</c> cuando su origen está
    /// VIVO **Y** no tiene rastro de haber sido anulado alguna vez:
    /// <list type="bullet">
    /// <item><b>ManualCashMovement</b>: vivo = <c>ManualCashMovements."IsVoided" = false</c>. Esta entidad
    ///   no tiene funcionalidad de "restaurar" (no hay <c>RestoreManualMovementAsync</c>), así que "vivo"
    ///   alcanza solo: si nunca se pudo revivir después de anulado, no hay ambigüedad posible.</item>
    /// <item><b>SupplierPayment</b>: vivo = <c>SupplierPayments."IsDeleted" = false</c>. Mismo argumento: no
    ///   existe <c>RestoreSupplierPaymentAsync</c> en este código, "vivo" alcanza solo.</item>
    /// <item><b>Payment</b>: vivo = <c>Payments."IsDeleted" = false</c> **Y ADEMÁS** sin un
    ///   <c>AuditLogs</c> con <c>EntityName='Payment'</c>, <c>Action='PaymentAnnulled'</c> y
    ///   <c>EntityId = Payment.Id::text</c> (ver hallazgo B4 mas abajo). Este SI necesita el chequeo extra
    ///   porque Payment ES la única de las 3 entidades con un camino de "Papelera" real
    ///   (<c>PaymentService.RestorePaymentAsync</c>, PaymentService.cs:1361) que revive un Payment
    ///   soft-deleted (<c>IsDeleted = false</c>) SIN tocar el par de asientos que dejó su anulación.</item>
    /// </list>
    /// No hace falta un JOIN explícito entre el original y su reversa: cada FILA (sea el original o la
    /// reversa) ya carga su propia FK al origen (<c>ManualCashMovementId</c>/<c>PaymentId</c>/
    /// <c>SupplierPaymentId</c> — ver <see cref="TravelApi.Domain.Helpers.CashLedgerEntryFactory.Reverse"/>,
    /// que copia el FK de origen a la reversa para trazabilidad), así que basta con mirar el estado VIVO
    /// del origen y aplicar el UPDATE a CUALQUIER fila (reversada o reversa) que apunte a él.</para>
    ///
    /// <para><b>Hallazgo B4 (re-review 2026-07-27, bloqueante) — Payment anulado y RESTAURADO desde la
    /// Papelera</b>: <c>RestorePaymentAsync</c> (PaymentService.cs:1370-1371) hace <c>payment.IsDeleted =
    /// false; payment.DeletedAt = null;</c> y agrega un asiento de caja NUEVO (re-asienta el cobro), pero
    /// **NO toca** el par viejo de la anulación (el original que quedó <c>IsReversed=true</c> + su reversa
    /// <c>IsReversal=true</c> siguen intactos, tal cual quedaron). Si solo mirásemos
    /// <c>Payments."IsDeleted" = false</c>, ese par histórico de una ANULACIÓN real quedaría marcado
    /// "Reemplazado" — exactamente al revés de lo que pasó. <b>Fix</b>: excluir cualquier Payment que tenga
    /// un <c>AuditLogs</c> con <c>Action = 'PaymentAnnulled'</c> para su <c>Id</c> — esa acción de auditoría
    /// (escrita por <c>AnnulPaymentAsync</c>, PaymentService.cs:1905-1912) es un rastro PERMANENTE: sigue
    /// existiendo aunque el pago se restaure después (nadie borra AuditLogs), así que sirve para detectar
    /// "este Payment fue anulado alguna vez" sin depender del estado actual de <c>IsDeleted</c>.</para>
    ///
    /// <para><b>Las DOS entradas a <c>DeletePaymentCoreAsync</c> (leído PaymentService.cs:1835-1921 y
    /// 2003-2045) y sus rastros de auditoría</b>:
    /// <list type="bullet">
    /// <item><c>AnnulPaymentAsync</c> (PaymentService.cs:1876): escribe <c>AuditLogs</c> con
    ///   <c>Action='PaymentAnnulled'</c> DESPUÉS de llamar a <c>DeletePaymentCoreAsync</c>
    ///   (PaymentService.cs:1905-1912). Este backfill lo excluye.</item>
    /// <item><c>DeletePaymentAsync</c> (PaymentService.cs:1835, el DELETE LIBRE — bloqueado en estados
    ///   terminales, gate en <c>EnsurePaymentEditableByStateAsync</c>): NO escribe NINGÚN <c>AuditLogs</c>,
    ///   ni él ni <c>DeletePaymentCoreAsync</c> (PaymentService.cs:2003-2045) por sí solo. No hay una
    ///   segunda Action tipo "PaymentDeleted" que excluir — literalmente no existe rastro de auditoría de
    ///   este camino hoy.</item>
    /// </list>
    /// <b>Residuo ACEPTADO (fuera del alcance de esta migración)</b>: un Payment borrado por el DELETE
    /// LIBRE (nunca anulado con <c>AnnulPaymentAsync</c>) y DESPUÉS restaurado desde la Papelera NO tiene
    /// ningún rastro que este backfill pueda usar para detectar que fue una anulación real — quedaría
    /// marcado "Reemplazado" igual que el bug B4 original, por una puerta distinta. Medido en PROD con el
    /// diagnóstico de solo lectura: 0 casos hoy (ver SQL de verificación del reporte de esta tanda). Cerrar
    /// esto del todo requeriría que <c>DeletePaymentAsync</c> deje su propio rastro de auditoría (fuera del
    /// alcance de este backfill; anotado como trabajo futuro si algún día aparece un caso real).</para>
    ///
    /// <para><b>Caso borde ACEPTADO #2 (documentado, no se resuelve acá)</b>: un movimiento/pago EDITADO y
    /// DESPUÉS ANULADO/BORRADO (sin restaurar). El par de la edición vieja (que en su momento fue un
    /// "Reemplazado" legítimo) queda con <c>IsReplaced=false</c> ("Anulado") en este backfill, porque el
    /// criterio solo mira el estado FINAL del origen, no su historia completa de transiciones intermedias.
    /// Es indistinguible sin una bitácora de estados que este proyecto no tiene para estas 3 entidades.
    /// Aceptado: es un dato HISTÓRICO cosmético (el badge de una fila vieja), no afecta ningún saldo ni
    /// cálculo de caja.</para>
    ///
    /// <para><b>Por qué SQL crudo, UPDATE puro (no borra ni crea filas)</b>: mismo patrón establecido en
    /// este repositorio para reparaciones de datos (ver <c>Adr048_M1_RepairLegacyAnnulledReservaState</c>).
    /// NO cambia el esquema (0 columnas/índices nuevos, el <c>ModelSnapshot</c> no cambia). Nombres de
    /// tabla/columna verificados contra <c>AppDbContext.cs</c>/entidades (ninguna de las 5 tablas tocadas —
    /// <c>CashLedgerEntries</c>, <c>Payments</c>, <c>SupplierPayments</c>, <c>ManualCashMovements</c>,
    /// <c>AuditLogs</c>— tiene <c>ToTable</c> con un nombre distinto al del <c>DbSet</c>; a diferencia de
    /// <c>Reserva</c> -&gt; <c>"TravelFiles"</c>, estas 5 NUNCA sufrieron un rename manual en producción).
    /// <c>AuditLog.EntityId</c> es <c>string</c> (columna <c>varchar</c>), por eso el <c>::text</c> al
    /// comparar contra <c>Payments."Id"</c> (columna <c>integer</c>).</para>
    ///
    /// <para><b>Idempotencia</b>: cada <c>UPDATE</c> filtra <c>"IsReplaced" = false</c>, así que correrlo
    /// dos veces (no debería pasar — EF lo marca en <c>__EFMigrationsHistory</c>) no vuelve a tocar filas
    /// ya marcadas ni las "desmarca".</para>
    ///
    /// <para><b>Down = no-op documentado</b>: revertir ESTE backfill con precisión no es posible una vez
    /// que la aplicación ya desplegada empezó a escribir <c>IsReplaced</c> correcto para movimientos
    /// NUEVOS (mismo predicado "origen vivo" que usa este backfill): un <c>UPDATE ... SET "IsReplaced" =
    /// false</c> con el mismo criterio revertiría INDISTINTAMENTE tanto el backfill histórico de esta
    /// migración como las ediciones REALES posteriores al deploy, perdiendo dato legítimo. La única
    /// reversión segura es dropear la columna entera, y eso ya es el <c>Down</c> de la migración ANTERIOR
    /// (<c>20260727165909_Adr022_M2_AddIsReplacedToCashLedgerEntry</c>), no de esta. Si algún día hiciera
    /// falta deshacer SOLO el backfill (sin dropear la columna), habría que auditar manualmente contra un
    /// snapshot de antes del deploy — no hay forma automática y segura.</para>
    /// </summary>
    public partial class Adr022_M3_BackfillIsReplacedFromLiveOriginStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // (a) ManualCashMovement: el movimiento sigue vivo (no anulado) -> el par que lo revirtio
            // nacio de una EDICION, no de una anulacion real. No existe "restaurar" para esta entidad, asi
            // que IsVoided=false alcanza solo (no hay caso B4 equivalente aca).
            migrationBuilder.Sql("""
                UPDATE "CashLedgerEntries" AS e
                SET "IsReplaced" = TRUE
                FROM "ManualCashMovements" AS m
                WHERE e."ManualCashMovementId" = m."Id"
                  AND (e."IsReversed" = TRUE OR e."IsReversal" = TRUE)
                  AND e."IsReplaced" = FALSE
                  AND m."IsVoided" = FALSE;
                """);

            // (b) Payment (cobro de cliente): el cobro sigue vivo (no anulado/borrado) Y ADEMAS nunca dejo
            // un rastro de auditoria "PaymentAnnulled" para este Id.
            //
            // Hallazgo B4 (re-review 2026-07-27, bloqueante): sin la segunda condicion, un cobro anulado y
            // DESPUES RESTAURADO desde la Papelera (RestorePaymentAsync pone IsDeleted=false sin tocar el
            // par de la anulacion) quedaria marcado "Reemplazado" siendo una anulacion real. El rastro de
            // auditoria "PaymentAnnulled" (escrito por AnnulPaymentAsync, PaymentService.cs:1905-1912) es
            // PERMANENTE: sigue existiendo aunque el pago se restaure, asi que sirve de marca indeleble de
            // "esto fue anulado alguna vez", sin depender del estado actual (que puede haber cambiado por la
            // restauracion). Ver el XML-doc de la clase para el residuo aceptado (DELETE libre sin rastro).
            migrationBuilder.Sql("""
                UPDATE "CashLedgerEntries" AS e
                SET "IsReplaced" = TRUE
                FROM "Payments" AS p
                WHERE e."PaymentId" = p."Id"
                  AND (e."IsReversed" = TRUE OR e."IsReversal" = TRUE)
                  AND e."IsReplaced" = FALSE
                  AND p."IsDeleted" = FALSE
                  AND NOT EXISTS (
                    SELECT 1 FROM "AuditLogs" AS al
                    WHERE al."EntityName" = 'Payment'
                      AND al."Action" = 'PaymentAnnulled'
                      AND al."EntityId" = p."Id"::text
                  );
                """);

            // (c) SupplierPayment (pago a proveedor): el pago sigue vivo -> idem (a). No existe
            // "restaurar" para esta entidad, asi que IsDeleted=false alcanza solo.
            migrationBuilder.Sql("""
                UPDATE "CashLedgerEntries" AS e
                SET "IsReplaced" = TRUE
                FROM "SupplierPayments" AS sp
                WHERE e."SupplierPaymentId" = sp."Id"
                  AND (e."IsReversed" = TRUE OR e."IsReversal" = TRUE)
                  AND e."IsReplaced" = FALSE
                  AND sp."IsDeleted" = FALSE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op INTENCIONAL — ver el XML-doc de la clase ("Down = no-op documentado"): revertir este
            // backfill con el mismo predicado ("origen vivo") también revertiría ediciones REALES escritas
            // por la aplicación ya desplegada, no solo el backfill histórico. No hay forma automática y
            // segura de distinguir una cosa de la otra. Si hiciera falta un rollback completo de la
            // FUNCIONALIDAD (no solo del backfill), se revierte la migración anterior
            // (Adr022_M2_AddIsReplacedToCashLedgerEntry), que sí dropea la columna entera.
        }
    }
}
