using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Constants;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// ADR-044 T5-emision (2026-07-15, diseño §6.2b): reconciliador DEDICADO para la Nota de Credito parcial de
/// una cancelacion PARCIAL (se canceló UN servicio de una reserva facturada, la factura destino sigue viva por
/// el resto). Molde EXACTO de <see cref="DebitNoteAnnulmentReconciliation"/> (misma forma: lock por FOR UPDATE,
/// re-chequeo anti-carrera, blindado desde el caller).
///
/// <para><b>POR QUE EXISTE ESTE RECONCILIADOR SEPARADO (B1, el bloqueante mas grave del diseño)</b>: el job que
/// resuelve el CAE de cualquier comprobante (<c>AfipService.ProcessInvoiceJob</c>) NUNCA llama al reconciliador
/// de hijas ADR-042 (<c>OnArcaSucceededAsync</c>/<c>HandleArcaAnnulmentCallbackAsync</c>) — ese solo corre desde
/// los caminos especializados de la anulacion TOTAL. Y reusarlo para T5 seria PELIGROSO, no neutro: con la
/// unica hija del BC T5 en <c>Succeeded</c>, ese reconciliador evaluaria "todas las hijas OK" y transicionaria
/// el BC como si fuera una anulacion TOTAL de la reserva (<c>AwaitingOperatorRefund</c> + <c>ND fantasma</c>
/// via <c>TryEmitDebitNotePostCompletionAsync</c>) — una anulacion total FANTASMA sobre una cancelacion
/// PARCIAL. Por eso esta clase es un camino 100% aparte, con su propia semantica: (a) marcar la hija
/// Succeeded/Failed; (b) derivar <c>Invoice.AnnulmentStatus</c> de la factura DESTINO (Succeeded SOLO si el
/// remanente queda en 0 — la ultima porcion; si no, la factura sigue viva, NUNCA se fuerza); (c) avanzar el
/// BC T5 por SU circuito (el receivable del operador de ESE servicio, via <c>AwaitingOperatorRefund</c> —
/// el mismo estado que ya sabe cerrar solo cuando no hay receivable pendiente, ver
/// <c>BookingCancellationService.CloseZeroReceivableCancellationsAsync</c>). NUNCA marca la reserva anulada,
/// NUNCA emite una Nota de Debito automatica.</para>
///
/// <para><b>Lookup especifico e inequivoco (evita colisionar con ADR-042)</b>: hija
/// <see cref="BookingCancellationCreditNote"/> por <c>CreditNoteInvoiceId == nc.Id &amp;&amp; Status == Pending</c>,
/// filtrando ADEMAS a que su BC sea PURAMENTE parcial (≥1 linea <c>Scope=Partial</c>, 0 lineas <c>Scope=Full</c>
/// — el mismo predicado que usa la bandeja "Comprobantes por resolver", V4). Ese filtro garantiza que este
/// reconciliador NUNCA toque una hija de una anulacion TOTAL ADR-042 (esas tienen lineas <c>Full</c>; se
/// reconcilian por su propio camino, <c>OnArcaSucceededAsync</c>, jamas por aca). Da 0 filas para cualquier
/// otra NC (una ND, una NC-anula-ND del "Deshacer", una NC total) — no-op barato.</para>
///
/// <para><b>Por que <c>static</c> con <c>AppDbContext</c> explicito</b>: igual razon que
/// <see cref="DebitNoteAnnulmentReconciliation"/> — evita el ciclo de dependencias
/// AfipService -&gt; IBookingCancellationService -&gt; IInvoiceService -&gt; IAfipService -&gt; AfipService. El
/// remanente ACREDITABLE lo calcula con el MISMO overload estatico que usa el gate de emision
/// (<see cref="BookingCancellationService.ComputeInvoiceRemainingCreditableAmountAsync(AppDbContext, int, CancellationToken, int?)"/>):
/// una sola formula, nunca duplicada.</para>
/// </summary>
public static class PartialCreditNoteT5Reconciliation
{
    /// <summary>Mensaje cuando ARCA rechaza la NC parcial sin devolver texto. Mismo literal historico del modulo.</summary>
    public const string ArcaRejectedWithoutMessage = "ARCA rechazo la Nota de Crédito sin mensaje.";

    /// <summary>La columna <c>ArcaErrorMessage</c> tolera hasta 1000 chars; truncamos a esa cota.</summary>
    public const int ArcaErrorMaxLength = 1000;

    /// <summary>
    /// Busca la hija T5 (si existe) cuyo <c>CreditNoteInvoiceId</c> es <paramref name="resolvedInvoice"/> y
    /// sigue <c>Pending</c>, y le aplica la reconciliacion. Pensado para llamarse desde
    /// <c>AfipService.ProcessInvoiceJob</c> ni bien la Invoice queda resuelta, para TODA NC (el lookup da 0
    /// filas para cualquier NC que no sea una NC parcial T5 — caso barato, no toca nada).
    /// </summary>
    /// <returns>true si reconcilio algo (hija T5 encontrada y transicionada); false si no aplicaba.</returns>
    public static async Task<bool> TryReconcileAsync(
        AppDbContext db,
        Invoice resolvedInvoice,
        IAuditService? auditService,
        ILogger logger,
        CancellationToken ct)
    {
        // Lectura barata FUERA del lock: solo decide si hay algo que hacer. El re-chequeo real (idempotencia +
        // forma purely-partial) corre DENTRO del lock, sobre datos frescos.
        var childRef = await db.BookingCancellationCreditNotes
            .AsNoTracking()
            .Where(c => c.CreditNoteInvoiceId == resolvedInvoice.Id
                     && c.Status == BookingCancellationCreditNoteStatus.Pending)
            .Select(c => new { c.Id, c.BookingCancellationId })
            .FirstOrDefaultAsync(ct);

        if (childRef is null)
        {
            return false; // No es una NC parcial T5 en vuelo. No-op barato (el caso comun: cualquier otra NC/ND).
        }

        bool arcaApproved =
            string.Equals(resolvedInvoice.Resultado, "A", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(resolvedInvoice.CAE);
        bool arcaRejected = string.Equals(resolvedInvoice.Resultado, "R", StringComparison.OrdinalIgnoreCase);

        if (!arcaApproved && !arcaRejected)
        {
            return false; // "PENDING" o null: sigue en vuelo, no tocamos nada.
        }

        if (!db.Database.IsRelational())
        {
            // InMemory (tests unitarios): no soporta FOR UPDATE ni transacciones. Corremos el cuerpo directo
            // (la serializacion real bajo carrera se valida en integracion Postgres).
            return await ApplyAsync(db, childRef.Id, childRef.BookingCancellationId, arcaApproved, resolvedInvoice, auditService, logger, ct);
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // lock_timeout acotado: mismo criterio que el resto del modulo (RunUnderInvoiceLockAsync /
            // RunUnderParentLockAsync / DebitNoteAnnulmentReconciliation). Si otro worker retiene el lock del
            // BC > 5s, tira excepcion; Hangfire reintenta el job limpio (flujo idempotente).
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'", ct);

            await db.Database.ExecuteSqlRawAsync(
                "SELECT 1 FROM \"BookingCancellations\" WHERE \"Id\" = {0} FOR UPDATE",
                new object[] { childRef.BookingCancellationId }, ct);

            var result = await ApplyAsync(db, childRef.Id, childRef.BookingCancellationId, arcaApproved, resolvedInvoice, auditService, logger, ct);

            await tx.CommitAsync(ct);
            return result;
        });
    }

    /// <summary>
    /// Cuerpo de la reconciliacion, YA bajo lock (o en InMemory, directo). Re-lee la hija + el BC FRESCOS
    /// (anti-carrera: otro worker pudo haber ganado la carrera y ya haberla resuelto) y aplica el efecto.
    /// </summary>
    private static async Task<bool> ApplyAsync(
        AppDbContext db,
        int childId,
        int bookingCancellationId,
        bool arcaApproved,
        Invoice resolvedInvoice,
        IAuditService? auditService,
        ILogger logger,
        CancellationToken ct)
    {
        var child = await db.BookingCancellationCreditNotes
            .FirstOrDefaultAsync(c => c.Id == childId, ct);

        // Re-chequeo anti carrera (redelivery de Hangfire, o dos reconciliaciones casi simultaneas): si ya no
        // esta Pending, otra corrida ya la resolvio. No-op.
        if (child is null || child.Status != BookingCancellationCreditNoteStatus.Pending)
        {
            return false;
        }

        // Forma purely-partial, re-verificada DENTRO del lock (defensa en profundidad — la lectura de arriba
        // ya filtraba solo hijas Pending, pero la composicion de lineas del BC puede haber cambiado en teoria
        // si algun flujo agrego una linea Full despues; en la practica esto no deberia pasar, pero preferimos
        // no asumir).
        var lineScopes = await db.BookingCancellationLines
            .AsNoTracking()
            .Where(l => l.BookingCancellationId == bookingCancellationId)
            .Select(l => l.Scope)
            .ToListAsync(ct);
        bool isPurelyPartial = lineScopes.Contains(BookingCancellationLineScope.Partial)
                             && !lineScopes.Contains(BookingCancellationLineScope.Full);
        if (!isPurelyPartial)
        {
            // No deberia pasar (el guard de emision ya lo exige), pero si pasara NO tocamos nada: este
            // reconciliador es EXCLUSIVO de BCs puramente parciales (B1, evita colisionar con ADR-042).
            logger.LogError(
                "PartialCreditNoteT5Reconciliation: hija {ChildId} del BC {BcId} ya no es puramente parcial. " +
                "No se reconcilia por este camino (posible bug aguas arriba).",
                childId, bookingCancellationId);
            return false;
        }

        var bc = await db.BookingCancellations
            .Include(b => b.Reserva)
            .FirstOrDefaultAsync(b => b.Id == bookingCancellationId, ct);
        if (bc is null)
        {
            // Defensivo (no deberia pasar: FK). Marcamos la hija igual para no dejarla huerfana en Pending.
            child.Status = arcaApproved ? BookingCancellationCreditNoteStatus.Succeeded : BookingCancellationCreditNoteStatus.Failed;
            if (!arcaApproved)
                child.ArcaErrorMessage = TruncateArcaError(resolvedInvoice.Observaciones);
            await db.SaveChangesAsync(ct);
            logger.LogError(
                "PartialCreditNoteT5Reconciliation: BC {BcId} no encontrado al reconciliar la hija {ChildId}.",
                bookingCancellationId, childId);
            return true;
        }

        if (!arcaApproved)
        {
            // Rechazo de ARCA: la hija queda Failed, la factura destino NUNCA se toco (sigue viva). El BC
            // vuelve a Drafted — mismo estado que exige el guard de emision (INV-T5-EMIT-STATE) — para que el
            // usuario pueda "Reintentar" desde el MISMO paso (confirmar-y-emitir reusa esta hija Failed y le
            // asigna una NC nueva, ver BookingCancellationService.EmitOnePartialCreditNoteAsync).
            child.Status = BookingCancellationCreditNoteStatus.Failed;
            child.ArcaErrorMessage = TruncateArcaError(resolvedInvoice.Observaciones);
            bc.Status = BookingCancellationStatus.Drafted;
            bc.ArcaErrorMessage = child.ArcaErrorMessage;

            StageAudit(db, auditService, bc, child, resolvedInvoice, succeeded: false, remainingAfterThisNc: null, logger);

            await db.SaveChangesAsync(ct);

            logger.LogWarning(
                "metric:t5_partial_credit_note_rejected | BcPublicId={BcPublicId} TargetInvoiceId={TargetInvoiceId} " +
                "CreditNoteInvoiceId={CreditNoteInvoiceId}",
                bc.PublicId, child.OriginatingInvoiceId, resolvedInvoice.Id);
            return true;
        }

        // Aprobado: la hija pasa a Succeeded. CreditNoteInvoiceId YA esta seteado desde la creacion (invariante
        // dura B2) — no hace falta re-setearlo aca.
        child.Status = BookingCancellationCreditNoteStatus.Succeeded;
        child.ArcaErrorMessage = null;

        // Derivar AnnulmentStatus de la factura DESTINO (Bloqueante #3): recalcular el remanente FRESCO, bajo
        // el MISMO lock que ya tomamos sobre el BC — la factura no tiene lock propio aca porque el candado de
        // escritura de este reconciliador es el BC (childRef.BookingCancellationId), pero el remanente se lee
        // de datos YA persistidos (la hija recien paso a Succeeded arriba, dentro de esta misma unidad de
        // trabajo) asi que la lectura es consistente. Solo si el remanente llega A CERO (esta era la ULTIMA
        // porcion) la factura pasa a Succeeded; si queda > 0, NO se toca (sigue viva y cobrable/facturable).
        var targetInvoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == child.OriginatingInvoiceId, ct);
        decimal? remainingAfterThisNc = null;
        if (targetInvoice is not null)
        {
            remainingAfterThisNc = await BookingCancellationService.ComputeInvoiceRemainingCreditableAmountAsync(
                db, targetInvoice.Id, ct);
            if (remainingAfterThisNc.Value == 0m && targetInvoice.AnnulmentStatus != AnnulmentStatus.Succeeded)
            {
                targetInvoice.AnnulmentStatus = AnnulmentStatus.Succeeded;
                targetInvoice.AnnulledAt = DateTime.UtcNow;
            }
        }

        // Spec UX 2026-07-17 (T5 varios pendientes): esta cancelacion puede tener OTROS servicios (otras
        // lineas Partial) todavia sin su devolucion emitida — el caso real de Gastón (hotel USD + excursion
        // ARS, cada uno con su propia NC). Si avanzaramos el BC a AwaitingOperatorRefund apenas se emite UNA
        // de ellas, el guard de resolver/emitir (que exige bc.Status == Drafted) dejaria a las demas lineas
        // HUERFANAS PARA SIEMPRE (AwaitingOperatorRefund nunca vuelve a Drafted por si solo). Por eso: solo
        // avanzamos cuando NO queda ninguna otra linea pendiente (sin resolver, o resuelta pero sin su NC
        // Succeeded todavia). Mientras quede alguna, el BC se queda en Drafted para que el resto se pueda
        // seguir resolviendo/emitiendo, cada uno por separado.
        var allPartialLineTargets = await db.BookingCancellationLines
            .AsNoTracking()
            .Where(l => l.BookingCancellationId == bookingCancellationId
                     && l.Scope == BookingCancellationLineScope.Partial)
            .Select(l => l.TargetInvoiceId)
            .ToListAsync(ct);

        var succeededInvoiceIdsSoFar = (await db.BookingCancellationCreditNotes
            .AsNoTracking()
            .Where(c => c.BookingCancellationId == bookingCancellationId
                     && c.Status == BookingCancellationCreditNoteStatus.Succeeded)
            .Select(c => c.OriginatingInvoiceId)
            .ToListAsync(ct))
            .ToHashSet();
        // La hija de ESTA reconciliacion todavia no esta guardada (recien se marco Succeeded arriba, en
        // memoria): la sumamos a mano para que la cuenta de "que falta" ya la contemple.
        succeededInvoiceIdsSoFar.Add(child.OriginatingInvoiceId);

        bool everyPartialLineHasItsCreditNoteIssued = allPartialLineTargets.All(targetInvoiceId =>
            targetInvoiceId.HasValue && succeededInvoiceIdsSoFar.Contains(targetInvoiceId.Value));

        // Avanzar el BC T5 por SU circuito: el receivable del operador de ESTE servicio (nunca la reserva
        // entera). AwaitingOperatorRefund es el mismo estado que ya sabe cerrarse solo, sin reembolso
        // pendiente, via el barrido nocturno (BookingCancellationService.CloseZeroReceivableCancellationsAsync)
        // — no hace falta duplicar esa logica aca. NUNCA OnArcaSucceededAsync, NUNCA ND automatica, NUNCA
        // marcar la reserva cancelada (B1).
        if (everyPartialLineHasItsCreditNoteIssued)
        {
            bc.Status = BookingCancellationStatus.AwaitingOperatorRefund;
            bc.OperatorRefundDueBy ??= await ResolveOperatorRefundDueByAsync(db, ct);
        }
        else
        {
            bc.Status = BookingCancellationStatus.Drafted;
        }
        bc.ArcaErrorMessage = null;

        StageAudit(db, auditService, bc, child, resolvedInvoice, succeeded: true, remainingAfterThisNc, logger);

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "metric:t5_partial_credit_note_succeeded | BcPublicId={BcPublicId} TargetInvoiceId={TargetInvoiceId} " +
            "CreditNoteInvoiceId={CreditNoteInvoiceId} RemainingAfter={RemainingAfter}",
            bc.PublicId, child.OriginatingInvoiceId, resolvedInvoice.Id, remainingAfterThisNc);

        return true;
    }

    /// <summary>
    /// Plazo del reembolso del operador (mismo default que el camino legacy, <c>settings.OperatorRefundTimeoutDays</c>).
    /// Lectura directa (sin <c>IOperationalFinanceSettingsService</c>: esta clase es <c>static</c> y no tiene DI).
    /// </summary>
    private static async Task<DateTime> ResolveOperatorRefundDueByAsync(AppDbContext db, CancellationToken ct)
    {
        var timeoutDays = await db.OperationalFinanceSettings
            .AsNoTracking()
            .OrderBy(s => s.Id)
            .Select(s => (int?)s.OperatorRefundTimeoutDays)
            .FirstOrDefaultAsync(ct);
        return DateTime.UtcNow.AddDays(timeoutDays ?? 30);
    }

    private static void StageAudit(
        AppDbContext db,
        IAuditService? auditService,
        BookingCancellation bc,
        BookingCancellationCreditNote child,
        Invoice resolvedInvoice,
        bool succeeded,
        decimal? remainingAfterThisNc,
        ILogger logger)
    {
        var details = System.Text.Json.JsonSerializer.Serialize(new
        {
            bc.PublicId,
            reservaPublicId = bc.Reserva?.PublicId,
            targetInvoiceId = child.OriginatingInvoiceId,
            creditNoteInvoiceId = resolvedInvoice.Id,
            succeeded,
            remainingAfterThisNc,
            targetInvoiceFullyCredited = remainingAfterThisNc == 0m,
        });

        var action = succeeded ? AuditActions.PartialCreditNoteEmitted : AuditActions.PartialCreditNoteEmissionRejected;

        if (auditService is not null)
        {
            // STAGED (no LogBusinessEventAsync, que hace su propio SaveChanges): entra en la MISMA unidad de
            // trabajo que la mutacion, atomico (o commitea todo, o nada) — mismo patron que el resto del modulo.
            auditService.StageBusinessEvent(
                action: action,
                entityName: AuditActions.BookingCancellationEntityName,
                entityId: bc.Id.ToString(),
                details: details,
                userId: bc.ConfirmedByUserId ?? bc.DraftedByUserId,
                userName: bc.ConfirmedByUserName ?? bc.DraftedByUserName);
        }
        else
        {
            // Callback sin audit inyectado (algunos tests legacy construyen AfipService sin el, best-effort).
            logger.LogInformation("AUDIT (fallback logger): {Action} {Details}", action, details);
        }
    }

    private static string TruncateArcaError(string? observaciones)
    {
        string text = string.IsNullOrWhiteSpace(observaciones) ? ArcaRejectedWithoutMessage : observaciones;
        return text.Length > ArcaErrorMaxLength ? text[..ArcaErrorMaxLength] : text;
    }
}
