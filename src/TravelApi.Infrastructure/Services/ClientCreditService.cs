using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
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
using TravelApi.Infrastructure.Reservations;

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
    // FC4 (fix I1, 2026-06-14): resolucion de ownership de la reserva DESTINO al aplicar saldo a favor.
    // Opcionales (null por default) por la MISMA razon que en PaymentService: los tests unitarios viejos
    // construyen el service sin HttpContext/resolver, y en ese caso NO filtramos (comportamiento legacy).
    // Ver GetTargetReservaOwnerScopeOrNullAsync.
    private readonly IUserPermissionResolver? _permissionResolver;
    private readonly IHttpContextAccessor? _httpContextAccessor;

    public ClientCreditService(
        AppDbContext db,
        IBookingCancellationService bcService,
        IApprovalRequestService approvalService,
        IAuditService auditService,
        IOperationalFinanceSettingsService settings,
        ILogger<ClientCreditService> logger,
        IUserPermissionResolver? permissionResolver = null,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _db = db;
        _bcService = bcService;
        _approvalService = approvalService;
        _auditService = auditService;
        _settings = settings;
        _logger = logger;
        _permissionResolver = permissionResolver;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// FC4 (fix I1, 2026-06-14): replica EXACTA de <c>PaymentService.GetOwnerScopeOrNullAsync</c>.
    /// Devuelve null si el usuario ve TODAS las cobranzas (Admin o permiso <c>cobranzas.view_all</c>) =>
    /// no se filtra por reserva. Si no, devuelve el userId actual, que el caller compara contra
    /// <c>Reserva.ResponsibleUserId</c> de la reserva DESTINO.
    ///
    /// <para><b>Por que existe</b>: el endpoint Withdraw valida ownership del BOLSILLO origen
    /// (RequireOwnership sobre ClientCreditEntry), pero la reserva DESTINO viaja en el body
    /// (<c>AppliedToReservaPublicId</c>) y NO pasa por ningun attribute de ownership. Sin este chequeo, un
    /// vendedor con scope acotado podia aplicar saldo a una reserva del mismo cliente pero a cargo de OTRO
    /// vendedor — algo que el alta de pago normal (PaymentService.CreatePaymentAsync) ya le bloquea. Esto
    /// cierra esa fuga horizontal manteniendo la frontera de ownership por-reserva de Cobranzas.</para>
    ///
    /// <para><b>Comportamiento legacy</b>: si no hay HttpContext (tests unitarios que llaman WithdrawAsync
    /// directo), devuelve null => no se filtra, identico a hoy. Asi no se rompen los tests existentes.</para>
    /// </summary>
    private async Task<string?> GetTargetReservaOwnerScopeOrNullAsync(CancellationToken ct)
    {
        var httpUser = _httpContextAccessor?.HttpContext?.User;
        if (httpUser is null) return null; // sin HttpContext (tests unitarios): comportamiento legacy
        if (httpUser.IsInRole("Admin")) return null;

        var currentUserId = httpUser.FindFirstValue(ClaimTypes.NameIdentifier);
        if (_permissionResolver is null || string.IsNullOrEmpty(currentUserId))
        {
            // No podemos resolver permisos => fail-safe: filtrar por user actual o sentinel imposible.
            return string.IsNullOrEmpty(currentUserId) ? "__no_user__" : currentUserId;
        }

        var perms = await _permissionResolver.GetPermissionsAsync(currentUserId, ct);
        if (perms.Contains(Permissions.CobranzasViewAll))
        {
            return null; // ve todas las cobranzas
        }

        return currentUserId; // scope acotado: solo sus reservas
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
            // ADR-022 §4.9 (Q2): el bolsillo de cancelacion lleva la moneda REAL del refund desde el dia
            // uno (OperatorRefundReceived.Currency ya existe). Normalizamos por las dudas (null -> ARS).
            Currency = Monedas.Normalizar(currency),
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
        //    Retorna el withdrawal armado (todavia con Id=0 — SaveChanges al final) Y, como segundo
        //    valor, el Id de la reserva DESTINO a recalcular post-commit (solo AppliedToNewBooking lo
        //    devuelve; los otros 4 kinds devuelven null porque no tocan otra reserva). FC4: necesitamos
        //    saber que reserva recalcular para que el puente de aplicacion baje su deuda atomicamente.
        var (withdrawal, targetReservaIdToRecalc) = request.Kind switch
        {
            WithdrawalKind.KeptAsCredit => await HandleKeptAsCreditAsync(entry, request, userId, userName, ct),
            WithdrawalKind.PhysicalCash => await HandlePhysicalCashAsync(entry, request, userId, userName, ct),
            WithdrawalKind.Transfer => await HandleTransferAsync(entry, request, userId, userName, ct),
            WithdrawalKind.AppliedToNewBooking => await HandleAppliedToNewBookingAsync(entry, request, userId, userName, ct),
            WithdrawalKind.ReversedToOperator => await HandleReversedToOperatorAsync(entry, request, userId, userName, ct),
            _ => throw new ArgumentException($"WithdrawalKind no soportado: {request.Kind}", nameof(request)),
        };

        // 4-8) Persistencia + recalculo del destino + audit + cierre del BC.
        //
        //    FC4 (2026-06-14, fix bloqueante B1 del review): cuando el kind es AppliedToNewBooking, el
        //    decremento del bolsillo, el INSERT del withdrawal Y el INSERT del Payment puente en la reserva
        //    destino, MAS el recalculo de la deuda de esa reserva (ReservaMoneyByCurrency), tienen que ser
        //    atomicos. Si el recalculo del destino fallara despues de bajar el bolsillo, quedaria plata
        //    perdida (saldo descontado sin haber acreditado la reserva) — exactamente el bug que FC4 cierra.
        //    Por eso, SOLO cuando hay una reserva destino a recalcular, envolvemos todo en una transaccion
        //    explicita. Para los otros 4 kinds dejamos el flujo viejo intacto (sin transaccion envolvente):
        //    minimizamos el blast radius y no cambiamos su comportamiento.
        //
        //    OJO trainee/junior: la transaccion envolvente SOLO se puede usar contra un provider RELACIONAL.
        //    Los tests unit corren sobre EF InMemory, que NO soporta transacciones (BeginTransactionAsync
        //    explota). Por eso ramificamos por _db.Database.IsRelational(): en InMemory ejecutamos el mismo
        //    cuerpo sin transaccion (los tests de atomicidad real viven en integracion Postgres).
        bool needsWrappingTransaction = targetReservaIdToRecalc != null && _db.Database.IsRelational();

        if (needsWrappingTransaction)
        {
            // CreateExecutionStrategy + BeginTransactionAsync: mismo patron que ReservaService.CreateReservaAsync.
            // El ExecutionStrategy reintenta toda la lambda si Postgres devuelve un error transitorio.
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _db.Database.BeginTransactionAsync(ct);
                await PersistAndFinalizeAsync(commitDestinationRecalc: true);
                await transaction.CommitAsync(ct);
            });
        }
        else
        {
            // Flujo sin transaccion envolvente: los otros kinds (sin destino) o InMemory en tests.
            await PersistAndFinalizeAsync(commitDestinationRecalc: targetReservaIdToRecalc != null);
        }

        return MapWithdrawal(withdrawal, entry.PublicId);

        // Cuerpo comun de persistencia. Definido como local function para reusarlo dentro y fuera de la
        // transaccion envolvente sin duplicar codigo. El parametro indica si ademas hay que recalcular la
        // reserva destino (solo AppliedToNewBooking). Las SaveChanges internas (de aca, del audit y del
        // persister del destino) participan de la transaccion ambiente cuando existe, asi un fallo en
        // cualquier paso revierte TODO.
        async Task PersistAndFinalizeAsync(bool commitDestinationRecalc)
        {
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

            // 5) PRIMER SaveChanges: persiste withdrawal + (opcional) ManualCashMovement o Payment puente
            //    + decremento saldo del entry. Si el CHECK SQL del saldo o cualquier
            //    constraint falla, throws aca SIN haber emitido audit. La fila de
            //    audit fiscal solo se emite si este SaveChanges salio bien.
            //
            //    EF resuelve aca el orden topologico: ClientCreditWithdrawal primero
            //    (obtiene Id real de la secuencia Postgres), luego el Payment puente
            //    con la FK AppliedFromCreditWithdrawalId ya valida (lo seteamos por
            //    navigation property en el handler, no por Id escalar=0).
            await _db.SaveChangesAsync(ct);

            // 5b) FC4: recalcular la deuda de la reserva DESTINO. El Payment puente ya esta persistido (vivo,
            //     AffectsCash=false, monto positivo), asi que ReservaMoneyCalculator lo cuenta y baja la deuda
            //     exigible de la moneda del bolsillo. Esto es lo que hace que el saldo a favor efectivamente
            //     PAGUE la otra reserva (antes de FC4 esto no existia -> plata perdida). Corre DENTRO de la
            //     transaccion envolvente (cuando la hay), antes del commit.
            if (commitDestinationRecalc && targetReservaIdToRecalc != null)
            {
                await ReservaMoneyPersister.PersistAsync(_db, targetReservaIdToRecalc.Value, ct);
            }

            // 6) Audit base ClientCreditWithdrawn (todos los kinds, ya con withdrawal
            //    persistido). Lo logueamos solo ahora para que el audit no pueda
            //    quedar huerfano si el SaveChanges hubiera fallado.
            await _auditService.LogBusinessEventAsync(
                action: AuditActions.ClientCreditWithdrawn,
                entityName: AuditActions.ClientCreditWithdrawalEntityName,
                entityId: withdrawal.PublicId.ToString(),
                details: JsonSerializer.Serialize(new
                {
                    withdrawalPublicId = withdrawal.PublicId,
                    entryPublicId = entry.PublicId,
                    // ADR-022 §4.9: nullable para creditos de SOBREPAGO (sin BC detras).
                    bcPublicId = entry.BookingCancellation?.PublicId,
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
                        bcPublicId = entry.BookingCancellation?.PublicId,
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

            // 6c) FC4: audit dedicado del LADO DESTINO. Ademas del audit base (que vive del lado del bolsillo),
            //     dejamos rastro de que ESTA reserva recibio un saldo a favor como pago. Asi la auditoria de la
            //     reserva destino muestra de donde salio la plata que bajo su deuda (no es un cobro de caja).
            if (withdrawal.Kind == WithdrawalKind.AppliedToNewBooking && targetReservaIdToRecalc != null)
            {
                await _auditService.LogBusinessEventAsync(
                    action: AuditActions.ClientCreditAppliedToBooking,
                    entityName: AuditActions.ClientCreditWithdrawalEntityName,
                    entityId: withdrawal.PublicId.ToString(),
                    details: JsonSerializer.Serialize(new
                    {
                        withdrawalPublicId = withdrawal.PublicId,
                        entryPublicId = entry.PublicId,
                        customerPublicId = entry.Customer.PublicId,
                        targetReservaPublicId = request.AppliedToReservaPublicId,
                        amount = withdrawal.Amount,
                        currency = entry.Currency,
                    }),
                    userId: userId,
                    userName: userName,
                    ct: ct);
            }

            // FC1.2.7b counter: el cliente retiro saldo. Pasamos `kind` como property
            // estructurada para que el dashboard pueda filtrar por tipo de retiro.
            _logger.LogInformation(
                "metric:client_credit_withdrawn | WithdrawalPublicId={WithdrawalPublicId} EntryPublicId={EntryPublicId} Kind={Kind} Amount={Amount}",
                withdrawal.PublicId, entry.PublicId, withdrawal.Kind.ToString(), withdrawal.Amount);

            // 7) Cierre del BC si TODOS los entries estan consumidos.
            // ADR-022 §4.9 (B5): el cierre de cancelacion solo aplica a creditos que NACEN de una cancelacion
            // (BookingCancellationId != null). Un credito de SOBREPAGO no tiene BC detras (FK null): consumirlo
            // totalmente NO debe disparar OnAllCreditConsumedAsync. Por eso ramificamos por el origen del entry.
            if (request.Kind != WithdrawalKind.KeptAsCredit
                && entry.IsFullyConsumed
                && entry.BookingCancellationId != null)
            {
                await _bcService.OnAllCreditConsumedAsync(entry.BookingCancellationId.Value, ct);

                // 8) SEGUNDO SaveChanges: persiste cambios del callback
                //    (bc.Status=Closed, bc.ClosedAt, bc.Reserva.Status=Cancelled).
                await _db.SaveChangesAsync(ct);
            }
        }
    }

    // =========================================================================
    // ReverseAppliedCreditAsync (FC4 reversa — entrada publica del controller)
    // =========================================================================

    /// <summary>
    /// FC4 reversa (2026-06-18): deshace un withdrawal <c>AppliedToNewBooking</c>. Ver el contrato completo en
    /// <see cref="IClientCreditService.ReverseAppliedCreditAsync"/>. Espejo del WithdrawAsync(AppliedToNewBooking):
    /// vuelve la plata al bolsillo, soft-deletea el puente y recalcula la deuda de la reserva destino, todo
    /// atomico.
    /// </summary>
    public async Task<ClientCreditWithdrawalDto> ReverseAppliedCreditAsync(
        Guid withdrawalPublicId,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        // 0) Mismo gate de feature flag que WithdrawAsync: si el modulo no esta habilitado, la reversa tampoco
        //    opera. NO tocamos el flag, solo somos consistentes con el resto del modulo.
        await EnsureFeatureFlagOnAsync(ct);

        // 1) Cargar el withdrawal + su entry (con customer) tracked: vamos a re-incrementar el saldo del entry.
        var withdrawal = await _db.ClientCreditWithdrawals
            .Include(w => w.Entry).ThenInclude(e => e.Customer)
            .FirstOrDefaultAsync(w => w.PublicId == withdrawalPublicId, ct)
            ?? throw new KeyNotFoundException(
                $"ClientCreditWithdrawal {withdrawalPublicId} no encontrado.");

        // 2) Solo se revierte una APLICACION a otra reserva. Los otros kinds (efectivo, transferencia,
        //    KeptAsCredit, ReversedToOperator) tienen sus propios flujos de anulacion; no se mezclan aca.
        //    ValidationException -> el controller la traduce a 400 (request mal dirigido), no a 409.
        if (withdrawal.Kind != WithdrawalKind.AppliedToNewBooking)
        {
            throw new System.ComponentModel.DataAnnotations.ValidationException(
                $"Solo se puede revertir una aplicacion de saldo a favor a otra reserva " +
                $"(kind AppliedToNewBooking). Este retiro es {withdrawal.Kind}.");
        }

        var entry = withdrawal.Entry;

        // 3) Encontrar el puente VIVO atado a este withdrawal. Su existencia es la fuente de verdad de "esta
        //    aplicacion sigue activa" (el withdrawal no tiene flag de reversado).
        var liveBridge = await AppliedCreditBridge.FindLiveBridgeAsync(_db, withdrawal.Id, ct);

        // 4) Guardas de integridad (anti doble-reversa + tope superior del bolsillo). Si bloquea, abortamos
        //    SIN mutar nada. BusinessInvariantViolationException -> 409 via el controller.
        var blockReason = AppliedCreditBridge.GetReverseBlockReason(entry, liveBridge);
        if (blockReason is not null)
        {
            throw new BusinessInvariantViolationException(blockReason, invariantCode: "INV-098");
        }

        // liveBridge no es null aca (GetReverseBlockReason bloquea si lo fuera). Guardamos la reserva destino
        // para recalcular su deuda despues del soft-delete del puente.
        var targetReservaId = liveBridge!.ReservaId
            ?? throw new InvalidOperationException(
                "El pago puente de saldo a favor aplicado no tiene reserva destino. Estado inconsistente.");

        // 4b) Ownership de la reserva DESTINO (mismo principio que el fix I1 del WithdrawAsync). La reversa
        //     MUTA la deuda de esa reserva, asi que un vendedor con scope acotado solo puede revertir sobre una
        //     reserva a su cargo (a menos que vea todas las cobranzas). Sin HttpContext (tests/legacy) no se
        //     filtra. UnauthorizedAccessException -> el controller la traduce a 403.
        var ownerScope = await GetTargetReservaOwnerScopeOrNullAsync(ct);
        if (ownerScope is not null)
        {
            var targetResponsible = await _db.Reservas
                .AsNoTracking()
                .Where(r => r.Id == targetReservaId)
                .Select(r => r.ResponsibleUserId)
                .FirstOrDefaultAsync(ct);

            if (!string.Equals(targetResponsible, ownerScope, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException(
                    "La reserva destino no esta asignada al usuario actual. No se puede revertir una " +
                    "aplicacion de saldo sobre una reserva a cargo de otro vendedor.");
            }
        }

        // 5) Persistencia atomica. Igual criterio que WithdrawAsync: solo contra provider RELACIONAL usamos
        //    transaccion envolvente (InMemory en los tests no soporta transacciones -> ramificamos por
        //    IsRelational y corremos el mismo cuerpo sin transaccion).
        if (_db.Database.IsRelational())
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _db.Database.BeginTransactionAsync(ct);
                await ApplyReverseAsync();
                await transaction.CommitAsync(ct);
            });
        }
        else
        {
            await ApplyReverseAsync();
        }

        return MapWithdrawal(withdrawal, entry.PublicId);

        // Cuerpo comun de la reversa (local function para reusarlo dentro/fuera de la transaccion sin duplicar).
        async Task ApplyReverseAsync()
        {
            // 5a) Soft-delete del puente + re-incremento del bolsillo (sin SaveChanges; lo hace el helper).
            var amountReturnedToPocket = AppliedCreditBridge.ReverseArtifacts(entry, liveBridge);

            // 5b) PRIMER SaveChanges: persiste el soft-delete del puente + el re-incremento del saldo del entry,
            //     antes de tocar la reserva destino y antes de cualquier audit. Si falla, no quedo nada a medias.
            await _db.SaveChangesAsync(ct);

            // 5c) Recalcular la deuda de la reserva destino. Con el puente soft-deleted, ReservaMoneyCalculator
            //     deja de contarlo y la deuda vuelve a su nivel previo. Corre DENTRO de la transaccion envolvente.
            await ReservaMoneyPersister.PersistAsync(_db, targetReservaId, ct);

            // 5d) Audit de la reversa (DESPUES del SaveChanges del efecto, mismo principio anti-huerfano que
            //     WithdrawAsync paso 6). Deja rastro de cuanto volvio al bolsillo y de que reserva salio.
            await _auditService.LogBusinessEventAsync(
                action: AuditActions.ClientCreditApplicationReversed,
                entityName: AuditActions.ClientCreditWithdrawalEntityName,
                entityId: withdrawal.PublicId.ToString(),
                details: JsonSerializer.Serialize(new
                {
                    withdrawalPublicId = withdrawal.PublicId,
                    entryPublicId = entry.PublicId,
                    customerPublicId = entry.Customer.PublicId,
                    targetReservaId,
                    amountReturnedToPocket,
                    currency = entry.Currency,
                    remainingBalanceAfter = entry.RemainingBalance,
                }),
                userId: userId,
                userName: userName,
                ct: ct);

            _logger.LogInformation(
                "metric:client_credit_application_reversed | WithdrawalPublicId={WithdrawalPublicId} EntryPublicId={EntryPublicId} Amount={Amount} TargetReservaId={TargetReservaId}",
                withdrawal.PublicId, entry.PublicId, amountReturnedToPocket, targetReservaId);
        }
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
    private Task<(ClientCreditWithdrawal withdrawal, int? targetReservaIdToRecalc)> HandleKeptAsCreditAsync(
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

        // KeptAsCredit no toca otra reserva -> segundo valor null.
        return Task.FromResult<(ClientCreditWithdrawal, int?)>((withdrawal, null));
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
    private async Task<(ClientCreditWithdrawal withdrawal, int? targetReservaIdToRecalc)> HandlePhysicalCashAsync(
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

        // No toca otra reserva -> segundo valor null.
        return (withdrawal, null);
    }

    /// <summary>
    /// Kind 3: <c>Transfer</c>. Cliente retira por transferencia bancaria.
    /// Sin tope Ley 25.345 (no es efectivo). Genera ManualCashMovement Expense
    /// con el method override del request si vino (ej. "Transfer-BBVA").
    /// </summary>
    private Task<(ClientCreditWithdrawal withdrawal, int? targetReservaIdToRecalc)> HandleTransferAsync(
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

        // No toca otra reserva -> segundo valor null.
        return Task.FromResult<(ClientCreditWithdrawal, int?)>((withdrawal, null));
    }

    /// <summary>
    /// Kind 4: <c>AppliedToNewBooking</c> (FC4, 2026-06-14). El saldo a favor del cliente se aplica como
    /// PAGO de OTRA reserva del mismo cliente. Ademas de registrar el withdrawal y decrementar el bolsillo,
    /// crea un <see cref="Payment"/> "puente" POSITIVO en la reserva destino (ver <see cref="AppliedCreditBridge"/>):
    /// no mueve caja (<c>AffectsCash=false</c>, porque la plata ya entro cuando el operador devolvio el
    /// refund) pero baja la deuda exigible de esa reserva.
    ///
    /// <para><b>Por que un Payment y no un ManualCashMovement</b>: lo que tiene que bajar es la DEUDA del
    /// cliente en la reserva destino, y eso lo calcula <c>ReservaMoneyCalculator</c> a partir de los
    /// Payments vivos. Un ManualCashMovement no entra en esa cuenta. Por eso el puente es un Payment.</para>
    ///
    /// <para><b>Validaciones (diseño aprobado + correcciones del review)</b>:
    /// <list type="bullet">
    ///   <item>INV-093: la reserva destino debe ser del MISMO cliente del bolsillo.</item>
    ///   <item>Estado destino: debe ser VENTA FIRME (<see cref="EstadoReserva.IsSaleFirmStatus"/>, incluye
    ///         Closed desde ADR-033). Aplicar saldo a un Presupuesto/Cotizacion/Perdida/Cancelada no tiene
    ///         sentido (no hay venta firme que pagar) -> INV-096. Una Finalizada con deuda SI acepta saldo.</item>
    ///   <item>Moneda (MVP same-currency): el puente lleva la moneda del bolsillo. Si la reserva destino NO
    ///         tiene deuda exigible en esa moneda, se rechaza (INV-095). Esto bloquea de hecho el cruce de
    ///         monedas: un bolsillo en USD no puede pagar una deuda en ARS.</item>
    ///   <item>Tope: el monto aplicado no puede superar la deuda exigible del destino en esa moneda (INV-097).
    ///         No sobre-pagamos la reserva destino.</item>
    /// </list></para>
    /// </summary>
    private async Task<(ClientCreditWithdrawal withdrawal, int? targetReservaIdToRecalc)> HandleAppliedToNewBookingAsync(
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

        // Cargamos la reserva destino CON su grafo economico (servicios + pagos) porque ademas de validar
        // ownership/estado necesitamos calcular su deuda exigible por moneda. Mismos Includes que el persister
        // canonico (ReservaMoneyPersister): si difieren, la cuenta podria descuadrar.
        var targetReserva = await LoadReservaWithEconomicGraphAsync(request.AppliedToReservaPublicId.Value, ct)
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

        // FC4 (fix I1, 2026-06-14): ownership de la reserva DESTINO. INV-093 (arriba) ya garantiza que la
        // reserva sea del MISMO cliente, pero eso NO alcanza: un cliente puede tener reservas a cargo de
        // distintos vendedores. El alta de pago normal exige que, si el usuario no ve todas las cobranzas,
        // la reserva sea suya (Reserva.ResponsibleUserId == userId). Replicamos ese mismo scope aca para no
        // dejar una puerta lateral que permita aplicar saldo a una reserva ajena (misma fuga que cierra
        // PaymentService.CreatePaymentAsync). Si no hay HttpContext (tests/legacy), el scope es null y no
        // se filtra. Tiramos UnauthorizedAccessException, que el controller traduce a 403.
        var ownerScope = await GetTargetReservaOwnerScopeOrNullAsync(ct);
        if (ownerScope is not null
            && !string.Equals(targetReserva.ResponsibleUserId, ownerScope, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "La reserva destino no esta asignada al usuario actual. No se puede aplicar saldo a una " +
                "reserva a cargo de otro vendedor.");
        }

        // Estado VENTA FIRME: no se aplica saldo a una reserva que no es una venta firme (Presupuesto,
        // Cotizacion, Perdida, Cancelada, esperando refund).
        //
        // ADR-033 (2026-06-16, E5/B2): la pregunta de estado pasa de IsCollectableStatus (sin Closed) a
        // IsSaleFirmStatus (CON Closed). Asi se puede aplicar un saldo a favor a una reserva Finalizada que
        // todavia tiene deuda (mismo desacople que el cobro normal: cobrar/aplicar saldo depende de la deuda
        // real, no del estado operativo). NO se baja a IsCollectable()/EnsureCollectable() escalar: eso
        // romperia el contrato fuerte de FC4. Se conserva EXACTAMENTE:
        //   - la BusinessInvariantViolationException con invariantCode="INV-096" (el GlobalExceptionHandler lo
        //     propaga al 409 y el front de saldo a favor + el test AppliedToNonCollectibleReserva_RejectsInv096
        //     dependen de ese codigo; EnsureCollectable tira InvalidOperationException generico y lo perderia);
        //   - la validacion de deuda PER-MONEDA de mas abajo (INV-095), mas fuerte que el Balance escalar.
        if (!EstadoReserva.IsSaleFirmStatus(targetReserva.Status))
        {
            throw new BusinessInvariantViolationException(
                "No se puede aplicar un saldo a favor a una reserva que no esta en gestion de cobro " +
                $"(estado actual: {targetReserva.Status}). Pasala a En gestion primero.",
                invariantCode: "INV-096");
        }

        var creditCurrency = Monedas.Normalizar(entry.Currency);

        // Deuda exigible de la reserva destino EN LA MONEDA DEL BOLSILLO. El calculador agrupa por moneda y
        // nunca mezcla USD/ARS, asi que esto es exactamente "cuanto debe la reserva en esa moneda".
        decimal targetDebtInCreditCurrency = GetReservaConfirmedBalanceForCurrency(targetReserva, creditCurrency);

        // Moneda (MVP same-currency): si la reserva destino no tiene deuda en la moneda del bolsillo, no hay
        // nada que pagar en esa moneda. Esto bloquea de hecho aplicar un saldo USD a una reserva que solo
        // debe ARS (y viceversa) sin convertir, que es justo lo que NO queremos en el MVP.
        if (targetDebtInCreditCurrency <= 0m)
        {
            throw new BusinessInvariantViolationException(
                $"No se puede aplicar un saldo a favor en {creditCurrency} a una reserva que no tiene " +
                $"deuda en {creditCurrency}.",
                invariantCode: "INV-095");
        }

        var appliedAmount = ReservationEconomicPolicy.RoundCurrency(request.Amount);

        // Tope a la deuda destino: no sobre-pagamos la reserva destino. Leemos la deuda lo mas cerca posible
        // del commit; aun asi es best-effort ante una carrera (otro cobro entrando en paralelo). La red dura
        // es la transaccion envolvente + el recalculo final del destino dentro de esa transaccion: si el
        // numero cambio, el recalculo lo refleja y, en el peor caso de carrera, la reserva podria quedar con
        // un sobrepago leve que el circuito de sobrepago ya sabe manejar. No se permite forzar un sobre-pago
        // grande a proposito desde aca.
        if (appliedAmount > targetDebtInCreditCurrency)
        {
            throw new BusinessInvariantViolationException(
                $"El monto a aplicar (${appliedAmount:N2} {creditCurrency}) supera la deuda de la reserva " +
                $"destino en esa moneda (${targetDebtInCreditCurrency:N2}). Aplica como mucho la deuda.",
                invariantCode: "INV-097");
        }

        var withdrawal = new ClientCreditWithdrawal
        {
            ClientCreditEntryId = entry.Id,
            Entry = entry,
            Kind = WithdrawalKind.AppliedToNewBooking,
            Amount = appliedAmount,
            ExecutedAt = DateTime.UtcNow,
            ExecutedByUserId = userId,
            ExecutedByUserName = userName ?? string.Empty,
            // NO genera ManualCashMovement: el efecto en la reserva destino es el Payment puente de abajo.
            ManualCashMovementId = null,
            ApprovalRequestId = null,
        };
        _db.ClientCreditWithdrawals.Add(withdrawal);

        // Payment puente POSITIVO en la reserva destino: baja su deuda sin tocar caja.
        var bridge = new Payment
        {
            ReservaId = targetReserva.Id,
            Amount = appliedAmount,                         // POSITIVO (a diferencia del puente de sobrepago)
            Currency = creditCurrency,                      // moneda del bolsillo
            ImputedCurrency = null,                         // MVP same-currency: se imputa a su propia moneda
            Method = AppliedCreditBridge.BridgeMethod,      // "SaldoAFavorAplicado"
            AffectsCash = false,                            // la plata ya entro con el refund; NO mueve caja
            EntryType = PaymentEntryTypes.Payment,
            Status = "Paid",
            PaidAt = DateTime.UtcNow,
            Notes = $"Saldo a favor aplicado (bolsillo {entry.PublicId}).",
            CreatedByUserId = userId,
            CreatedByUserName = userName,
            // FC4: atamos el puente al withdrawal por NAVIGATION property (no por Id escalar). withdrawal.Id
            // es 0 hasta el SaveChanges; EF resuelve la FK en orden topologico (withdrawal primero, luego el
            // puente con AppliedFromCreditWithdrawalId ya valido). Mismo patron que el resto del modulo.
            AppliedFromCreditWithdrawal = withdrawal,
        };
        _db.Payments.Add(bridge);

        // Segundo valor: la reserva destino a recalcular post-SaveChanges (dentro de la transaccion envolvente).
        return (withdrawal, targetReserva.Id);
    }

    /// <summary>
    /// FC4: carga una reserva por PublicId con el MISMO grafo economico que usa <c>ReservaMoneyPersister</c>
    /// (pagos + 5 servicios tipados + genericos), para poder correr <c>ReservaMoneyCalculator</c> y leer la
    /// deuda por moneda. Se trackea (no AsNoTracking) porque la misma instancia se usa para crear el Payment
    /// puente apuntando a <c>targetReserva.Id</c>.
    /// </summary>
    private Task<Reserva?> LoadReservaWithEconomicGraphAsync(Guid reservaPublicId, CancellationToken ct)
    {
        return _db.Reservas
            .Include(f => f.Payments)
            .Include(f => f.Servicios)
            .Include(f => f.FlightSegments)
            .Include(f => f.HotelBookings)
            .Include(f => f.TransferBookings)
            .Include(f => f.PackageBookings)
            .Include(f => f.AssistanceBookings)
            .FirstOrDefaultAsync(r => r.PublicId == reservaPublicId, ct);
    }

    /// <summary>
    /// FC4: deuda exigible (ConfirmedSale - TotalPaid) de una reserva ya cargada, EN UNA moneda concreta.
    /// Devuelve 0 si esa moneda no aparece en la reserva o si esta saldada/a favor (Balance &lt;= 0). Usa el
    /// calculador puro <c>ReservaMoneyCalculator</c> para no duplicar la matematica oficial de la plata.
    /// </summary>
    private static decimal GetReservaConfirmedBalanceForCurrency(Reserva reserva, string currency)
    {
        var summary = TravelApi.Domain.Reservations.ReservaMoneyCalculator.Calculate(reserva);
        if (summary.PorMoneda.TryGetValue(currency, out var line))
        {
            return line.Balance;
        }
        return 0m;
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
    private async Task<(ClientCreditWithdrawal withdrawal, int? targetReservaIdToRecalc)> HandleReversedToOperatorAsync(
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

        // No toca otra reserva -> segundo valor null.
        return (withdrawal, null);
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

        // ADR-022 §4.4 (B1): asiento de caja de la devolucion fisica, en la MISMA SaveChanges. UN solo
        // asiento por el manual (RK-1: NO se asienta el ClientCreditWithdrawal por separado). SourceType
        // se deriva del FK del manual (ClientCreditWithdrawal). La MONEDA sale del ORIGEN REAL (la del
        // bolsillo, entry.Currency), NO del manual (que nace en ARS por default). Asi un retiro de un
        // bolsillo en USD asienta en USD.
        var withdrawalLedgerEntry = TravelApi.Domain.Helpers.CashLedgerEntryFactory.ForManualMovement(
            movement, currencyOverride: entry.Currency, actorUserId: userId, actorUserName: userName);
        _db.CashLedgerEntries.Add(withdrawalLedgerEntry);

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
