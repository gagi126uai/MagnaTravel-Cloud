using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Constants;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// ADR-044 "Deshacer una multa ya emitida" (2026-07-14): molde exacto de
/// <see cref="CancellationDebitNoteReconciliation"/>, pero para el evento inverso — cuando la Nota de Credito
/// que ANULA una Nota de Debito de multa resuelve en ARCA.
///
/// <para><b>POR QUE EXISTE</b>: la NC-anula-ND se emite de forma asincrona (mismo pipeline
/// <c>InvoiceService.CreateAsync</c> + <c>ProcessInvoiceJob</c> que cualquier comprobante). Cuando consigue CAE,
/// hay que (a) desvincular la ND del BC (el paso vuelve a <c>ConfirmedNoDebitNote</c>, abierto para
/// re-emitir/cerrar), (b) resetear SOLO las lineas cuyos cargos alimentaron esa ND puntual (B2, nunca tocar
/// <c>ManualReview</c> de otro operador) y (c) acuñar el saldo a favor de la porcion ya cobrada (B1). Si ARCA
/// rechaza, la ND original sigue viva tal cual y el BC no se toca.</para>
///
/// <para><b>Corre bajo <c>RunUnderParentLockAsync</c>-equivalente (B3)</b>: como esta clase es <c>static</c> y
/// vive fuera de <c>BookingCancellationService</c> (para evitar una dependencia circular AfipService -&gt;
/// IBookingCancellationService -&gt; IInvoiceService -&gt; IAfipService -&gt; AfipService), replica el MISMO
/// patron de lock (<c>SELECT ... FOR UPDATE</c> + <c>lock_timeout</c> acotado) en vez de reusar el metodo
/// privado. En Postgres SIEMPRE bajo lock; en InMemory (tests unitarios) corre directo, mismo criterio que el
/// resto del modulo.</para>
/// </summary>
public static class DebitNoteAnnulmentReconciliation
{
    /// <summary>Mensaje cuando ARCA rechaza la NC-anula-ND sin devolver texto. Mismo literal historico del modulo.</summary>
    public const string ArcaRejectedWithoutMessage = "ARCA rechazo la Nota de Crédito sin mensaje.";

    /// <summary>La columna <c>ArcaErrorMessage</c> tolera hasta 1000 chars; truncamos a esa cota.</summary>
    public const int ArcaErrorMaxLength = 1000;

    /// <summary>
    /// Busca la(s) fila(s) hija cuyo <c>AnnulmentCreditNoteInvoiceId</c> es <paramref name="resolvedCreditNote"/>
    /// y siguen <c>Pending</c>, y les aplica la reconciliacion. Pensado para llamarse desde
    /// <c>AfipService.ProcessInvoiceJob</c> ni bien la Invoice queda resuelta, para TODA NC (el lookup da 0
    /// filas para cualquier NC que no sea una NC-anula-ND — caso barato, no toca nada).
    /// </summary>
    /// <returns>Cantidad de eventos de deshacer transicionados a un estado terminal (Succeeded o Failed).</returns>
    public static async Task<int> ReconcileFromCreditNoteAsync(
        AppDbContext db,
        Invoice resolvedCreditNote,
        IAuditService? auditService,
        ILogger logger,
        CancellationToken ct)
    {
        var pendingAnnulments = await db.Set<BookingCancellationDebitNoteAnnulment>()
            .Where(a => a.AnnulmentCreditNoteInvoiceId == resolvedCreditNote.Id
                     && a.Status == DebitNoteAnnulmentStatus.Pending)
            .Select(a => a.Id)
            .ToListAsync(ct);

        if (pendingAnnulments.Count == 0)
        {
            return 0;
        }

        int changedCount = 0;
        foreach (var annulmentId in pendingAnnulments)
        {
            if (await ReconcileOneAsync(db, annulmentId, resolvedCreditNote, auditService, logger, ct))
            {
                changedCount++;
            }
        }

        return changedCount;
    }

    /// <summary>
    /// Reconcilia UN evento de deshacer. Determina el resultado de ARCA fuera del lock (lectura barata) y solo
    /// entra al lock si hay algo que escribir (aprobado o rechazado); si la NC sigue "PENDING" en ARCA, no hace
    /// nada (sigue en vuelo).
    /// </summary>
    private static async Task<bool> ReconcileOneAsync(
        AppDbContext db,
        int annulmentId,
        Invoice resolvedCreditNote,
        IAuditService? auditService,
        ILogger logger,
        CancellationToken ct)
    {
        bool arcaApproved =
            string.Equals(resolvedCreditNote.Resultado, "A", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(resolvedCreditNote.CAE);
        bool arcaRejected = string.Equals(resolvedCreditNote.Resultado, "R", StringComparison.OrdinalIgnoreCase);

        if (!arcaApproved && !arcaRejected)
        {
            return false; // "PENDING" o null: sigue en vuelo, no tocamos nada.
        }

        if (!db.Database.IsRelational())
        {
            // InMemory (tests unitarios): no soporta FOR UPDATE ni transacciones. Corremos el cuerpo directo
            // (la serializacion real bajo carrera se valida en integracion Postgres — B3/test 20).
            return await ApplyAsync(db, annulmentId, arcaApproved, resolvedCreditNote, auditService, logger, ct);
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // lock_timeout acotado: si otro worker retiene el lock del padre > 5s, tira excepcion; Hangfire
            // reintenta el job limpio (mismo criterio que RunUnderParentLockAsync de BookingCancellationService).
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'", ct);

            var annulmentForLock = await db.Set<BookingCancellationDebitNoteAnnulment>()
                .AsNoTracking()
                .Where(a => a.Id == annulmentId)
                .Select(a => a.BookingCancellationId)
                .FirstOrDefaultAsync(ct);

            await db.Database.ExecuteSqlRawAsync(
                "SELECT 1 FROM \"BookingCancellations\" WHERE \"Id\" = {0} FOR UPDATE",
                new object[] { annulmentForLock }, ct);

            var result = await ApplyAsync(db, annulmentId, arcaApproved, resolvedCreditNote, auditService, logger, ct);

            await tx.CommitAsync(ct);
            return result;
        });
    }

    /// <summary>
    /// Cuerpo de la reconciliacion, YA bajo lock (o en InMemory, directo). Re-lee la fila hija fresca (anti
    /// carrera: otro worker pudo haber ganado la carrera y ya haberla resuelto) y aplica el efecto.
    /// </summary>
    private static async Task<bool> ApplyAsync(
        AppDbContext db,
        int annulmentId,
        bool arcaApproved,
        Invoice resolvedCreditNote,
        IAuditService? auditService,
        ILogger logger,
        CancellationToken ct)
    {
        var annulment = await db.Set<BookingCancellationDebitNoteAnnulment>()
            .FirstOrDefaultAsync(a => a.Id == annulmentId, ct);

        // Re-chequeo anti carrera: si otro worker (o una corrida anterior de este mismo job, reintento de
        // Hangfire) ya la resolvio, no hacemos nada de nuevo.
        if (annulment is null || annulment.Status != DebitNoteAnnulmentStatus.Pending)
        {
            return false;
        }

        if (!arcaApproved)
        {
            annulment.Status = DebitNoteAnnulmentStatus.Failed;
            annulment.ArcaErrorMessage = TruncateArcaError(resolvedCreditNote.Observaciones);
            // La ND original NO se toca: sigue Issued, tal cual estaba. El usuario puede reintentar deshacer.
            await db.SaveChangesAsync(ct);

            logger.LogWarning(
                "Deshacer multa RECHAZADO por ARCA. AnnulmentId={AnnulmentId} CreditNoteInvoiceId={CreditNoteInvoiceId}",
                annulmentId, resolvedCreditNote.Id);
            return true;
        }

        annulment.Status = DebitNoteAnnulmentStatus.Succeeded;
        annulment.ArcaErrorMessage = null;

        var bc = await db.BookingCancellations
            .FirstOrDefaultAsync(b => b.Id == annulment.BookingCancellationId, ct);

        if (bc is null)
        {
            // Defensivo (no deberia pasar: FK Cascade). Sin BC no hay nada mas que hacer aca; la NC igual
            // consiguio CAE y la fila hija queda marcada Succeeded (auditable).
            await db.SaveChangesAsync(ct);
            logger.LogError(
                "Deshacer multa: BC {BookingCancellationId} no encontrado al reconciliar el evento {AnnulmentId}.",
                annulment.BookingCancellationId, annulmentId);
            return true;
        }

        var undoneDebitNoteInvoiceId = annulment.AnnulledDebitNoteInvoiceId;

        // M3 (Rev 2): NO se toca AnnulmentStatus de la ND. "¿ya fue deshecha?" se responde por ESTA fila hija
        // (Status=Succeeded) o por la NC asociada — nunca por Invoice.AnnulmentStatus (ver el XML-doc de la
        // entidad hija). Solo desvinculamos si el BC sigue apuntando a la MISMA ND que se anulo (defensivo:
        // si algo mas ya cambio el puntero, no lo pisamos).
        bool debitNoteStillLinked = bc.DebitNoteInvoiceId == undoneDebitNoteInvoiceId;

        // CORNER "Succeeded sin mint" (ADR-044, corrección post-gate 2026-07-14): el BC ya NO apunta a la ND que
        // se estaba anulando (otro flujo la re-apuntó entre la solicitud y este callback). La NC igual consiguió
        // CAE (el hecho fiscal es real: la ND quedó anulada), así que la fila hija se marca Succeeded — pero NO
        // desvinculamos ni acuñamos, para no pisar el estado nuevo. Ese salto NO puede quedar silencioso (podría
        // esconder un descuadre de plata): LogError + auditoría dedicada para que un humano lo revise.
        if (!debitNoteStillLinked)
        {
            logger.LogError(
                "metric:debit_note_undo_needs_review | AnnulmentId={AnnulmentId} BcId={BcId} " +
                "UndoneDebitNoteInvoiceId={NdId} CurrentDebitNoteInvoiceId={CurrentNdId} | La NC consiguió CAE " +
                "pero el BC ya no apunta a la ND que se estaba anulando (re-apuntada en carrera). Se marcó el " +
                "evento Succeeded SIN desvincular ni acuñar saldo a favor: requiere revisión manual.",
                annulment.Id, bc.Id, undoneDebitNoteInvoiceId, bc.DebitNoteInvoiceId);

            await StageUndoNeedsReviewAuditAsync(db, auditService, bc, annulment, logger);
            await db.SaveChangesAsync(ct);
            return true;
        }

        // Camino normal: el BC sigue apuntando a la ND anulada -> desvincular (el paso vuelve a abierto).
        bc.DebitNoteInvoiceId = null;
        bc.DebitNoteStatus = DebitNoteStatus.NotApplicable;
        bc.DebitNoteArcaErrorMessage = null;

        // B2 (Rev 2): reset ACOTADO de line.DebitNoteStatus a las lineas que estuvieron EN esta ND.
        await ResetLinesFedByDebitNoteAsync(db, bc.Id, undoneDebitNoteInvoiceId, ct);

        // B1 (Rev 2 + fix bloqueante seguridad 2026-07-14): acuñar la porción YA COBRADA de la multa como saldo
        // a favor. Se usa la regla PURA OperatorPenaltyUndoRules.ComputeCollectedPenalty (NO ComputePendingPenaltyForDisplay,
        // que clampea a 0 el pendiente cuando el saldo es <=0 y por eso acuñaría el bruto entero como crédito
        // FANTASMA en una reserva anulada saldada). En el producto de hoy no hay camino para cobrar la multa de
        // una anulada, así que esto acuña 0 en la práctica (ver el XML-doc de OperatorPenaltyUndoRules).
        ClientCreditEntry? mintedCredit = null;
        if (bc.PenaltyAmountAtEvent.HasValue)
        {
            var penaltyCurrencyIso = ProjectPenaltyCurrencyToIsoOrNull(bc.PenaltyCurrencyAtEvent) ?? annulment.Currency;
            var penaltyCurrencyBalance = await LoadPenaltyCurrencyBalanceAsync(db, bc.ReservaId, penaltyCurrencyIso, ct);
            var collectedPenaltyPortion = OperatorPenaltyUndoRules.ComputeCollectedPenalty(
                bc.PenaltyAmountAtEvent.Value, penaltyCurrencyBalance);

            mintedCredit = await ClientCreditService.CreateEntryFromDebitNoteUndoAsync(
                db,
                annulmentId: annulment.Id,
                reservaId: bc.ReservaId,
                customerId: bc.CustomerId,
                collectedPenaltyPortion: collectedPenaltyPortion,
                currency: penaltyCurrencyIso,
                actorUserId: annulment.RequestedByUserId,
                actorUserName: annulment.RequestedByUserName,
                logger: logger,
                cancellationToken: ct);
        }

        await StageAuditAsync(db, auditService, bc, annulment, mintedCredit, logger, ct);

        await db.SaveChangesAsync(ct);

        if (mintedCredit is not null)
        {
            // Recalcular el saldo por moneda de la reserva: el puente negativo de CreateEntryFromDebitNoteUndoAsync
            // ya esta persistido, ahora hay que reflejarlo en ReservaMoneyByCurrency (mismo contrato que
            // OverpaymentCreditConverter / CancellationToClientCreditConverter).
            await ReservaMoneyPersister.PersistAsync(db, bc.ReservaId, ct);
        }

        logger.LogInformation(
            "metric:debit_note_undone | AnnulmentId={AnnulmentId} BcId={BcId} UndoneDebitNoteInvoiceId={NdId} " +
            "AnnulmentCreditNoteInvoiceId={NcId} MintedCredit={MintedCredit}",
            annulmentId, bc.Id, undoneDebitNoteInvoiceId, resolvedCreditNote.Id, mintedCredit is not null);

        return true;
    }

    /// <summary>
    /// B2 (Rev 2): resetea <c>line.DebitNoteStatus</c> SOLO en las lineas de este BC que estuvieron EN la ND
    /// que se acaba de anular, para que puedan volver a emitir una ND complementaria si hace falta. JAMAS toca
    /// una linea en <see cref="DebitNoteStatus.ManualReview"/> (es la ND complementaria de OTRO operador, ajena
    /// a esta ND — resetearla borraria una multa pendiente que nunca se cobraria, ver el XML-doc de la entidad
    /// <c>BookingCancellationLineOperatorCharge.TargetInvoiceId</c>).
    /// </summary>
    private static async Task ResetLinesFedByDebitNoteAsync(
        AppDbContext db, int bookingCancellationId, int undoneDebitNoteInvoiceId, CancellationToken ct)
    {
        // T3b: lineas cuyos cargos trasladables (Kind != Withholding) apuntan a la ND anulada via TargetInvoiceId.
        var lineIdsFromCharges = await db.BookingCancellationLineOperatorCharges
            .Where(c => c.BookingCancellationLine.BookingCancellationId == bookingCancellationId
                     && c.Kind != OperatorChargeKind.Withholding
                     && c.TargetInvoiceId == undoneDebitNoteInvoiceId)
            .Select(c => c.BookingCancellationLineId)
            .Distinct()
            .ToListAsync(ct);

        List<BookingCancellationLine> linesToReset;
        if (lineIdsFromCharges.Count > 0)
        {
            linesToReset = await db.BookingCancellationLines
                .Where(l => lineIdsFromCharges.Contains(l.Id) && l.DebitNoteStatus != DebitNoteStatus.ManualReview)
                .ToListAsync(ct);
        }
        else
        {
            // T3a legado (sin TargetInvoiceId poblado, ND unica del BC): las lineas CONFIRMADAS del BC que NO
            // esten en ManualReview. JAMAS a ciegas: ManualReview queda siempre afuera.
            linesToReset = await db.BookingCancellationLines
                .Where(l => l.BookingCancellationId == bookingCancellationId
                         && l.PenaltyStatus == PenaltyStatus.Confirmed
                         && l.DebitNoteStatus != DebitNoteStatus.ManualReview)
                .ToListAsync(ct);
        }

        foreach (var line in linesToReset)
        {
            line.DebitNoteStatus = DebitNoteStatus.NotApplicable;
            line.DebitNoteArcaErrorMessage = null;
        }
    }

    /// <summary>
    /// Saldo de la reserva EN LA MONEDA de la multa (convención: positivo = multa aún por cobrar). Devuelve 0 si
    /// no hay fila para esa moneda (reserva sin movimientos en esa moneda = nada por cobrar en ella). Es el único
    /// dato que necesita <see cref="OperatorPenaltyUndoRules.ComputeCollectedPenalty"/>.
    /// </summary>
    private static async Task<decimal> LoadPenaltyCurrencyBalanceAsync(
        AppDbContext db, int reservaId, string penaltyCurrencyIso, CancellationToken ct)
    {
        var row = await db.ReservaMoneyByCurrency
            .AsNoTracking()
            .Where(m => m.ReservaId == reservaId && m.Currency == penaltyCurrencyIso)
            .Select(m => (decimal?)m.Balance)
            .FirstOrDefaultAsync(ct);
        return row ?? 0m;
    }

    /// <summary>
    /// CORNER "Succeeded sin mint" (ADR-044): auditoría del salto cuando la NC consiguió CAE pero el BC ya no
    /// apunta a la ND anulada (re-apuntada en carrera). Deja rastro para revisión manual sin perder plata en
    /// silencio. STAGED (misma unidad de trabajo que el Save del caller).
    /// </summary>
    private static async Task StageUndoNeedsReviewAuditAsync(
        AppDbContext db,
        IAuditService? auditService,
        BookingCancellation bc,
        BookingCancellationDebitNoteAnnulment annulment,
        ILogger logger)
    {
        var details = System.Text.Json.JsonSerializer.Serialize(new
        {
            bc.PublicId,
            annulmentPublicId = annulment.PublicId,
            action = "operator-penalty-debit-note-undo-needs-review",
            reason = annulment.Reason,
            undoneDebitNoteInvoiceId = annulment.AnnulledDebitNoteInvoiceId,
            currentDebitNoteInvoiceId = bc.DebitNoteInvoiceId,
            annulmentCreditNoteInvoiceId = annulment.AnnulmentCreditNoteInvoiceId,
            note = "NC con CAE pero el BC ya no apunta a la ND anulada: no se desvinculó ni acuñó. Revisar a mano.",
        });

        if (auditService is not null)
        {
            auditService.StageBusinessEvent(
                action: AuditActions.OperatorPenaltyDebitNoteUndoNeedsReview,
                entityName: AuditActions.BookingCancellationEntityName,
                entityId: bc.Id.ToString(),
                details: details,
                userId: annulment.RequestedByUserId,
                userName: annulment.RequestedByUserName);
        }
        else
        {
            logger.LogError("AUDIT (fallback logger): {Action} {Details}",
                AuditActions.OperatorPenaltyDebitNoteUndoNeedsReview, details);
        }

        await Task.CompletedTask;
    }

    /// <summary>Espejo minimo de <c>BookingCancellationService.ProjectPenaltyCurrencyToIsoOrNull</c> (moneda ARCA -&gt; ISO).</summary>
    private static string? ProjectPenaltyCurrencyToIsoOrNull(string? penaltyCurrencyAtEvent)
    {
        if (string.IsNullOrWhiteSpace(penaltyCurrencyAtEvent)) return null;
        return TravelApi.Domain.Helpers.ArcaCurrencyMapper.ToIso(penaltyCurrencyAtEvent)
               ?? Monedas.Normalizar(penaltyCurrencyAtEvent);
    }

    private static async Task StageAuditAsync(
        AppDbContext db,
        IAuditService? auditService,
        BookingCancellation bc,
        BookingCancellationDebitNoteAnnulment annulment,
        ClientCreditEntry? mintedCredit,
        ILogger logger,
        CancellationToken ct)
    {
        var details = System.Text.Json.JsonSerializer.Serialize(new
        {
            bc.PublicId,
            annulmentPublicId = annulment.PublicId,
            action = "operator-penalty-debit-note-undone",
            reason = annulment.Reason,
            undoneDebitNoteInvoiceId = annulment.AnnulledDebitNoteInvoiceId,
            annulmentCreditNoteInvoiceId = annulment.AnnulmentCreditNoteInvoiceId,
            amount = annulment.Amount,
            currency = annulment.Currency,
            mintedCreditAmount = mintedCredit?.CreditedAmount,
            mintedCreditPublicId = mintedCredit?.PublicId,
        });

        if (auditService is not null)
        {
            // STAGED (no LogBusinessEventAsync, que hace su propio SaveChanges): entra en la MISMA unidad de
            // trabajo que la mutacion, atomico (o commitea todo, o nada).
            auditService.StageBusinessEvent(
                action: AuditActions.OperatorPenaltyDebitNoteUndone,
                entityName: AuditActions.BookingCancellationEntityName,
                entityId: bc.Id.ToString(),
                details: details,
                userId: annulment.RequestedByUserId,
                userName: annulment.RequestedByUserName);
        }
        else
        {
            // Callback sin audit inyectado (algunos tests legacy construyen AfipService sin el, best-effort).
            logger.LogInformation("AUDIT (fallback logger): {Action} {Details}",
                AuditActions.OperatorPenaltyDebitNoteUndone, details);
        }

        await Task.CompletedTask;
    }

    private static string TruncateArcaError(string? observaciones)
    {
        string text = string.IsNullOrWhiteSpace(observaciones) ? ArcaRejectedWithoutMessage : observaciones;
        return text.Length > ArcaErrorMaxLength ? text[..ArcaErrorMaxLength] : text;
    }
}
