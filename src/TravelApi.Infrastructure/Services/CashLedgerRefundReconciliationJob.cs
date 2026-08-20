using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
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
    private readonly IUserPermissionResolver _permissionResolver;
    private readonly ILogger<CashLedgerRefundReconciliationJob> _logger;

    // RelatedEntityType del aviso Y prefijo de su clave de resolucion ("CashLedgerRefundReconciliation:{bcId}").
    // Mismo patron que PartialCreditNoteReviewAlertJob: un literal dedicado, para que el dedup de este job nunca
    // choque con avisos de otro tipo sobre la misma cancelacion. Constante COMPARTIDA (no privada del job):
    // NotificationTargetUrlResolver la necesita para reconocer estos avisos al armar el link de la campanita.
    private const string NotificationRelatedType = NotificationRelatedEntityTypes.CashLedgerRefundReconciliation;

    /// <summary>Un destinatario del aviso + si ese usuario puede ver montos de costo (F-14, revision
    /// seguridad 2026-08-19). Ver <see cref="GetSupplierPaymentsAudienceAsync"/>.</summary>
    private sealed record TreasuryAudienceMember(ApplicationUser User, bool CanSeeCost);

    public CashLedgerRefundReconciliationJob(
        AppDbContext db,
        INotificationService notificationService,
        IUserPermissionResolver permissionResolver,
        ILogger<CashLedgerRefundReconciliationJob> logger)
    {
        _db = db;
        _notificationService = notificationService;
        _permissionResolver = permissionResolver;
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
        //    exactamente lo que este job quiere pescar). Delegado a CashLedgerRefundLedgerAmountLoader: fix
        //    2026-08-19 de un bug de origen (2026-08-16) que dejaba esto SIEMPRE en 0 en Postgres real. Ver
        //    el XML-doc de esa clase para el detalle completo del bug y por que era el falso positivo masivo.
        var liveLedgerAmountByRefundId = await CashLedgerRefundLedgerAmountLoader.LoadAsync(
            _db, singleCancellationRefundIds, ct);

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

        List<TreasuryAudienceMember>? treasuryAudience = null;
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

            treasuryAudience ??= await GetSupplierPaymentsAudienceAsync(ct);
            if (treasuryAudience.Count == 0)
            {
                _logger.LogWarning(
                    "CashLedgerRefundReconciliationJob: BC {BookingCancellationId} diverge pero NO hay usuarios con tesoreria.supplier_payments a quien avisar.",
                    bookingCancellationId);
                return;
            }

            var reservaId = reservaIdByBookingCancellationId.GetValueOrDefault(bookingCancellationId);
            var numeroReserva = reservaNumeroById.TryGetValue(reservaId, out var numero) ? numero : null;

            // F-14 (revision seguridad 2026-08-19, B1): el mensaje NO es unico. Un destinatario con
            // tesoreria.supplier_payments pero SIN cobranzas.see_cost es la MISMA situacion que ya
            // enmascara el resto de la cuenta del proveedor (NetAmount/DerivedAmount/LedgerAmount en 0) —
            // el monto de la diferencia tambien es plata de costo, asi que a ese usuario le llega la
            // variante SIN numeros. Se arman las dos variantes una sola vez por cancelacion (no por
            // usuario) porque el texto no depende de QUIEN lo recibe, solo de si puede ver costos o no.
            var messageWithAmounts = BuildUserMessage(numeroReserva, divergences, includeAmounts: true);
            var messageWithoutAmounts = BuildUserMessage(numeroReserva, divergences, includeAmounts: false);

            foreach (var member in treasuryAudience)
            {
                // Dedup (decision 2026-08-19): "vivo" para este job es SOLO ResolvedAt == null — la causa
                // (la divergencia) sigue sin corregirse. Antes tambien exigia !IsRead && !IsDismissed, asi
                // que un aviso que el usuario ya vio/descarto se volvia a crear al dia siguiente si la
                // divergencia seguia viva (el "grita todos los dias" que motivo esta obra). El estado real
                // ahora vive en la ficha del operador (solapa Reembolsos), no en si alguien ya cerro el
                // aviso de la campanita.
                var hasLiveAlert = await _db.Notifications.AnyAsync(n =>
                    n.UserId == member.User.Id
                    && n.ResolutionKey == resolutionKey
                    && n.ResolvedAt == null, ct);

                if (hasLiveAlert)
                    continue;

                await _notificationService.CreateAndSendAsync(new Notification
                {
                    UserId = member.User.Id,
                    Type = "Warning",
                    // Decision 2026-08-19: de "Urgent" a "Normal" — este descalce de UNA reserva puntual ya
                    // no dispara el banner naranja full-width (ese queda reservado para caidas de TODO el
                    // sistema). Sigue siendo un aviso Warning normal en la campanita (punto ambar).
                    Priority = "Normal",
                    RelatedEntityId = bookingCancellationId,
                    RelatedEntityType = NotificationRelatedType,
                    ResolutionKey = resolutionKey,
                    Message = member.CanSeeCost ? messageWithAmounts : messageWithoutAmounts,
                }, ct);
            }
        }
    }

    /// <summary>
    /// Decision 2026-08-19: el aviso va SOLO a quien maneja tesoreria (mismo permiso que la solapa
    /// Reembolsos, <c>tesoreria.supplier_payments</c>).
    ///
    /// <para><b>Fix revision seguridad 2026-08-19 (B1+B2)</b>: la version anterior reproducia a mano el
    /// criterio de <c>PermissionAuthorizationHandler</c> (leer <c>RolePermissions</c> + bypass especial
    /// para el rol Admin), lo que traia dos problemas: (B2) no filtraba <c>ApplicationUser.IsActive</c>,
    /// divergiendo de <see cref="IUserPermissionResolver"/> (que SI deniega a un usuario dado de baja); y
    /// (B1) no habia forma barata de saber, POR DESTINATARIO, si ademas tiene <c>cobranzas.see_cost</c>
    /// para decidir si el mensaje puede llevar el monto de la diferencia (dato de costo, F-14).
    ///
    /// Ahora se resuelve la audiencia usuario-por-usuario con la MISMA fuente de verdad que ya usa el
    /// resto del sistema (<see cref="IUserPermissionResolver.GetPermissionsAsync"/>): un candidato entra si
    /// esa consulta dice que tiene <c>tesoreria.supplier_payments</c> (el resolver ya devuelve vacio para
    /// un usuario inactivo, asi que IsActive queda cubierto sin duplicar el chequeo), y de paso se sabe si
    /// tambien tiene <c>cobranzas.see_cost</c> para elegir la variante del mensaje.</para>
    ///
    /// <para><b>Nota de alcance</b>: al dejar de reproducir el bypass de Admin a mano, un usuario Admin
    /// SOLO entra a esta audiencia si su rol tiene <c>tesoreria.supplier_payments</c> asignado en
    /// <c>RolePermissions</c> (igual que cualquier otro rol) — sigue pudiendo ABRIR la pantalla de
    /// Reembolsos igual (ese bypass vive aparte, en el handler de autorizacion HTTP), pero ya no se le
    /// reproduce artificialmente para esta lista de destinatarios. Es una simplificacion deliberada: un
    /// solo mecanismo de permisos en todo el sistema, sin una segunda copia del bypass en un job.</para>
    /// </summary>
    private async Task<List<TreasuryAudienceMember>> GetSupplierPaymentsAudienceAsync(CancellationToken ct)
    {
        // Candidatos: todo usuario activo. _db.Users (AppDbContext hereda de IdentityDbContext<ApplicationUser>)
        // en vez de UserManager.Users: es el MISMO DbSet, pero soporta ToListAsync de verdad (UserManager.Users
        // es un IQueryable simple sin metodos async propios). El resolver TAMBIEN chequea IsActive puertas
        // adentro, pero filtrar aca de entrada evita resolver permisos de usuarios que ya sabemos dados de baja.
        var candidates = await _db.Users.Where(u => u.IsActive).ToListAsync(ct);

        var audience = new List<TreasuryAudienceMember>();
        foreach (var candidate in candidates)
        {
            var perms = await _permissionResolver.GetPermissionsAsync(candidate.Id, ct);
            if (!perms.Contains(Permissions.TesoreriaSupplierPayments))
                continue;

            audience.Add(new TreasuryAudienceMember(candidate, perms.Contains(Permissions.CobranzasSeeCost)));
        }

        return audience;
    }

    /// <summary>
    /// Mensaje en castellano de negocio, sin jerga tecnica ni IDs crudos (gate de exposicion de datos): le dice
    /// al usuario QUE reserva revisar, no COMO se detecto (nada de "asiento", "ledger" ni "CHECK"). Decision
    /// 2026-08-19: se saca "antes de cerrarla" (sonaba a plazo urgente, contradice el tono bajado de esta
    /// obra).
    ///
    /// <para><b>F-14 (revision seguridad 2026-08-19, B1)</b>: <paramref name="includeAmounts"/> decide si el
    /// mensaje lleva el monto de la diferencia por moneda. El HECHO ("no coincide") es una señal operativa,
    /// no un dato de costo, y puede viajar siempre; el MONTO si es plata de costo, asi que solo va cuando el
    /// destinatario tiene <c>cobranzas.see_cost</c> (mismo criterio que ya enmascara los montos en el DTO de
    /// la solapa Reembolsos). Cada moneda es su PROPIO numero — P-3: nunca se suman monedas distintas.</para>
    /// </summary>
    private static string BuildUserMessage(
        string? numeroReserva, IReadOnlyList<CashLedgerRefundDivergence> divergences, bool includeAmounts)
    {
        if (string.IsNullOrWhiteSpace(numeroReserva))
            return "La devolución del operador de una reserva no coincide con la caja. Revisala cuando puedas.";

        if (!includeAmounts)
            return $"La devolución del operador de la reserva {numeroReserva} no coincide con la caja. Revisala cuando puedas.";

        var diferenciasPorMoneda = divergences
            .Select(dv => $"{dv.Currency} {CurrencyDisplayFormat.Amount(Math.Abs(dv.Delta))}")
            .ToList();
        var diferenciasTexto = JoinWithSpanishAnd(diferenciasPorMoneda);

        return $"La devolución del operador de la reserva {numeroReserva} no coincide con la caja: hay una " +
               $"diferencia de {diferenciasTexto}. Revisala cuando puedas.";
    }

    /// <summary>
    /// Junta una lista de textos al estilo castellano ("A", "A y B", "A, B y C"). Usado para no sumar
    /// montos de monedas distintas (P-3) cuando una misma reserva diverge en 2+ monedas: cada una aparece
    /// como su propio "{moneda} {monto}", nunca como un total unico.
    /// </summary>
    private static string JoinWithSpanishAnd(IReadOnlyList<string> items)
    {
        return items.Count switch
        {
            0 => string.Empty,
            1 => items[0],
            _ => string.Join(", ", items.Take(items.Count - 1)) + " y " + items[^1],
        };
    }
}
