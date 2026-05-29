using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Application.Exceptions;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// FC1.2.3 v3 §2.3 + §6.4 (2026-05-18): gestiona el saldo a favor del cliente
/// (<see cref="ClientCreditEntry"/>) y los retiros (<see cref="ClientCreditWithdrawal"/>).
/// Modela T3 del flujo de cancelacion/refund.
///
/// <para>
/// <b>Patron de transacciones</b>:
/// <list type="bullet">
///   <item><see cref="CreateEntryAsync"/> NO hace SaveChanges — es invocado por
///         <c>OperatorRefundService.AllocateAsync</c> dentro de su tx envolvente
///         (HC1 plan v3).</item>
///   <item><see cref="WithdrawAsync"/> SI hace SaveChanges — es el caller publico
///         del flujo, no esta dentro de otra tx envolvente.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Cierre del BC en cascada</b>: cuando el ultimo withdraw deja el BC con
/// TODOS los entries con RemainingBalance=0, este service llama a
/// <c>IBookingCancellationService.OnAllCreditConsumedAsync</c> para que el BC
/// pase a <c>Closed</c> y la Reserva a <c>Cancelled</c>. El callback NO commitea
/// — el SaveChanges final del WithdrawAsync atomiza todo.
/// </para>
///
/// <para>
/// <b>Concurrencia entre retiros paralelos</b> (MR-02 plan v3): el CHECK SQL
/// <c>chk_ClientCreditEntries_remaining_non_negative</c> garantiza que dos
/// withdraws simultaneos no puedan dejar el saldo negativo. Si ambos pasan la
/// validacion en memoria pero al persistir colisionan, Postgres rechaza el
/// segundo con 23514 -> <see cref="BusinessInvariantViolationException"/>
/// INV-085 via interceptor. <see cref="OnAllCreditConsumedAsync"/> reverifica
/// con SQL crudo si quedan entries con saldo &gt; 0 antes de cerrar el BC, para
/// que el cierre sea idempotente bajo concurrencia.
/// </para>
/// </summary>
public class ClientCreditService : IClientCreditService
{
    private readonly AppDbContext _db;
    private readonly IBookingCancellationService _bcService;
    private readonly IApprovalRequestService _approvalService;
    private readonly IAuditService _auditService;
    private readonly IOperationalFinanceSettingsService _settings;
    private readonly ILogger<ClientCreditService> _logger;

    public ClientCreditService(
        AppDbContext db,
        IBookingCancellationService bcService,
        IApprovalRequestService approvalService,
        IAuditService auditService,
        IOperationalFinanceSettingsService settings,
        ILogger<ClientCreditService> logger)
    {
        _db = db;
        _bcService = bcService;
        _approvalService = approvalService;
        _auditService = auditService;
        _settings = settings;
        _logger = logger;
    }

    // =========================================================================
    // CreateEntryAsync (sin cambios FC1.2.2 — solo Add() en tx envolvente).
    // =========================================================================

    /// <summary>
    /// Crea un <see cref="ClientCreditEntry"/> con saldo inicial igual al
    /// <paramref name="netAmount"/> del allocation que lo origino. NO hace
    /// <c>SaveChangesAsync</c> — el caller (OperatorRefundService) commitea
    /// la transaccion envolvente.
    /// </summary>
    public Task<ClientCreditEntry> CreateEntryAsync(
        int bookingCancellationId,
        OperatorRefundAllocation operatorRefundAllocation,
        int customerId,
        decimal netAmount,
        string currency,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        if (operatorRefundAllocation is null)
        {
            throw new ArgumentNullException(nameof(operatorRefundAllocation));
        }

        if (netAmount <= 0m)
        {
            throw new ArgumentException(
                "ClientCreditEntry no se puede crear con netAmount <= 0.",
                nameof(netAmount));
        }

        var entry = new ClientCreditEntry
        {
            BookingCancellationId = bookingCancellationId,
            // Pasamos la navigation property (no el Id escalar) porque al ser
            // invocados dentro de la misma tx envolvente del caller, la
            // allocation puede tener Id=0 todavia. EF resuelve la FK al hacer
            // SaveChanges en orden topologico.
            Allocation = operatorRefundAllocation,
            CustomerId = customerId,
            CreditedAmount = netAmount,
            RemainingBalance = netAmount,
            CreatedAt = DateTime.UtcNow,
            IsFullyConsumed = false,
        };
        _db.ClientCreditEntries.Add(entry);

        _logger.LogDebug(
            "ClientCreditEntry pendiente Add para BcId={BcId} AllocationPublicId={AllocationPublicId} Customer={CustomerId} NetAmount={NetAmount} {Currency}.",
            bookingCancellationId, operatorRefundAllocation.PublicId, customerId, netAmount, currency);

        return Task.FromResult(entry);
    }

    // =========================================================================
    // WithdrawAsync (T3 del flujo — entrada publica del controller)
    // =========================================================================

    /// <summary>
    /// FC1.2.3 v3 §6.4 (2026-05-18): retira saldo del entry segun el kind.
    /// Despacha a un handler privado por kind para que la logica especifica
    /// (Ley 25.345, approval, ManualCashMovement) quede separada del flujo
    /// comun (validar entry, decrementar saldo, audit, cerrar BC si corresponde).
    /// </summary>
    public async Task<ClientCreditWithdrawalDto> WithdrawAsync(
        Guid entryPublicId,
        WithdrawClientCreditRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        // ATENCION trainee/junior — orden de operaciones (Obs 4 review FC1.2.3, 2026-05-18):
        //
        // El audit del withdrawal va DESPUES del SaveChanges del withdrawal+entry,
        // NO antes. Razon: AuditService.LogBusinessEventAsync internamente hace
        // su propio SaveChanges. Si lo logueamos antes y el SaveChanges del
        // withdrawal/entry falla (ej. CHECK chk_remaining_non_negative por
        // concurrencia, FK rota, etc.), nos queda una fila de audit en BD
        // SIN withdrawal correspondiente. Eso es veneno para auditoria fiscal:
        // el reporte diario muestra "se retiraron $X" pero no hay registro del
        // retiro.
        //
        // Patron seguro: persistir primero el efecto (withdrawal + movement +
        // decremento saldo) y SOLO si SaveChanges salio bien, loguear el audit.
        // Si el audit a su vez falla, peor caso: tenemos withdrawal sin audit
        // (recuperable manualmente con ChangeLogs/logs). El caso contrario
        // (audit sin withdrawal) es irreversible y rompe la consistencia fiscal.

        // 0) Feature flag: si el modulo no esta habilitado, ningun service del
        //    modulo deberia operar. Centralizamos la verificacion en cada
        //    metodo publico para evitar bypass via tests o backdoors.
        await EnsureFeatureFlagOnAsync(ct);

        // 1) Cargar el entry con su BC + customer + withdrawals existentes.
        //    BC + Reserva las usa OnAllCreditConsumedAsync para evaluar cierre;
        //    Withdrawals existentes los usa el daily egress report.
        //
        //    Por que NO AsNoTracking: vamos a modificar entry.RemainingBalance
        //    y necesitamos que EF lo trackee + envie el UPDATE en el SaveChanges.
        var entry = await _db.ClientCreditEntries
            .Include(e => e.BookingCancellation).ThenInclude(b => b.Reserva)
            .Include(e => e.Customer)
            .Include(e => e.Withdrawals)
            .FirstOrDefaultAsync(e => e.PublicId == entryPublicId, ct)
            ?? throw new KeyNotFoundException(
                $"ClientCreditEntry {entryPublicId} no encontrado.");

        // 2) Validacion comun pre-kind: amount > 0 (excepto KeptAsCredit que es 0)
        //    + amount <= RemainingBalance.
        ValidateAmountCommon(request, entry);

        // 3) Despacho por kind. Cada handler:
        //    - Valida invariantes especificos del kind (Ley 25.345, approval).
        //    - Crea el ClientCreditWithdrawal (Add() en memoria).
        //    - Crea el ManualCashMovement asociado si corresponde (Add() en memoria).
        //    - Para ReversedToOperator: NO loguea audit reforzado todavia (se
        //      reordeno post-SaveChanges, ver paso 6b).
        //    Retorna el withdrawal armado (todavia con Id=0 — SaveChanges al final).
        var withdrawal = request.Kind switch
        {
            WithdrawalKind.KeptAsCredit => await HandleKeptAsCreditAsync(entry, request, userId, userName, ct),
            WithdrawalKind.PhysicalCash => await HandlePhysicalCashAsync(entry, request, userId, userName, ct),
            WithdrawalKind.Transfer => await HandleTransferAsync(entry, request, userId, userName, ct),
            WithdrawalKind.AppliedToNewBooking => await HandleAppliedToNewBookingAsync(entry, request, userId, userName, ct),
            WithdrawalKind.ReversedToOperator => await HandleReversedToOperatorAsync(entry, request, userId, userName, ct),
            _ => throw new ArgumentException($"WithdrawalKind no soportado: {request.Kind}", nameof(request)),
        };

        // 4) Decrementar el saldo (excepto KeptAsCredit que NO consume).
        //    Regla 5 policy del ADR-002: KeptAsCredit deja huella de la decision
        //    pero no toca el saldo. El cliente puede retirar mas adelante.
        if (request.Kind != WithdrawalKind.KeptAsCredit)
        {
            entry.RemainingBalance = ReservationEconomicPolicy.RoundCurrency(
                entry.RemainingBalance - request.Amount);

            // Si llegamos a saldo cero exacto, marcamos consumido. El BC se
            // cierra solo si TODOS los entries del BC estan en este estado
            // (eval en OnAllCreditConsumedAsync — un BC puede tener N entries
            // por N retiros del operador).
            if (entry.RemainingBalance == 0m)
            {
                entry.IsFullyConsumed = true;
            }
        }

        // 5) PRIMER SaveChanges: persiste withdrawal + (opcional) ManualCashMovement
        //    + decremento saldo del entry. Si el CHECK SQL del saldo o cualquier
        //    constraint falla, throws aca SIN haber emitido audit. La fila de
        //    audit fiscal solo se emite si este SaveChanges salio bien.
        //
        //    EF resuelve aca el orden topologico: ClientCreditWithdrawal primero
        //    (obtiene Id real de la secuencia Postgres), luego ManualCashMovement
        //    con la FK ya valida (gracias al fix del builder en Obs 2).
        await _db.SaveChangesAsync(ct);

        // 6) Audit base ClientCreditWithdrawn (todos los kinds, ya con withdrawal
        //    persistido). withdrawal.PublicId es estable desde el momento del
        //    new (lo genera la BD via DEFAULT gen_random_uuid()? No — lo asigna
        //    el modelo en construccion). Lo logueamos solo ahora para que el
        //    audit no pueda quedar huerfano si el SaveChanges hubiera fallado.
        await _auditService.LogBusinessEventAsync(
            action: AuditActions.ClientCreditWithdrawn,
            entityName: AuditActions.ClientCreditWithdrawalEntityName,
            entityId: withdrawal.PublicId.ToString(),
            details: JsonSerializer.Serialize(new
            {
                withdrawalPublicId = withdrawal.PublicId,
                entryPublicId = entry.PublicId,
                bcPublicId = entry.BookingCancellation.PublicId,
                customerPublicId = entry.Customer.PublicId,
                kind = withdrawal.Kind.ToString(),
                withdrawal.Amount,
                remainingBalanceAfter = entry.RemainingBalance,
                approvalRequestId = withdrawal.ApprovalRequestId,
            }),
            userId: userId,
            userName: userName,
            ct: ct);

        // 6b) Audit reforzado para ReversedToOperator (movido post-SaveChanges
        //     desde HandleReversedToOperatorAsync — Obs 4). Se loguea SOLO si
        //     el SaveChanges del paso 5 salio bien, garantizando que no quede
        //     audit de reversal huerfano si el withdrawal no se persistio.
        if (withdrawal.Kind == WithdrawalKind.ReversedToOperator)
        {
            await _auditService.LogBusinessEventAsync(
                action: AuditActions.ClientRefundReversalApproved,
                entityName: AuditActions.ClientCreditEntryEntityName,
                entityId: entry.PublicId.ToString(),
                details: JsonSerializer.Serialize(new
                {
                    entryPublicId = entry.PublicId,
                    bcPublicId = entry.BookingCancellation.PublicId,
                    customerPublicId = entry.Customer.PublicId,
                    withdrawalPublicId = withdrawal.PublicId,
                    withdrawal.Amount,
                    approvalRequestPublicId = withdrawal.ApprovalRequestId,
                    executedByUserId = userId,
                }),
                userId: userId,
                userName: userName,
                ct: ct);
        }

        // FC1.2.7b counter: el cliente retiro saldo. Pasamos `kind` como property
        // estructurada para que el dashboard pueda filtrar por tipo de retiro
        // (PhysicalCash vs Transfer vs ReversedToOperator) y armar series por
        // kind. Si physical_cash crece desproporcionado, hay problema operativo
        // (la caja deberia preferir transfer para evitar manejo de efectivo).
        _logger.LogInformation(
            "metric:client_credit_withdrawn | WithdrawalPublicId={WithdrawalPublicId} EntryPublicId={EntryPublicId} Kind={Kind} Amount={Amount}",
            withdrawal.PublicId, entry.PublicId, withdrawal.Kind.ToString(), withdrawal.Amount);

        // 7) Cierre del BC si TODOS los entries estan consumidos.
        //    MR-02 plan v3: el callback evalua con SQL crudo + ChangeTracker
        //    (porque los cambios in-memory de este metodo todavia no estan en BD)
        //    si quedan entries con saldo > 0 en el BC. Si no quedan, transiciona
        //    el BC a Closed y la Reserva a Cancelled. NO hace SaveChanges (HC1):
        //    el SaveChanges del paso 8 commitea bc.Status + Reserva.Status +
        //    audit BookingCancellationClosed (este ultimo lo persistio
        //    AuditService internamente).
        //
        //    NOTA: ahora que ya hicimos SaveChanges en paso 5, la query SQL del
        //    callback YA VE las modificaciones de este flujo (RemainingBalance=0).
        //    El ChangeTracker quedo limpio para esos cambios; solo veria
        //    bc.Status si el callback lo modifica.
        if (request.Kind != WithdrawalKind.KeptAsCredit && entry.IsFullyConsumed)
        {
            await _bcService.OnAllCreditConsumedAsync(entry.BookingCancellationId, ct);

            // 8) SEGUNDO SaveChanges: persiste cambios del callback
            //    (bc.Status=Closed, bc.ClosedAt, bc.Reserva.Status=Cancelled).
            //    Solo si el callback efectivamente modifico algo (puede haber
            //    sido no-op por idempotencia bajo concurrencia, en cuyo caso
            //    el SaveChanges queda como no-op tambien — EF detecta 0 cambios).
            await _db.SaveChangesAsync(ct);
        }

        return MapWithdrawal(withdrawal, entry.PublicId);
    }

    // =========================================================================
    // Handlers por kind
    // =========================================================================

    /// <summary>
    /// Kind 1: <c>KeptAsCredit</c>. El cliente decide dejar el saldo a su favor.
    /// NO consume el saldo (regla 5 policy ADR-002). Generamos un withdrawal
    /// con Amount=0 como "marca de decision" para que el timeline del cliente
    /// muestre el evento ("el 12/05 decidio dejar $X como credito").
    ///
    /// <para>
    /// <b>Decision modelado (opcion A del brief)</b>: SI generamos withdrawal
    /// con <c>Kind=KeptAsCredit</c> y <c>Amount=0</c>. La opcion B (NO generar
    /// withdrawal, solo flag en el entry) la descartamos porque:
    /// <list type="bullet">
    ///   <item>El cliente puede tomar la decision varias veces sobre el mismo
    ///         entry (ej. "deja como credito" -> "no, retiro" -> "no, deja"),
    ///         y un timeline 1:N modela eso mas claro que un flag mutable.</item>
    ///   <item>El audit log con entityType=ClientCreditWithdrawal queda
    ///         consistente con los otros kinds (mismo entityType para queries).</item>
    /// </list>
    /// </para>
    /// </summary>
    private Task<ClientCreditWithdrawal> HandleKeptAsCreditAsync(
        ClientCreditEntry entry,
        WithdrawClientCreditRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        // Amount debe ser 0 para KeptAsCredit (no consume saldo). Si el caller
        // mando > 0, fallamos: probablemente confundio kind con monto.
        if (request.Amount != 0m)
        {
            throw new ArgumentException(
                "KeptAsCredit requiere Amount=0 (no consume saldo). " +
                "Si queres retirar plata, usar PhysicalCash o Transfer.",
                nameof(request));
        }

        var withdrawal = new ClientCreditWithdrawal
        {
            ClientCreditEntryId = entry.Id,
            Entry = entry,
            Kind = WithdrawalKind.KeptAsCredit,
            Amount = 0m,
            ExecutedAt = DateTime.UtcNow,
            ExecutedByUserId = userId,
            ExecutedByUserName = userName ?? string.Empty,
            // KeptAsCredit no genera ManualCashMovement (no hay flujo fisico).
            ManualCashMovementId = null,
            ApprovalRequestId = null,
        };
        _db.ClientCreditWithdrawals.Add(withdrawal);

        return Task.FromResult(withdrawal);
    }

    /// <summary>
    /// Kind 2: <c>PhysicalCash</c>. Cliente retira efectivo.
    ///
    /// <para>
    /// <b>Reglas</b>:
    /// <list type="bullet">
    ///   <item>INV-094 Ley 25.345: si Amount supera el threshold configurado,
    ///         rechazar. La ley argentina prohibe pagos en efectivo arriba de
    ///         X ARS — la agencia es solidariamente responsable si los pasamos.</item>
    ///   <item>Si Amount supera <c>PhysicalRefundAlertThreshold</c> pero no
    ///         llega al threshold de la ley, loguear alerta admin (no bloquea).</item>
    /// </list>
    /// </para>
    /// </summary>
    private async Task<ClientCreditWithdrawal> HandlePhysicalCashAsync(
        ClientCreditEntry entry,
        WithdrawClientCreditRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        var settings = await _settings.GetEntityAsync(ct);

        // INV-094 Ley 25.345 — bloqueo duro. El tope sale de settings (configurable por
        // admin) porque el monto del texto original de la ley esta congelado: el umbral
        // operativo real lo fija ARCA por resolucion (auditoria fiscal 2026-05-29).
        if (request.Amount > settings.Ley25345ThresholdAmount)
        {
            throw new BusinessInvariantViolationException(
                $"Ley 25.345: no se puede retirar en efectivo mas de ${settings.Ley25345ThresholdAmount:N2}. " +
                $"Intentaron retirar ${request.Amount:N2}. Usar Transfer en su lugar.",
                invariantCode: "INV-094");
        }

        // Alerta admin (soft): el dashboard del admin debe destacar este
        // movimiento, pero no bloqueamos. Logueamos con prefijo "metric:" para
        // que Serilog lo capture como contador.
        if (request.Amount > settings.PhysicalRefundAlertThreshold)
        {
            _logger.LogWarning(
                "metric:physical_refund_alert | EntryPublicId={EntryPublicId} Amount={Amount} Threshold={Threshold} UserId={UserId}",
                entry.PublicId, request.Amount, settings.PhysicalRefundAlertThreshold, userId);

            // Audit dedicado de la alerta (ademas del audit base de Withdrawn).
            await _auditService.LogBusinessEventAsync(
                action: AuditActions.ClientCreditPhysicalRefundAlert,
                entityName: AuditActions.ClientCreditEntryEntityName,
                entityId: entry.PublicId.ToString(),
                details: JsonSerializer.Serialize(new
                {
                    entryPublicId = entry.PublicId,
                    bcPublicId = entry.BookingCancellation.PublicId,
                    request.Amount,
                    threshold = settings.PhysicalRefundAlertThreshold,
                }),
                userId: userId,
                userName: userName,
                ct: ct);
        }

        var withdrawal = BuildWithdrawalAndMovement(
            entry,
            request,
            WithdrawalKind.PhysicalCash,
            userId,
            userName);

        return withdrawal;
    }

    /// <summary>
    /// Kind 3: <c>Transfer</c>. Cliente retira por transferencia bancaria.
    /// Sin tope Ley 25.345 (no es efectivo). Genera ManualCashMovement Expense
    /// con el method override del request si vino (ej. "Transfer-BBVA").
    /// </summary>
    private Task<ClientCreditWithdrawal> HandleTransferAsync(
        ClientCreditEntry entry,
        WithdrawClientCreditRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        var withdrawal = BuildWithdrawalAndMovement(
            entry,
            request,
            WithdrawalKind.Transfer,
            userId,
            userName);

        return Task.FromResult(withdrawal);
    }

    /// <summary>
    /// Kind 4: <c>AppliedToNewBooking</c>. El saldo se aplica como pago de otra
    /// reserva del cliente. NO genera ManualCashMovement — el
    /// <c>PaymentService</c> lo hara al registrar el pago en la reserva destino
    /// (decision diferida a FC4, hoy validamos la reserva existe y dejamos el
    /// withdrawal registrado).
    ///
    /// <para>
    /// <b>FC1.2.3 alcance</b>: este handler decrementa el saldo y registra el
    /// withdrawal, pero NO crea el Payment en la reserva destino. Esa
    /// integracion vive en FC4 cuando se modele <c>PaymentService.ApplyCreditAsync</c>.
    /// Hoy un admin/contador tendria que crear el Payment a mano apuntando a la
    /// reserva destino + verificando el ClientCreditWithdrawal en auditoria.
    /// </para>
    /// </summary>
    private async Task<ClientCreditWithdrawal> HandleAppliedToNewBookingAsync(
        ClientCreditEntry entry,
        WithdrawClientCreditRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        if (request.AppliedToReservaPublicId is null)
        {
            throw new ArgumentException(
                "AppliedToNewBooking requiere AppliedToReservaPublicId en el request.",
                nameof(request));
        }

        // Validar que la reserva destino existe y pertenece al mismo customer
        // (defense-in-depth: el controller deberia haber validado ownership,
        // pero aca validamos integridad logica del saldo).
        var targetReserva = await _db.Reservas
            .FirstOrDefaultAsync(r => r.PublicId == request.AppliedToReservaPublicId, ct)
            ?? throw new KeyNotFoundException(
                $"Reserva destino {request.AppliedToReservaPublicId} no encontrada.");

        if (targetReserva.PayerId != entry.CustomerId)
        {
            // Regla de negocio: el saldo del cliente A no se puede aplicar a una
            // reserva del cliente B. Si esto pasa, hay un error operativo.
            throw new BusinessInvariantViolationException(
                "La reserva destino no pertenece al mismo cliente del saldo. " +
                "No se puede aplicar saldo de un cliente a la reserva de otro.",
                invariantCode: "INV-093");
        }

        var withdrawal = new ClientCreditWithdrawal
        {
            ClientCreditEntryId = entry.Id,
            Entry = entry,
            Kind = WithdrawalKind.AppliedToNewBooking,
            Amount = ReservationEconomicPolicy.RoundCurrency(request.Amount),
            ExecutedAt = DateTime.UtcNow,
            ExecutedByUserId = userId,
            ExecutedByUserName = userName ?? string.Empty,
            // NO genera ManualCashMovement (FC4 hara el Payment en la reserva nueva).
            ManualCashMovementId = null,
            ApprovalRequestId = null,
        };
        _db.ClientCreditWithdrawals.Add(withdrawal);

        return withdrawal;
    }

    /// <summary>
    /// Kind 5: <c>ReversedToOperator</c>. Cliente DEVUELVE plata ya retirada
    /// para que la agencia la re-acredite al operador (caso raro, post-T3).
    ///
    /// <para>
    /// <b>Reglas</b>:
    /// <list type="bullet">
    ///   <item>Requiere <c>ApprovalRequestPublicId</c> de tipo
    ///         <c>ClientRefundReversal</c> aprobado para entityType="ClientCreditEntry",
    ///         entityId=entry.Id, requestedBy=userId.</item>
    ///   <item>Audit reforzado: ademas del audit base
    ///         <c>ClientCreditWithdrawn</c>, logueamos
    ///         <c>ClientRefundReversalApproved</c> con todos los datos para el
    ///         daily egress report (ADR-002 §8).</item>
    ///   <item>Genera ManualCashMovement Income (la plata vuelve a caja porque
    ///         el cliente la entrego).</item>
    /// </list>
    /// </para>
    /// </summary>
    private async Task<ClientCreditWithdrawal> HandleReversedToOperatorAsync(
        ClientCreditEntry entry,
        WithdrawClientCreditRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        if (request.ApprovalRequestPublicId is null)
        {
            throw new ApprovalRequiredException(
                ApprovalRequestType.ClientRefundReversal,
                AuditActions.ClientCreditEntryEntityName,
                entry.Id);
        }

        var approval = await _db.ApprovalRequests
            .FirstOrDefaultAsync(a => a.PublicId == request.ApprovalRequestPublicId, ct)
            ?? throw new ApprovalRequiredException(
                ApprovalRequestType.ClientRefundReversal,
                AuditActions.ClientCreditEntryEntityName,
                entry.Id);

        // Coherencia approval ↔ entry: tipo correcto, scoped a este entry,
        // aprobado, no expirado, y solicitado por el mismo user que lo ejecuta
        // (mismo patron que BookingCancellationService.ConfirmAsync).
        var validForReversal = approval.RequestType == ApprovalRequestType.ClientRefundReversal
                            && approval.EntityType == AuditActions.ClientCreditEntryEntityName
                            && approval.EntityId == entry.Id
                            && approval.Status == ApprovalStatus.Approved
                            && approval.RequestedByUserId == userId
                            && approval.ExpiresAt > DateTime.UtcNow;
        if (!validForReversal)
        {
            throw new ApprovalRequiredException(
                ApprovalRequestType.ClientRefundReversal,
                AuditActions.ClientCreditEntryEntityName,
                entry.Id);
        }

        // OJO trainee/junior — orden de operaciones (Obs 4 review FC1.2.3, 2026-05-18):
        //
        // El audit reforzado `ClientRefundReversalApproved` se MOVIO al flujo
        // comun de WithdrawAsync (paso 6b), DESPUES del SaveChanges del withdrawal.
        // Razon: AuditService.LogBusinessEventAsync hace su propio SaveChanges
        // internamente; si lo logueabamos aca (antes del SaveChanges del
        // withdrawal) y el INSERT del withdrawal fallaba (CHECK constraint, FK,
        // etc.), nos quedaba audit huerfano de un reversal que nunca ocurrio.
        // Veneno fiscal — el reporte diario mostraba "se aprobo reversal X" sin
        // que hubiera withdrawal asociado.
        //
        // Ahora: solo armamos withdrawal + movement aca. El audit se emite en
        // WithdrawAsync paso 6b SI Y SOLO SI el SaveChanges del paso 5 salio bien.

        // Consumir el approval. MarkConsumedAsync es idempotente: si ya esta
        // Consumed, no-op. Si esta en otro estado, throws.
        //
        // NOTA: MarkConsumedAsync internamente hace SaveChanges. Esto cae fuera
        // del modelo "audit despues del SaveChanges del withdrawal" pero es
        // aceptable porque: (a) si despues falla el SaveChanges del withdrawal,
        // el approval queda Consumed sin haber sido efectivamente usado —
        // recuperable manualmente; (b) si lo dejaramos para despues, dos
        // intentos en paralelo podrian doble-consumir el approval (peor).
        // Mantenemos consumo aca como guard pre-write.
        await _approvalService.MarkConsumedAsync(approval.Id, ct);

        // Construir withdrawal + ManualCashMovement Income (el builder detecta
        // kind=ReversedToOperator y arma Income en vez de Expense).
        var withdrawal = BuildWithdrawalAndMovement(
            entry,
            request,
            WithdrawalKind.ReversedToOperator,
            userId,
            userName,
            approvalRequestId: approval.PublicId.ToString());

        return withdrawal;
    }

    // =========================================================================
    // Queries
    // =========================================================================

    public async Task<ClientCreditEntryDto?> GetEntryByPublicIdAsync(
        Guid publicId,
        CancellationToken ct)
    {
        var entry = await _db.ClientCreditEntries
            .AsNoTracking()
            .Include(e => e.BookingCancellation)
            .Include(e => e.Customer)
            .Include(e => e.Allocation)
            .Include(e => e.Withdrawals)
            .FirstOrDefaultAsync(e => e.PublicId == publicId, ct);

        return entry is null ? null : MapEntry(entry);
    }

    public async Task<List<ClientCreditEntryDto>> GetEntriesByBcAsync(
        Guid bookingCancellationPublicId,
        CancellationToken ct)
    {
        // Resolver BC.Id desde PublicId con un Select chico para no traer la
        // fila entera (evita Include de Reserva/Customer/Supplier que no
        // necesitamos aca).
        var bcId = await _db.BookingCancellations
            .Where(b => b.PublicId == bookingCancellationPublicId)
            .Select(b => (int?)b.Id)
            .FirstOrDefaultAsync(ct);

        if (bcId is null)
        {
            // BC no existe -> lista vacia. No tiramos KeyNotFound porque la
            // semantica de la query es "dame los entries de esta BC"; si no
            // hay BC, no hay entries. El controller puede decidir 404 si quiere.
            return new List<ClientCreditEntryDto>();
        }

        var entries = await _db.ClientCreditEntries
            .AsNoTracking()
            .Include(e => e.BookingCancellation)
            .Include(e => e.Customer)
            .Include(e => e.Allocation)
            .Include(e => e.Withdrawals)
            .Where(e => e.BookingCancellationId == bcId.Value)
            .OrderBy(e => e.CreatedAt)
            .ToListAsync(ct);

        return entries.Select(MapEntry).ToList();
    }

    // =========================================================================
    // Helpers privados
    // =========================================================================

    /// <summary>
    /// Verificacion del feature flag (mismo patron que los otros services del
    /// modulo). Si esta off, ningun retiro puede ejecutarse — protege contra
    /// despliegues parciales en prod.
    /// </summary>
    private async Task EnsureFeatureFlagOnAsync(CancellationToken ct)
    {
        var settings = await _settings.GetEntityAsync(ct);
        if (!settings.EnableNewCancellationFlow)
        {
            throw new InvalidOperationException(
                "El modulo de cancelacion/refund no esta habilitado en este ambiente " +
                "(EnableNewCancellationFlow=false).");
        }
    }

    /// <summary>
    /// Valida lo comun a todos los kinds (excepto KeptAsCredit que se valida
    /// dentro del handler).
    /// </summary>
    private static void ValidateAmountCommon(
        WithdrawClientCreditRequest request,
        ClientCreditEntry entry)
    {
        // KeptAsCredit valida en su handler (Amount debe ser exactamente 0).
        // Los demas kinds requieren Amount > 0.
        if (request.Kind == WithdrawalKind.KeptAsCredit)
        {
            return;
        }

        if (request.Amount <= 0m)
        {
            throw new ArgumentException(
                "Amount debe ser > 0 para retirar saldo.",
                nameof(request));
        }

        // INV-085: no se puede retirar mas que el saldo disponible. El CHECK SQL
        // del entry tambien lo previene a nivel BD (defensa en profundidad),
        // pero validar aca permite un mensaje claro en vez de DbUpdateException.
        if (request.Amount > entry.RemainingBalance)
        {
            throw new BusinessInvariantViolationException(
                $"El monto solicitado (${request.Amount:N2}) supera el saldo disponible " +
                $"(${entry.RemainingBalance:N2}) del cliente.",
                invariantCode: "INV-085");
        }
    }

    /// <summary>
    /// Helper compartido para los kinds que generan ManualCashMovement
    /// (PhysicalCash, Transfer, ReversedToOperator). Construye el withdrawal,
    /// invoca el <see cref="ManualCashMovementBuilder"/> y los agrega ambos al
    /// ChangeTracker. NO hace SaveChanges — el caller envolvente atomiza.
    /// </summary>
    private ClientCreditWithdrawal BuildWithdrawalAndMovement(
        ClientCreditEntry entry,
        WithdrawClientCreditRequest request,
        WithdrawalKind kind,
        string userId,
        string? userName,
        string? approvalRequestId = null)
    {
        var withdrawal = new ClientCreditWithdrawal
        {
            ClientCreditEntryId = entry.Id,
            Entry = entry,
            Kind = kind,
            Amount = ReservationEconomicPolicy.RoundCurrency(request.Amount),
            ExecutedAt = DateTime.UtcNow,
            ExecutedByUserId = userId,
            ExecutedByUserName = userName ?? string.Empty,
            ApprovalRequestId = approvalRequestId,
        };
        _db.ClientCreditWithdrawals.Add(withdrawal);

        // Crear el ManualCashMovement asociado. El builder detecta el kind y
        // decide Direction (Expense para PhysicalCash/Transfer, Income para
        // ReversedToOperator).
        //
        // Trainee/junior — fix Obs 2 review FC1.2.3 (2026-05-18):
        // El builder ahora setea internamente movement.ClientCreditWithdrawal =
        // withdrawal (navigation property) en vez de movement.ClientCreditWithdrawalId
        // al int crudo. Esto es CRITICO porque withdrawal.Id == 0 hasta el
        // SaveChanges (el Id real lo asigna Postgres). Si setearamos FK escalar
        // a 0, el INSERT del movement violaria la FK.
        //
        // Con navigation property, EF resuelve la FK en orden topologico:
        // primero inserta withdrawal (obtiene Id), despues movement con la FK
        // ya valida. Mismo patron que BuildIncomeForRefund usa con
        // OperatorRefundReceived (ver builder linea ~140).
        //
        // Antes habia un parche externo aca (`movement.ClientCreditWithdrawal =
        // withdrawal;`) para suplir el bug. Ya no hace falta — el builder
        // hace lo correcto desde su firma.
        var movement = ManualCashMovementBuilder.BuildExpenseForWithdrawal(
            withdrawal,
            entry,
            createdByUserId: userId,
            methodOverride: request.PaymentMethodOverride);

        // Reference: si vino en el request, sobreescribimos el Reference que el
        // builder dejo null (el builder no conoce el contexto operativo).
        if (!string.IsNullOrWhiteSpace(request.Reference))
        {
            movement.Reference = request.Reference;
        }
        _db.ManualCashMovements.Add(movement);

        return withdrawal;
    }

    private static ClientCreditWithdrawalDto MapWithdrawal(
        ClientCreditWithdrawal w,
        Guid entryPublicId)
    {
        return new ClientCreditWithdrawalDto
        {
            PublicId = w.PublicId,
            EntryPublicId = entryPublicId,
            Kind = w.Kind,
            Amount = w.Amount,
            ExecutedAt = w.ExecutedAt,
            ExecutedByUserId = w.ExecutedByUserId,
            ExecutedByUserName = w.ExecutedByUserName,
            // ManualCashMovementPublicId requiere que el navigation property
            // este cargado o que el Id resuelva post-SaveChanges. En este
            // contexto (justo despues del SaveChanges del WithdrawAsync) no lo
            // tenemos a mano sin un query extra — lo dejamos null y el frontend
            // puede pedir GetEntry para obtener el detalle completo.
            ManualCashMovementPublicId = null,
            ApprovalRequestId = w.ApprovalRequestId,
        };
    }

    private static ClientCreditEntryDto MapEntry(ClientCreditEntry e)
    {
        return new ClientCreditEntryDto
        {
            PublicId = e.PublicId,
            BookingCancellationPublicId = e.BookingCancellation?.PublicId ?? Guid.Empty,
            CustomerPublicId = e.Customer?.PublicId ?? Guid.Empty,
            OperatorRefundAllocationPublicId = e.Allocation?.PublicId ?? Guid.Empty,
            CreditedAmount = e.CreditedAmount,
            RemainingBalance = e.RemainingBalance,
            IsFullyConsumed = e.IsFullyConsumed,
            CreatedAt = e.CreatedAt,
            Withdrawals = e.Withdrawals?
                .OrderBy(w => w.ExecutedAt)
                .Select(w => new ClientCreditWithdrawalDto
                {
                    PublicId = w.PublicId,
                    EntryPublicId = e.PublicId,
                    Kind = w.Kind,
                    Amount = w.Amount,
                    ExecutedAt = w.ExecutedAt,
                    ExecutedByUserId = w.ExecutedByUserId,
                    ExecutedByUserName = w.ExecutedByUserName,
                    ApprovalRequestId = w.ApprovalRequestId,
                    ManualCashMovementPublicId = null,
                })
                .ToList() ?? new List<ClientCreditWithdrawalDto>(),
        };
    }
}
