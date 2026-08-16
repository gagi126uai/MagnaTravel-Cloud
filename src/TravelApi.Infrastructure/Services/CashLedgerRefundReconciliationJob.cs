using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>Resultado de una pasada del job, para logging/tests (no se persiste).</summary>
public sealed record CashLedgerRefundReconciliationResult(
    int DivergencesFound,
    int AutoResolved,
    int RefundsSkippedDueToMultiCancellationSplit);

/// <summary>
/// ADR-022 GAP C (2026-08-16): job recurrente que reconcilia el extracto del proveedor (lo que
/// <see cref="TravelApi.Infrastructure.Reservations.SupplierCancellationCircuitReader"/> le muestra al usuario
/// como "Reembolso recibido", derivado de <see cref="BookingCancellationLine.ReceivedRefundAmount"/>) contra el
/// Libro de Caja (los <see cref="CashLedgerEntry"/> VIGENTES que <c>OperatorRefundService</c> escribio al
/// registrar el ingreso del reembolso).
///
/// <para><b>Por que existe</b>: son DOS puertas separadas para el MISMO hecho economico (la plata que entro
/// del operador). En el camino feliz se mantienen sincronizadas SOLAS (las dos se actualizan en la misma
/// transaccion de <c>OperatorRefundService.AllocateAsync</c>/<c>RecordReceivedInternalAsync</c>). Pero el
/// asiento de caja tambien se puede tocar desde Tesoreria (editar/borrar un <c>ManualCashMovement</c>), un
/// camino que NO sabe nada de cancelaciones ni reembolsos — si alguien edita o borra ahi el movimiento del
/// ingreso, la caja deja de reflejar la plata pero la cancelacion sigue mostrandola como "recibida". Este job
/// es la red de seguridad que lo detecta.</para>
///
/// <para><b>Que NO hace (regla dura de ADR-022)</b>: SOLO avisa. Nunca corrige el extracto ni el Libro de Caja
/// por su cuenta — decidir CUAL de los dos numeros esta mal (y por que) es una decision de negocio que le
/// corresponde a una persona, no a un job nocturno.</para>
///
/// <para><b>Limitacion documentada (no un backfill pendiente, una decision de alcance)</b>: un reembolso puede,
/// en teoria, repartirse entre VARIAS cancelaciones distintas (relacion N:M via
/// <see cref="OperatorRefundAllocation"/>). Para no generar avisos falsos por un reparto legitimo (la plata SI
/// esta bien repartida, solo que entre mas de una reserva), esta primera version SOLO evalua los reembolsos
/// cuyas asignaciones vivas apuntan TODAS a la MISMA cancelacion — el caso normal de la operatoria actual. Los
/// repartidos quedan afuera de esta ronda (no se avisa de ellos, tampoco se los marca "ok"): se loguean como
/// omitidos para que quede rastro sin generar ruido al usuario.</para>
///
/// <para><b>Patron</b>: mismo esqueleto que <see cref="PartialCreditNoteReviewAlertJob"/>/
/// <see cref="CoherenceWatchdogJob"/> — service Scoped, registrado como recurring Hangfire en Program.cs, avisa
/// via <see cref="INotificationService"/> con <see cref="Notification.ResolutionKey"/> por cancelacion (para
/// deduplicar y para que el aviso se apague SOLO cuando la divergencia de esa cancelacion se corrija).</para>
/// </summary>
public class CashLedgerRefundReconciliationJob
{
    private readonly AppDbContext _db;
    private readonly INotificationService _notificationService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<CashLedgerRefundReconciliationJob> _logger;

    // RelatedEntityType del aviso Y prefijo de su clave de resolucion ("CashLedgerRefundReconciliation:{bcId}").
    // Mismo patron que PartialCreditNoteReviewAlertJob: un literal dedicado, para que el dedup de este job nunca
    // choque con avisos de otro tipo sobre la misma cancelacion.
    private const string NotificationRelatedType = "CashLedgerRefundReconciliation";

    public CashLedgerRefundReconciliationJob(
        AppDbContext db,
        INotificationService notificationService,
        UserManager<ApplicationUser> userManager,
        ILogger<CashLedgerRefundReconciliationJob> logger)
    {
        _db = db;
        _notificationService = notificationService;
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>
    /// Una pasada del job. Hangfire la invoca via la cron registrada en Program.cs; tambien se puede invocar
    /// manualmente (tests, script de admin).
    /// </summary>
    public async Task<CashLedgerRefundReconciliationResult> RunAsync(CancellationToken ct = default)
    {
        // 1) Unica fuente que sabe "que cancelaciones toco la plata de cada reembolso": las asignaciones VIVAS
        //    (una anulada/voided no cuenta, ni para el extracto ni para este chequeo).
        var liveAllocations = await _db.OperatorRefundAllocations
            .Where(a => !a.IsVoided)
            .Select(a => new { a.OperatorRefundReceivedId, a.BookingCancellationId })
            .ToListAsync(ct);

        if (liveAllocations.Count == 0)
        {
            _logger.LogDebug("CashLedgerRefundReconciliationJob: no hay asignaciones de reembolso vivas, nada para conciliar.");
            return new CashLedgerRefundReconciliationResult(0, 0, 0);
        }

        // 2) Por reembolso, a cuantas cancelaciones DISTINTAS reparte plata viva. Ver la limitacion documentada
        //    en el XML-doc de la clase: solo evaluamos los de UNA sola cancelacion.
        var bookingCancellationIdsByRefund = liveAllocations
            .GroupBy(a => a.OperatorRefundReceivedId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.BookingCancellationId).Distinct().ToList());

        var singleCancellationRefundIds = bookingCancellationIdsByRefund
            .Where(kv => kv.Value.Count == 1)
            .Select(kv => kv.Key)
            .ToList();

        var skippedSplitRefundsCount = bookingCancellationIdsByRefund.Count - singleCancellationRefundIds.Count;
        if (skippedSplitRefundsCount > 0)
        {
            // LogInformation (no Debug, pedido del reviewer 16/08): que se pueda auditar en los
            // logs de produccion cuantos refunds N:M quedan fuera de esta ronda de conciliacion.
            _logger.LogInformation(
                "CashLedgerRefundReconciliationJob: {Count} reembolso(s) repartidos entre varias cancelaciones, fuera de alcance de esta conciliacion.",
                skippedSplitRefundsCount);
        }

        if (singleCancellationRefundIds.Count == 0)
            return new CashLedgerRefundReconciliationResult(0, 0, skippedSplitRefundsCount);

        // 3) Moneda de cada reembolso candidato (la moneda REAL en la que entro la plata).
        var refundCurrencyById = await _db.OperatorRefundReceived
            .Where(r => singleCancellationRefundIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Currency })
            .ToDictionaryAsync(r => r.Id, r => r.Currency, ct);

        // 4) Monto VIGENTE de caja por reembolso (0 si el asiento fue revertido o nunca existio — eso es
        //    exactamente lo que este job quiere pescar).
        var liveLedgerAmountByRefundId = await _db.CashLedgerEntries
            .Where(e => e.SourceType == CashLedgerSourceTypes.OperatorRefund
                     && e.OperatorRefundReceivedId != null
                     && singleCancellationRefundIds.Contains(e.OperatorRefundReceivedId.Value)
                     && !e.IsReversed && !e.IsReversal)
            .GroupBy(e => e.OperatorRefundReceivedId!.Value)
            .Select(g => new { RefundId = g.Key, Amount = g.Sum(e => e.Amount) })
            .ToDictionaryAsync(x => x.RefundId, x => x.Amount, ct);

        // 5) Por cancelacion candidata, total de CAJA por moneda (sumando los reembolsos de un solo destino).
        var ledgerByBookingCancellationId = new Dictionary<int, Dictionary<string, decimal>>();
        foreach (var refundId in singleCancellationRefundIds)
        {
            var bookingCancellationId = bookingCancellationIdsByRefund[refundId][0];
            var currency = refundCurrencyById.TryGetValue(refundId, out var ccy) ? ccy : Monedas.ARS;
            var liveAmount = liveLedgerAmountByRefundId.TryGetValue(refundId, out var amt) ? amt : 0m;

            if (!ledgerByBookingCancellationId.TryGetValue(bookingCancellationId, out var perCurrency))
            {
                perCurrency = new Dictionary<string, decimal>();
                ledgerByBookingCancellationId[bookingCancellationId] = perCurrency;
            }
            perCurrency[currency] = perCurrency.GetValueOrDefault(currency) + liveAmount;
        }

        var candidateBookingCancellationIds = ledgerByBookingCancellationId.Keys.ToList();

        // 6) Total DERIVADO por moneda: exactamente el mismo campo que ya le muestra al usuario
        //    SupplierCancellationCircuitReader ("Reembolso recibido").
        var derivedRows = await _db.BookingCancellationLines
            .Where(l => candidateBookingCancellationIds.Contains(l.BookingCancellationId))
            .Select(l => new { l.BookingCancellationId, l.Currency, l.ReceivedRefundAmount })
            .ToListAsync(ct);

        var derivedByBookingCancellationId = derivedRows
            .GroupBy(r => r.BookingCancellationId)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(r => Monedas.Normalizar(r.Currency))
                      .ToDictionary(cg => cg.Key, cg => cg.Sum(r => r.ReceivedRefundAmount)));

        // 7) Numero de reserva de cada cancelacion candidata, para hablarle al usuario en su idioma (nunca IDs
        //    crudos — gate de exposicion de datos).
        var bookingCancellationInfo = await _db.BookingCancellations
            .Where(bc => candidateBookingCancellationIds.Contains(bc.Id))
            .Select(bc => new { bc.Id, bc.ReservaId })
            .ToListAsync(ct);
        var reservaIdByBookingCancellationId = bookingCancellationInfo.ToDictionary(bc => bc.Id, bc => bc.ReservaId);
        var reservaIds = reservaIdByBookingCancellationId.Values.Distinct().ToList();
        var reservaNumeroById = await _db.Reservas
            .Where(r => reservaIds.Contains(r.Id))
            .Select(r => new { r.Id, r.NumeroReserva })
            .ToDictionaryAsync(r => r.Id, r => r.NumeroReserva, ct);

        List<ApplicationUser>? adminUsers = null;
        var divergenceCount = 0;
        var resolvedCount = 0;

        foreach (var bookingCancellationId in candidateBookingCancellationIds)
        {
            ct.ThrowIfCancellationRequested();

            // Blindaje por item (pedido del reviewer de seguridad 16/08): si UNA cancelacion tiene
            // datos rotos y explota, se loguea y se sigue con las demas — sin esto, un solo registro
            // corrupto dejaba TODA la pasada del dia sin evaluar.
            try
            {
                await ConciliarUnaCancelacionAsync(bookingCancellationId);
            }
            catch (OperationCanceledException)
            {
                throw; // el apagado del worker no se traga
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "CashLedgerRefundReconciliationJob: fallo evaluando BC {BookingCancellationId}; se continua con el resto.",
                    bookingCancellationId);
            }
        }

        return new CashLedgerRefundReconciliationResult(divergenceCount, resolvedCount, skippedSplitRefundsCount);

        async Task ConciliarUnaCancelacionAsync(int bookingCancellationId)
        {
            var derived = derivedByBookingCancellationId.TryGetValue(bookingCancellationId, out var derivedDict)
                ? derivedDict
                : new Dictionary<string, decimal>();
            var ledger = ledgerByBookingCancellationId[bookingCancellationId];

            var divergences = CashLedgerRefundReconciliationCalculator.FindDivergences(derived, ledger);
            var resolutionKey = NotificationResolutionKeys.ForEntity(NotificationRelatedType, bookingCancellationId);

            if (divergences.Count == 0)
            {
                // Ya coincide: si habia un aviso vivo de una divergencia anterior, se apaga solo.
                var resolved = await _notificationService.ResolveByKeyAsync(resolutionKey, ct);
                if (resolved > 0)
                {
                    resolvedCount++;
                    _logger.LogInformation(
                        "CashLedgerRefundReconciliationJob: BC {BookingCancellationId} volvio a coincidir, aviso apagado.",
                        bookingCancellationId);
                }
                return;
            }

            divergenceCount++;

            // Metrica/log tecnico (auditoria operativa) — los IDs y montos crudos quedan ACA, nunca en el
            // mensaje que ve el usuario (armado mas abajo).
            _logger.LogWarning(
                "metric:cash_ledger_refund_reconciliation_divergence | BookingCancellationId={BookingCancellationId} Divergences={Divergences}",
                bookingCancellationId,
                string.Join(
                    "; ",
                    divergences.Select(dv => $"{dv.Currency} derivado={dv.DerivedAmount} caja={dv.LedgerAmount}")));

            adminUsers ??= (await _userManager.GetUsersInRoleAsync("Admin")).ToList();
            if (adminUsers.Count == 0)
            {
                _logger.LogWarning(
                    "CashLedgerRefundReconciliationJob: BC {BookingCancellationId} diverge pero NO hay usuarios Admin a quien avisar.",
                    bookingCancellationId);
                return;
            }

            var reservaId = reservaIdByBookingCancellationId.GetValueOrDefault(bookingCancellationId);
            var numeroReserva = reservaNumeroById.TryGetValue(reservaId, out var numero) ? numero : null;

            var message = BuildUserMessage(numeroReserva);

            foreach (var admin in adminUsers)
            {
                // Dedup: si este admin ya tiene un aviso VIVO de esta misma cancelacion, no se repite (el job
                // corre todos los dias mientras la divergencia siga sin resolverse).
                var hasLiveAlert = await _db.Notifications.AnyAsync(n =>
                    n.UserId == admin.Id
                    && n.ResolutionKey == resolutionKey
                    && n.ResolvedAt == null && !n.IsRead && !n.IsDismissed, ct);

                if (hasLiveAlert)
                    continue;

                await _notificationService.CreateAndSendAsync(new Notification
                {
                    UserId = admin.Id,
                    Type = "Warning",
                    Priority = "Urgent",
                    RelatedEntityId = bookingCancellationId,
                    RelatedEntityType = NotificationRelatedType,
                    ResolutionKey = resolutionKey,
                    Message = message,
                }, ct);
            }
        }
    }

    /// <summary>
    /// Mensaje en castellano de negocio, sin jerga tecnica ni IDs crudos (gate de exposicion de datos): le dice
    /// al admin QUE reserva revisar y QUE mirar, no COMO se detecto (nada de "asiento", "ledger" ni "CHECK").
    /// </summary>
    private static string BuildUserMessage(string? numeroReserva)
    {
        return string.IsNullOrWhiteSpace(numeroReserva)
            ? "La devolución del operador de una reserva no coincide entre lo que figura recibido y lo que hay en la caja. Revisala antes de cerrarla."
            : $"La devolución del operador de la reserva {numeroReserva} no coincide entre lo que figura recibido y lo que hay en la caja. Revisala antes de cerrarla.";
    }
}
