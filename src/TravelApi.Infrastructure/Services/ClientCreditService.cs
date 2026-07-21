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
    // FC4 flujo cliente: reintentos ante choque de concurrencia sobre el RemainingBalance del bolsillo (xmin).
    // Mismo valor que el lado operador (SupplierCreditService).
    private const int MaxConcurrencyRetries = 3;

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
    // ADR-044 "Deshacer una multa ya emitida" (2026-07-14): tercer origen del credito (B1 del re-review).
    // =========================================================================

    /// <summary>
    /// Origen del puente de "multa deshecha" (mismo criterio que <c>OverpaymentCreditCleanup.BridgeMethod</c> /
    /// <c>CancellationToClientCreditConverter.BridgeMethod</c>: un metodo propio para distinguir estos Payments
    /// tecnicos de los cobros reales del cliente).
    /// </summary>
    public const string DebitNoteUndoBridgeMethod = "MultaDeshecha";

    /// <summary>
    /// Mensaje de negocio cuando un usuario intenta borrar o editar DIRECTAMENTE el Payment puente de una multa
    /// deshecha. Espejo de <c>OverpaymentCreditCleanup.DirectBridgeMutationBlockReason</c>: el puente es respaldo
    /// interno del saldo a favor; tocarlo a mano lo desincroniza del bolsillo del cliente.
    /// </summary>
    public const string DirectBridgeMutationBlockReason =
        "Este movimiento es el respaldo interno de un saldo a favor por una multa deshecha; " +
        "gestionalo desde el saldo a favor del cliente.";

    /// <summary>
    /// ADR-044 "Deshacer una multa ya emitida" (endurecimiento post-gate 2026-07-14): true si <paramref name="payment"/>
    /// ES el Payment puente que traslada al bolsillo del cliente la porción cobrada de una multa deshecha. Espejo
    /// de <c>OverpaymentCreditCleanup.IsOverpaymentBridge</c> / <c>AppliedCreditBridge.IsAppliedCreditBridge</c>.
    ///
    /// <para>La firma es <c>Method == DebitNoteUndoBridgeMethod</c> + <c>AffectsCash == false</c> (lo distingue de
    /// un cobro real). No lleva FK propio (mismo criterio que el puente de anulación
    /// <c>CancellationToClientCreditConverter</c>): el Method es único y suficiente para identificarlo. Recibe la
    /// entidad materializada; EF NO lo traduce a SQL — para queries usar la forma inline expandida (Method +
    /// !AffectsCash).</para>
    /// </summary>
    public static bool IsDebitNoteUndoBridge(Payment payment)
    {
        return payment.Method == DebitNoteUndoBridgeMethod
            && !payment.AffectsCash;
    }

    /// <summary>
    /// ADR-044 "Deshacer una multa ya emitida" (B1 del re-review, 2026-07-14): cuando se deshace una Nota de
    /// Debito de multa que el cliente YA HABIA PAGADO (total o parcialmente), acuña un
    /// <see cref="ClientCreditEntry"/> por la porcion EFECTIVAMENTE COBRADA de la multa, en su moneda.
    ///
    /// <para><b>Es un ESPEJO de <c>PaymentService.ConvertOverpaymentToClientCreditAsync</c>
    /// (<c>OverpaymentCreditConverter</c>)</b>, NO de <see cref="CreateEntryAsync"/>: no hay ninguna
    /// <c>OperatorRefundAllocation</c> detras (la multa no es un reembolso del operador), asi que el credito
    /// nace con <c>OperatorRefundAllocationId = null</c> — igual que el credito de sobrepago. Y al igual que
    /// ese origen, <c>BookingCancellationId</c> tambien queda <c>null</c> A PROPOSITO: si se atara al BC,
    /// gastar este credito disparia el guard B5 (<c>OnAllCreditConsumedAsync</c>) que CIERRA el BC cuando todos
    /// sus creditos llegan a 0 — pero "deshacer la multa" deja el paso ABIERTO
    /// (<c>ConfirmedNoDebitNote</c>), nunca lo cierra. El BC se sigue trazando via
    /// <c>SourceDebitNoteAnnulmentId -&gt; BookingCancellationDebitNoteAnnulment.BookingCancellationId</c>.</para>
    ///
    /// <para><b>Idempotencia dura (retry de Hangfire)</b>: si YA existe un <see cref="ClientCreditEntry"/> con
    /// <see cref="ClientCreditEntry.SourceDebitNoteAnnulmentId"/> == <paramref name="annulmentId"/>, NO se crea
    /// otro (devuelve <c>null</c>). El reconciliador puede re-correr ante un retry de Hangfire; esta guarda es
    /// lo que garantiza a lo sumo UN credito por evento de deshacer.</para>
    ///
    /// <para><b>Ademas del bolsillo, mueve la plata FUERA del saldo de la reserva</b> con un <see cref="Payment"/>
    /// puente NEGATIVO (<c>AffectsCash=false</c>, no mueve caja: la plata real ya habia entrado con el cobro
    /// original de la multa). Sin este puente, la misma plata podria "reaparecer" como saldo a favor aparente
    /// si mas adelante se re-confirma una NUEVA multa sobre la misma reserva y moneda (doble-conteo silencioso).</para>
    ///
    /// <para><b>NO hace <c>SaveChangesAsync</c></b> (a diferencia de <c>OverpaymentCreditConverter.ConvertAsync</c>):
    /// solo <c>Add()</c> en memoria. Lo commitea el <c>DebitNoteAnnulmentReconciliation</c> que la invoca, en la
    /// MISMA unidad de trabajo que desvincula la ND (atomico: o queda todo, o no queda nada).</para>
    /// </summary>
    /// <param name="collectedPenaltyPortion">
    /// Porcion de la multa efectivamente cobrada (regla B1: <c>max(0, PenaltyAmountAtEvent - pendingPenalty)</c>,
    /// calculada por el caller ANTES de desvincular la ND). Si es &lt;= 0 (multa impaga), el caller NO debe
    /// invocar este metodo (no hay nada que acuñar); se valida igual acá por las dudas.
    /// </param>
    /// <returns>El <see cref="ClientCreditEntry"/> agregado (sin persistir), o <c>null</c> si no correspondia
    /// acuñar nada (monto &lt;= 0, o ya existia un credito para este evento).</returns>
    public static async Task<ClientCreditEntry?> CreateEntryFromDebitNoteUndoAsync(
        AppDbContext db,
        int annulmentId,
        int reservaId,
        int customerId,
        decimal collectedPenaltyPortion,
        string currency,
        string? actorUserId,
        string? actorUserName,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var amount = ReservationEconomicPolicy.RoundCurrency(collectedPenaltyPortion);
        if (amount <= 0m)
        {
            return null; // multa impaga (o ya neteada a 0): nada que acuñar, deja de ser deuda y listo.
        }

        var alreadyMinted = await db.ClientCreditEntries
            .AnyAsync(e => e.SourceDebitNoteAnnulmentId == annulmentId, cancellationToken);
        if (alreadyMinted)
        {
            logger.LogInformation(
                "Deshacer multa: ya existe un ClientCreditEntry para el evento {AnnulmentId}; no se acuña de nuevo (idempotencia).",
                annulmentId);
            return null;
        }

        var normalizedCurrency = Monedas.Normalizar(currency);

        var credit = new ClientCreditEntry
        {
            CustomerId = customerId,
            OperatorRefundAllocationId = null,
            BookingCancellationId = null, // deliberado: ver el XML-doc de arriba (guard B5).
            Currency = normalizedCurrency,
            CreditedAmount = amount,
            RemainingBalance = amount,
            IsFullyConsumed = false,
            CreatedAt = DateTime.UtcNow,
            SourcePaymentId = null,
            SourceReservaId = reservaId,
            SourceDebitNoteAnnulmentId = annulmentId,
            CreatedByUserId = actorUserId,
            CreatedByUserName = actorUserName,
        };
        db.ClientCreditEntries.Add(credit);

        var bridge = new Payment
        {
            ReservaId = reservaId,
            Amount = -amount,
            Currency = normalizedCurrency,
            Method = DebitNoteUndoBridgeMethod,
            Notes = "Multa del operador deshecha: la porción ya cobrada pasa a saldo a favor del cliente.",
            PaidAt = DateTime.UtcNow,
            Status = "Paid",
            EntryType = PaymentEntryTypes.Payment,
            AffectsCash = false,
            CreatedByUserId = actorUserId,
            CreatedByUserName = actorUserName,
        };
        db.Payments.Add(bridge);

        logger.LogInformation(
            "metric:debit_note_undo_credit_minted | AnnulmentId={AnnulmentId} ReservaId={ReservaId} CustomerId={CustomerId} {Currency} {Amount}",
            annulmentId, reservaId, customerId, normalizedCurrency, amount);

        return credit;
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
        CancellationToken ct,
        bool requesterIsAdmin = false)
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

        // 2026-06-24: bandera de auto-autorizacion del Admin para el caso ReversedToOperator. La setea
        // HandleReversedToOperatorAsync cuando el Admin saltea el approval ClientRefundReversal. La leemos en
        // el paso 6b para emitir el audit AdminSelfAuthorized DESPUES del SaveChanges del withdrawal (mismo
        // orden seguro que el resto de los audits: nunca audit huerfano de un retiro que no se persistio).
        bool reversalAdminSelfAuthorized = false;

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
        ClientCreditWithdrawal withdrawal;
        int? targetReservaIdToRecalc;
        switch (request.Kind)
        {
            case WithdrawalKind.KeptAsCredit:
                (withdrawal, targetReservaIdToRecalc) = await HandleKeptAsCreditAsync(entry, request, userId, userName, ct);
                break;
            case WithdrawalKind.PhysicalCash:
                (withdrawal, targetReservaIdToRecalc) = await HandlePhysicalCashAsync(entry, request, userId, userName, ct);
                break;
            case WithdrawalKind.Transfer:
                (withdrawal, targetReservaIdToRecalc) = await HandleTransferAsync(entry, request, userId, userName, ct);
                break;
            case WithdrawalKind.AppliedToNewBooking:
                (withdrawal, targetReservaIdToRecalc) = await HandleAppliedToNewBookingAsync(entry, request, userId, userName, ct);
                break;
            case WithdrawalKind.ReversedToOperator:
                (withdrawal, targetReservaIdToRecalc, reversalAdminSelfAuthorized) =
                    await HandleReversedToOperatorAsync(entry, request, userId, userName, requesterIsAdmin, ct);
                break;
            default:
                throw new ArgumentException($"WithdrawalKind no soportado: {request.Kind}", nameof(request));
        }

        // 4-8) Persistencia + recalculo del destino + audit + cierre del BC, TODO atomico.
        //
        //    Originalmente la transaccion envolvente solo cubria AppliedToNewBooking (FC4, fix B1 2026-06-14).
        //    ARREGLO 2 (atomicidad de retiros, 2026-06-24): la extendemos a TODOS los kinds. Razon: los retiros
        //    PhysicalCash / Transfer / ReversedToOperator decrementan el bolsillo de saldo a favor Y emiten un
        //    EGRESO DE CAJA real (ManualCashMovement + asiento en el Libro de Caja, ver BuildWithdrawalAndMovement),
        //    encadenando ademas el/los audit (que hacen su propio SaveChanges) y el cierre del BC (otro SaveChanges).
        //    Sin transaccion, si el proceso se cortaba a mitad la plata ya habia salido de caja pero el estado
        //    quedaba a medias (ej. asiento de egreso escrito pero el BC sin cerrar, o el audit fiscal sin emitir).
        //    Para ReversedToOperator esto es especialmente sensible: es plata que vuelve al operador.
        //
        //    Con la transaccion envolvente, todas las SaveChanges internas (la del paso 5, las de los audits y la
        //    del cierre del BC) se flushean dentro de la MISMA transaccion abierta y NO commitean: el commit real
        //    ocurre una sola vez en transaction.CommitAsync. O queda todo, o no queda nada.
        //
        //    OJO trainee/junior: la transaccion envolvente SOLO se puede usar contra un provider RELACIONAL.
        //    Los tests unit corren sobre EF InMemory, que NO soporta transacciones (BeginTransactionAsync
        //    explota). Por eso ramificamos por _db.Database.IsRelational(): en InMemory ejecutamos el mismo
        //    cuerpo sin transaccion (los tests de atomicidad real viven en integracion Postgres).
        if (_db.Database.IsRelational())
        {
            // CreateExecutionStrategy + BeginTransactionAsync: mismo patron que ReservaService.CreateReservaAsync.
            // El ExecutionStrategy reintenta toda la lambda si Postgres devuelve un error transitorio.
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _db.Database.BeginTransactionAsync(ct);
                await PersistAndFinalizeAsync(commitDestinationRecalc: targetReservaIdToRecalc != null);
                await transaction.CommitAsync(ct);
            });
        }
        else
        {
            // InMemory en tests: mismo cuerpo sin transaccion (el provider no las soporta).
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

            // 6b-admin) 2026-06-24: si el Admin se auto-autorizo el reversal (salteo el approval
            //     ClientRefundReversal), dejamos el audit AdminSelfAuthorized para el contador. Se emite
            //     DESPUES del SaveChanges del paso 5 (mismo orden seguro que el resto): si el withdrawal no
            //     se persistio, no queda audit huerfano de un bypass que no ocurrio. No hay campo de motivo
            //     en el request, asi que usamos un motivo por defecto explicito + el Reference opcional.
            if (withdrawal.Kind == WithdrawalKind.ReversedToOperator && reversalAdminSelfAuthorized)
            {
                var reversalReason = string.IsNullOrWhiteSpace(request.Reference)
                    ? "Admin auto-autorizo la devolucion de saldo al operador (sin doble firma)."
                    : $"Admin auto-autorizo la devolucion de saldo al operador (sin doble firma). Ref: {request.Reference.Trim()}";

                await _auditService.LogBusinessEventAsync(
                    action: AuditActions.AdminSelfAuthorized,
                    entityName: AuditActions.ClientCreditEntryEntityName,
                    entityId: entry.PublicId.ToString(),
                    details: JsonSerializer.Serialize(new
                    {
                        bypassedGate = "ClientRefundReversalApproval",
                        entryPublicId = entry.PublicId,
                        customerPublicId = entry.Customer.PublicId,
                        withdrawalPublicId = withdrawal.PublicId,
                        reason = reversalReason,
                        amount = withdrawal.Amount,
                        currency = entry.Currency,
                        selfAuthorizedByUserId = userId,
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

        // 2) Solo se revierte una APLICACION (a otra reserva O a una multa: ambas comparten el mismo Kind
        //    AppliedToNewBooking, ver ApplyCreditToPenaltyCoreOnEntries). Los otros kinds (efectivo,
        //    transferencia, KeptAsCredit, ReversedToOperator) tienen sus propios flujos de anulacion; no se
        //    mezclan aca. ValidationException -> el controller la traduce a 400 (request mal dirigido), no a 409.
        //    Cosmetico (fix post-review Tanda D1): el mensaje NO debe asumir "a otra reserva" (tambien se usa
        //    para multas) ni exponer el nombre interno del Kind en ingles.
        if (withdrawal.Kind != WithdrawalKind.AppliedToNewBooking)
        {
            throw new System.ComponentModel.DataAnnotations.ValidationException(
                "Este retiro no corresponde a una aplicación de saldo a favor (a otra reserva o a una multa), " +
                "asi que no se puede revertir por acá.");
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
                $"Ley 25.345: no se puede retirar en efectivo más de ${CurrencyDisplayFormat.Amount(settings.Ley25345ThresholdAmount)}. " +
                $"Se intentó retirar ${CurrencyDisplayFormat.Amount(request.Amount)}. Usá transferencia bancaria en su lugar.",
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
                "No se puede aplicar un saldo a favor a una reserva que no está en gestión de cobro. " +
                "Pasala a En gestión primero.",
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
                $"El monto a aplicar (${CurrencyDisplayFormat.Amount(appliedAmount)} {creditCurrency}) supera la deuda de la reserva " +
                $"destino en esa moneda (${CurrencyDisplayFormat.Amount(targetDebtInCreditCurrency)}). Aplica como mucho la deuda.",
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
    private async Task<(ClientCreditWithdrawal withdrawal, int? targetReservaIdToRecalc, bool adminSelfAuthorized)> HandleReversedToOperatorAsync(
        ClientCreditEntry entry,
        WithdrawClientCreditRequest request,
        string userId,
        string? userName,
        bool requesterIsAdmin,
        CancellationToken ct)
    {
        // 2026-06-24: BYPASS Admin. Hoy el dueno es el unico Admin y pedirse a si mismo un approval
        // ClientRefundReversal es teatro (se auto-aprobaba). Cuando el actor es Admin, NO exigimos el
        // approval: el Admin se auto-autoriza y mas arriba (WithdrawAsync paso 6b) queda el audit
        // AdminSelfAuthorized. Condicionado SOLO al rol Admin: el dia que existan varios admins que no
        // sean el dueno, se puede volver a exigir el approval por policy (la maquinaria sigue intacta abajo).
        if (requesterIsAdmin)
        {
            // Construir withdrawal + ManualCashMovement Income SIN approval consumido (no hubo approval).
            // El ApprovalRequestId queda null en el withdrawal. El motivo del bypass se documenta en el
            // audit AdminSelfAuthorized (no hay campo de motivo en WithdrawClientCreditRequest: usamos un
            // default razonable mas un Reference opcional si el Admin lo cargo).
            var adminWithdrawal = BuildWithdrawalAndMovement(
                entry,
                request,
                WithdrawalKind.ReversedToOperator,
                userId,
                userName,
                approvalRequestId: null);

            return (adminWithdrawal, null, true);
        }

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

        // No toca otra reserva -> segundo valor null. No fue bypass de Admin -> tercer valor false.
        return (withdrawal, null, false);
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
    // FC4 — flujo de CUENTA DEL CLIENTE (espejo del lado operador SupplierCreditService).
    //
    // Ver / APLICAR / REVERTIR saldo a favor del cliente a otra reserva del MISMO cliente, a nivel cliente
    // (FIFO por antiguedad), sin elegir bolsillo a mano. REUSA el mismo modelo que el flujo por-bolsillo:
    // el "retiro" es un ClientCreditWithdrawal(AppliedToNewBooking) y el efecto en la reserva destino es el
    // Payment puente de AppliedCreditBridge (no inventamos otro modelo). La diferencia es que aca el drenaje
    // recorre VARIOS bolsillos en una sola operacion atomica, con retry por concurrencia (xmin del bolsillo).
    // =========================================================================

    public async Task<ClientCreditOverviewDto> GetCustomerCreditAsync(int customerId, CancellationToken ct)
    {
        var ownerScope = await GetTargetReservaOwnerScopeOrNullAsync(ct);
        var customer = await _db.Customers
            .AsNoTracking()
            .Where(c => c.Id == customerId)
            .Select(c => new { c.PublicId, c.FullName })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Cliente no encontrado");

        // Solo bolsillos con saldo disponible (RemainingBalance > 0): es lo que se puede aplicar.
        var entriesQuery = _db.ClientCreditEntries
            .AsNoTracking()
            .Where(e => e.CustomerId == customerId && e.RemainingBalance > 0m);
        if (ownerScope is not null)
        {
            entriesQuery = entriesQuery.Where(e =>
                (e.SourceReserva != null && e.SourceReserva.ResponsibleUserId == ownerScope)
                || (e.BookingCancellation != null && e.BookingCancellation.Reserva.ResponsibleUserId == ownerScope)
                || (e.SourceDebitNoteAnnulment != null
                    && e.SourceDebitNoteAnnulment.BookingCancellation.Reserva.ResponsibleUserId == ownerScope));
        }
        var entries = await entriesQuery
            .Select(e => new { e.PublicId, e.Currency, e.CreditedAmount, e.RemainingBalance, e.CreatedAt })
            .ToListAsync(ct);

        var dto = new ClientCreditOverviewDto
        {
            CustomerPublicId = customer.PublicId,
            CustomerName = customer.FullName,
        };

        // Agrupamos por moneda (ARS/USD nunca se mezclan).
        var byCurrency = entries
            .GroupBy(e => Monedas.Normalizar(e.Currency))
            .OrderBy(g => g.Key);

        foreach (var group in byCurrency)
        {
            dto.Currencies.Add(new ClientCreditCurrencyLineDto
            {
                Currency = group.Key,
                AvailableBalance = group.Sum(e => e.RemainingBalance),
                Entries = group
                    .OrderBy(e => e.CreatedAt)
                    .Select(e => new ClientCreditEntryLineDto
                    {
                        PublicId = e.PublicId,
                        CreditedAmount = e.CreditedAmount,
                        RemainingBalance = e.RemainingBalance,
                        CreatedAt = e.CreatedAt,
                    })
                    .ToList(),
            });
        }

        // Aplicaciones VIVAS de saldo a favor: cada Payment puente activo (!IsDeleted) atado a un retiro
        // AppliedToNewBooking de un bolsillo de ESTE cliente. Una sola query (proyeccion) para no disparar
        // N+1: trae directo el numero de la reserva destino. Por INV-093 la reserva destino siempre es del
        // MISMO cliente, asi que el titular es el propio cliente. El front muestra estas filas en el extracto
        // con un boton "Revertir" por cada una (revierte por su ApplicationPublicId). Asi un apply que drenó N
        // bolsillos queda como N filas independientes.
        //
        // Tanda D1 (2026-07-16): AHORA acepta DOS Methods — el puente "aplicado a otra reserva" (BridgeMethod)
        // Y el puente "aplicado a una multa" (PenaltyBridgeMethod, con LinkedInvoiceId != null apuntando a la
        // ND). p.Reserva sigue siendo la reserva ANULADA dueña de la multa en ese segundo caso (el puente vive
        // ahi), asi que TargetReservaPublicId/Number siguen siendo correctos sin cambios; solo se suma
        // DestinationKind + el numero de la ND para que el front etiquete cada fila correctamente.
        var applicationQuery = _db.Payments
            .AsNoTracking()
            .Where(p => (p.Method == AppliedCreditBridge.BridgeMethod || p.Method == AppliedCreditBridge.PenaltyBridgeMethod)
                     && !p.AffectsCash
                     && !p.IsDeleted
                     && p.AppliedFromCreditWithdrawalId != null
                     && p.AppliedFromCreditWithdrawal!.Entry.CustomerId == customerId);
        if (ownerScope is not null)
        {
            applicationQuery = applicationQuery.Where(p =>
                p.Reserva != null && p.Reserva.ResponsibleUserId == ownerScope
                && ((p.AppliedFromCreditWithdrawal!.Entry.SourceReserva != null
                        && p.AppliedFromCreditWithdrawal.Entry.SourceReserva.ResponsibleUserId == ownerScope)
                    || (p.AppliedFromCreditWithdrawal.Entry.BookingCancellation != null
                        && p.AppliedFromCreditWithdrawal.Entry.BookingCancellation.Reserva.ResponsibleUserId == ownerScope)
                    || (p.AppliedFromCreditWithdrawal.Entry.SourceDebitNoteAnnulment != null
                        && p.AppliedFromCreditWithdrawal.Entry.SourceDebitNoteAnnulment.BookingCancellation.Reserva.ResponsibleUserId == ownerScope)));
        }
        var applicationRows = await applicationQuery
            .Select(p => new
            {
                ApplicationPublicId = p.AppliedFromCreditWithdrawal!.PublicId,
                EntryPublicId = p.AppliedFromCreditWithdrawal!.Entry.PublicId,
                p.Currency,
                p.Amount,
                AppliedAt = p.PaidAt,
                p.Method,
                p.LinkedInvoiceId,
                TargetReservaPublicId = p.Reserva != null ? p.Reserva.PublicId : Guid.Empty,
                TargetReservaNumber = p.Reserva != null ? p.Reserva.NumeroReserva : null,
            })
            .ToListAsync(ct);

        // Para las filas de destino "multa" (LinkedInvoiceId != null), resolvemos el numero legible de la ND
        // en UNA query batcheada (sin N+1).
        var debitNoteIdsInApplications = applicationRows
            .Where(r => r.LinkedInvoiceId.HasValue)
            .Select(r => r.LinkedInvoiceId!.Value)
            .Distinct()
            .ToList();
        var debitNoteDisplayById = debitNoteIdsInApplications.Count == 0
            ? new Dictionary<int, (Guid PublicId, string Display)>()
            : await _db.Invoices
                .AsNoTracking()
                .Where(i => debitNoteIdsInApplications.Contains(i.Id))
                .Select(i => new { i.Id, i.PublicId, i.PuntoDeVenta, i.NumeroComprobante })
                .ToDictionaryAsync(
                    i => i.Id,
                    i => (PublicId: i.PublicId, Display: $"{i.PuntoDeVenta:D5}-{i.NumeroComprobante:D8}"),
                    ct);

        dto.ActiveApplications = applicationRows
            .OrderByDescending(r => r.AppliedAt)
            .Select(r =>
            {
                bool isPenalty = r.Method == AppliedCreditBridge.PenaltyBridgeMethod && r.LinkedInvoiceId.HasValue;
                debitNoteDisplayById.TryGetValue(r.LinkedInvoiceId ?? -1, out var debitNoteDisplay);

                return new ClientCreditApplicationLineDto
                {
                    ApplicationPublicId = r.ApplicationPublicId,
                    EntryPublicId = r.EntryPublicId,
                    Currency = Monedas.Normalizar(r.Currency),
                    Amount = r.Amount,
                    TargetReservaPublicId = r.TargetReservaPublicId,
                    TargetReservaNumber = r.TargetReservaNumber,
                    TargetReservaHolderName = customer.FullName, // INV-093: el titular del destino es este mismo cliente
                    AppliedAt = r.AppliedAt,
                    DestinationKind = isPenalty
                        ? ClientCreditApplicationDestinationKind.Penalty
                        : ClientCreditApplicationDestinationKind.Reserva,
                    DebitNotePublicId = isPenalty ? debitNoteDisplay.PublicId : null,
                    DebitNoteDisplayNumber = isPenalty ? debitNoteDisplay.Display : null,
                };
            })
            .ToList();

        // A DIFERENCIA del lado operador: NO se enmascara por cobranzas.see_cost. El saldo a favor del cliente
        // es plata del cliente (lado venta/cobranza), no un costo de la agencia. Coherente con /available-credit
        // y con la cuenta del cliente, que tampoco enmascaran montos.
        return dto;
    }

    // =========================================================================
    // ApplyCustomerCreditAsync (con retry de concurrencia)
    // =========================================================================

    public async Task<ClientCreditApplicationResultDto> ApplyCustomerCreditAsync(
        int customerId,
        ApplyClientCreditRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        if (request.Amount <= 0m)
        {
            throw new ArgumentException("El monto a aplicar debe ser mayor a 0.", nameof(request));
        }
        if (!Monedas.EsSoportada(request.Currency))
        {
            throw new ArgumentException("Moneda no soportada.", nameof(request));
        }

        // Mismo gate de feature flag que el resto de las escrituras del modulo.
        await EnsureFeatureFlagOnAsync(ct);

        for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            try
            {
                return await TryApplyCustomerCreditOnceAsync(customerId, request, userId, userName, ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogWarning(ex,
                    "ApplyCustomerCreditAsync concurrency conflict on attempt {Attempt}/{Max} for customer {CustomerId}. Retrying.",
                    attempt + 1, MaxConcurrencyRetries, customerId);

                _db.ChangeTracker.Clear();
                if (attempt == MaxConcurrencyRetries - 1) throw;
                await Task.Delay((int)Math.Pow(4, attempt) * 100, ct);
            }
        }

        throw new InvalidOperationException("ApplyCustomerCreditAsync retry loop exhausted sin resultado.");
    }

    private async Task<ClientCreditApplicationResultDto> TryApplyCustomerCreditOnceAsync(
        int customerId,
        ApplyClientCreditRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        string currency = Monedas.Normalizar(request.Currency);
        decimal amount = ReservationEconomicPolicy.RoundCurrency(request.Amount);

        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == customerId, ct)
            ?? throw new KeyNotFoundException("Cliente no encontrado");

        // Reserva destino con su grafo economico (tracked: usamos su Id para los puentes y calculamos su deuda).
        var targetReserva = await LoadReservaWithEconomicGraphAsync(request.TargetReservaPublicId, ct)
            ?? throw new KeyNotFoundException("Reserva destino no encontrada");

        // (c) MISMO cliente. El saldo del cliente A no se aplica a una reserva del cliente B.
        if (targetReserva.PayerId != customerId)
        {
            throw new BusinessInvariantViolationException(
                "La reserva destino no pertenece al mismo cliente del saldo a favor. " +
                "No se puede aplicar saldo de un cliente a la reserva de otro.",
                invariantCode: "INV-093");
        }

        // Ownership de la reserva DESTINO (mismo principio que el alta de pago normal y el flujo por-bolsillo):
        // un vendedor con scope acotado solo puede aplicar saldo a una reserva a su cargo. Sin esto, este
        // endpoint a nivel cliente seria una puerta lateral que evade la restriccion del path por-bolsillo.
        var ownerScope = await GetTargetReservaOwnerScopeOrNullAsync(ct);
        if (ownerScope is not null
            && !string.Equals(targetReserva.ResponsibleUserId, ownerScope, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "La reserva destino no esta asignada al usuario actual. No se puede aplicar saldo a una " +
                "reserva a cargo de otro vendedor.");
        }

        // Estado VENTA FIRME (incluye Closed con deuda, ADR-033). No se aplica saldo a un Presupuesto/Cotizacion/
        // Perdida/Cancelada. Mismo invariantCode INV-096 que el flujo por-bolsillo.
        if (!EstadoReserva.IsSaleFirmStatus(targetReserva.Status))
        {
            throw new BusinessInvariantViolationException(
                "No se puede aplicar un saldo a favor a una reserva que no está en gestión de cobro. " +
                "Pasala a En gestión primero.",
                invariantCode: "INV-096");
        }

        // (b) Deuda exigible del destino EN LA MONEDA DEL SALDO. 0 si esa moneda no aparece o ya esta saldada.
        // Esto bloquea de hecho el cruce de monedas: un saldo USD contra una reserva que solo debe ARS da 0.
        decimal targetDebtInCurrency = GetReservaConfirmedBalanceForCurrency(targetReserva, currency);
        if (targetDebtInCurrency <= 0m)
        {
            throw new BusinessInvariantViolationException(
                $"No se puede aplicar un saldo a favor en {currency} a una reserva que no tiene deuda en {currency} " +
                "(el saldo a favor solo se aplica a una deuda de la misma moneda; ARS nunca toca USD).",
                invariantCode: "INV-095");
        }

        // (a) Pool disponible en esa moneda. El saldo a favor del cliente es un ledger de PRIMERA CLASE: el
        // RemainingBalance se decrementa atomicamente en cada retiro y NUNCA se re-deriva de otra proyeccion.
        // Por eso (a diferencia del lado operador) NO hace falta topear contra un "sobrepago derivado fresco":
        // Σ RemainingBalance YA es el saldo real. FIFO por antiguedad (CreatedAt, desempate por Id).
        var entries = await _db.ClientCreditEntries
            .Where(e => e.CustomerId == customerId && e.RemainingBalance > 0m)
            .ToListAsync(ct);
        var currencyEntries = entries
            .Where(e => Monedas.Normalizar(e.Currency) == currency)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .ToList();

        decimal pool = currencyEntries.Sum(e => e.RemainingBalance);

        // SEGURIDAD: los mensajes de tope NO revelan la cifra disponible ni la deuda (el endpoint ya esta
        // gateado por cobranzas.edit). El CHECK SQL del saldo no-negativo es la red dura bajo concurrencia.
        if (amount > pool)
        {
            throw new BusinessInvariantViolationException(
                "El monto a aplicar supera el saldo a favor disponible del cliente en esa moneda.",
                invariantCode: "INV-085");
        }

        // (d) No sobre-aplicar: el monto no puede superar la deuda viva del destino en esa moneda, para no dejar
        // la reserva destino con saldo a favor atrapado.
        if (amount > targetDebtInCurrency)
        {
            throw new BusinessInvariantViolationException(
                "El monto a aplicar supera la deuda de la reserva destino en esa moneda.",
                invariantCode: "INV-097");
        }

        // Drenar FIFO. Por cada bolsillo tocado: 1 ClientCreditWithdrawal(AppliedToNewBooking) + 1 Payment puente
        // positivo (no mueve caja, baja la deuda del destino). Cada retiro es reversible de forma independiente.
        decimal remainingToApply = amount;
        ClientCreditWithdrawal? firstWithdrawal = null;
        ClientCreditEntry? firstEntry = null;

        foreach (var entry in currencyEntries)
        {
            if (remainingToApply <= 0m) break;

            decimal take = Math.Min(entry.RemainingBalance, remainingToApply);
            entry.RemainingBalance = ReservationEconomicPolicy.RoundCurrency(entry.RemainingBalance - take);
            if (entry.RemainingBalance <= 0m)
            {
                entry.RemainingBalance = 0m;
                entry.IsFullyConsumed = true;
            }
            remainingToApply = ReservationEconomicPolicy.RoundCurrency(remainingToApply - take);

            var withdrawal = new ClientCreditWithdrawal
            {
                ClientCreditEntryId = entry.Id,
                Entry = entry,
                Kind = WithdrawalKind.AppliedToNewBooking,
                Amount = take,
                ExecutedAt = DateTime.UtcNow,
                ExecutedByUserId = userId,
                ExecutedByUserName = userName ?? string.Empty,
                ManualCashMovementId = null,
                ApprovalRequestId = null,
            };
            _db.ClientCreditWithdrawals.Add(withdrawal);
            firstWithdrawal ??= withdrawal;
            firstEntry ??= entry;

            // Payment puente POSITIVO en la reserva destino. La FK al withdrawal se ata por NAVIGATION property
            // (withdrawal.Id es 0 hasta el SaveChanges; EF resuelve la FK en orden topologico).
            var bridge = new Payment
            {
                ReservaId = targetReserva.Id,
                Amount = take,                                  // POSITIVO (baja la deuda)
                Currency = currency,                            // moneda del bolsillo
                ImputedCurrency = null,                         // same-currency: se imputa a su propia moneda
                Method = AppliedCreditBridge.BridgeMethod,      // "SaldoAFavorAplicado"
                AffectsCash = false,                            // la plata ya entro antes; NO mueve caja
                EntryType = PaymentEntryTypes.Payment,
                Status = "Paid",
                PaidAt = DateTime.UtcNow,
                Notes = $"Saldo a favor aplicado (bolsillo {entry.PublicId}).",
                CreatedByUserId = userId,
                CreatedByUserName = userName,
                AppliedFromCreditWithdrawal = withdrawal,
            };
            _db.Payments.Add(bridge);

            // Auditoria STAGED (no LogBusinessEventAsync): entra en la MISMA SaveChanges que el retiro + puente.
            _auditService.StageBusinessEvent(
                action: AuditActions.ClientCreditApplied,
                entityName: AuditActions.ClientCreditWithdrawalEntityName,
                entityId: withdrawal.PublicId.ToString(),
                details: JsonSerializer.Serialize(new
                {
                    withdrawalPublicId = withdrawal.PublicId,
                    entryPublicId = entry.PublicId,
                    customerPublicId = customer.PublicId,
                    currency,
                    amount = take,
                    targetReservaPublicId = targetReserva.PublicId,
                }),
                userId: userId,
                userName: userName);
        }

        // Persistencia atomica: retiros + puentes + auditoria staged en UN SaveChanges, y el recalculo de la
        // deuda del destino (que hace su propia SaveChanges) dentro de la MISMA transaccion envolvente. El xmin
        // de cada bolsillo detecta si otro apply lo movio en paralelo -> DbUpdateConcurrencyException -> retry.
        await PersistApplyAndRecalcAsync(targetReserva.Id, ct);

        decimal availableAfter = await GetCustomerAvailableBalanceAsync(customerId, currency, ct);

        _logger.LogInformation(
            "metric:client_credit_applied | CustomerId={CustomerId} Currency={Currency} Amount={Amount} TargetReservaId={TargetReservaId}",
            customerId, currency, amount, targetReserva.Id);

        return new ClientCreditApplicationResultDto
        {
            ApplicationPublicId = firstWithdrawal!.PublicId,
            EntryPublicId = firstEntry!.PublicId,
            Currency = currency,
            Amount = amount,
            TargetReservaPublicId = targetReserva.PublicId,
            IsReversal = false,
            AvailableBalanceAfter = availableAfter,
        };
    }

    // =========================================================================
    // ReverseCustomerCreditApplicationAsync (con retry de concurrencia)
    // =========================================================================

    public async Task<ClientCreditApplicationResultDto> ReverseCustomerCreditApplicationAsync(
        int customerId,
        Guid applicationPublicId,
        ReverseClientCreditApplicationRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        // Motivo OPCIONAL (decision del dueño): puede venir null/vacio y la reversa procede igual. Si viene, se
        // audita; si no, se audita sin motivo. NO se exige un minimo de caracteres.
        await EnsureFeatureFlagOnAsync(ct);

        for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            try
            {
                return await TryReverseCustomerCreditOnceAsync(customerId, applicationPublicId, request, userId, userName, ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogWarning(ex,
                    "ReverseCustomerCreditApplicationAsync concurrency conflict on attempt {Attempt}/{Max} for application {ApplicationPublicId}. Retrying.",
                    attempt + 1, MaxConcurrencyRetries, applicationPublicId);

                _db.ChangeTracker.Clear();
                if (attempt == MaxConcurrencyRetries - 1) throw;
                await Task.Delay((int)Math.Pow(4, attempt) * 100, ct);
            }
        }

        throw new InvalidOperationException("ReverseCustomerCreditApplicationAsync retry loop exhausted sin resultado.");
    }

    private async Task<ClientCreditApplicationResultDto> TryReverseCustomerCreditOnceAsync(
        int customerId,
        Guid applicationPublicId,
        ReverseClientCreditApplicationRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        // El "applicationPublicId" es el PublicId del retiro AppliedToNewBooking. Tracked: vamos a re-incrementar
        // el bolsillo.
        var withdrawal = await _db.ClientCreditWithdrawals
            .Include(w => w.Entry).ThenInclude(e => e.Customer)
            .FirstOrDefaultAsync(w => w.PublicId == applicationPublicId, ct)
            ?? throw new KeyNotFoundException("Aplicacion de saldo a favor no encontrada");

        // La aplicacion tiene que pertenecer a ESTE cliente (su bolsillo es del customer de la ruta).
        if (withdrawal.Entry.CustomerId != customerId)
        {
            throw new KeyNotFoundException("Aplicacion de saldo a favor no encontrada");
        }

        // Solo se revierte una APLICACION (a otra reserva O a una multa: ambas comparten el mismo Kind
        // AppliedToNewBooking). Los otros kinds (efectivo, transferencia, etc.) tienen sus propios flujos.
        // Mismo criterio (409) que el lado operador. Cosmetico (fix post-review Tanda D1): el mensaje NO debe
        // asumir "a otra reserva" (esta misma ruta reversa tambien aplicaciones contra multas).
        if (withdrawal.Kind != WithdrawalKind.AppliedToNewBooking)
        {
            throw new BusinessInvariantViolationException(
                "Este retiro no corresponde a una aplicación de saldo a favor (a otra reserva o a una multa), " +
                "asi que no se puede revertir por acá.",
                invariantCode: "INV-CLICREDIT-005");
        }

        var entry = withdrawal.Entry;
        string currency = Monedas.Normalizar(entry.Currency);

        // Puente VIVO atado a este retiro: su existencia es la fuente de verdad de "esta aplicacion sigue activa".
        var liveBridge = await AppliedCreditBridge.FindLiveBridgeAsync(_db, withdrawal.Id, ct);

        // Guardas de integridad (anti doble-reversa + tope superior del bolsillo). Si bloquea, abortamos SIN mutar.
        var blockReason = AppliedCreditBridge.GetReverseBlockReason(entry, liveBridge);
        if (blockReason is not null)
        {
            throw new BusinessInvariantViolationException(blockReason, invariantCode: "INV-098");
        }

        var targetReservaId = liveBridge!.ReservaId
            ?? throw new InvalidOperationException(
                "El pago puente de saldo a favor aplicado no tiene reserva destino. Estado inconsistente.");

        // PublicId + responsable del destino (lo leemos antes de mutar). El responsable lo usa la guarda de
        // ownership de abajo.
        var targetReservaInfo = await _db.Reservas
            .AsNoTracking()
            .Where(r => r.Id == targetReservaId)
            .Select(r => new { r.PublicId, r.ResponsibleUserId })
            .FirstOrDefaultAsync(ct);
        var targetReservaPublicId = targetReservaInfo?.PublicId ?? Guid.Empty;

        // B1 (review seguridad): ownership de la reserva DESTINO. La reversa MUTA la deuda de esa reserva, asi
        // que un vendedor con scope acotado solo puede revertir sobre una reserva a su cargo (a menos que vea
        // todas las cobranzas). Simetrico con el apply (GetTargetReservaOwnerScopeOrNullAsync) y con el viejo
        // ReverseAppliedCreditAsync (paso 4b). Sin HttpContext (tests/legacy) el scope es null y no se filtra.
        // UnauthorizedAccessException -> el controller la traduce a 403.
        var ownerScope = await GetTargetReservaOwnerScopeOrNullAsync(ct);
        if (ownerScope is not null
            && !string.Equals(targetReservaInfo?.ResponsibleUserId, ownerScope, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "La reserva destino no esta asignada al usuario actual. No se puede revertir una aplicacion de " +
                "saldo sobre una reserva a cargo de otro vendedor.");
        }

        // Reversa sobre las entidades ya cargadas (sin SaveChanges): soft-delete del puente + re-incremento del
        // bolsillo (y recalculo de IsFullyConsumed dentro de ReverseArtifacts).
        decimal amountReturnedToPocket = AppliedCreditBridge.ReverseArtifacts(entry, liveBridge);

        // Auditoria STAGED (misma SaveChanges). El motivo es OPCIONAL: si vino, lo normalizamos; si no, queda null.
        var auditReason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        _auditService.StageBusinessEvent(
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
                currency,
                remainingBalanceAfter = entry.RemainingBalance,
                reason = auditReason,
            }),
            userId: userId,
            userName: userName);

        // Persistencia atomica: soft-delete del puente + re-incremento del bolsillo + auditoria staged en un
        // SaveChanges, y el recalculo de la deuda del destino en la misma transaccion envolvente.
        await PersistApplyAndRecalcAsync(targetReservaId, ct);

        decimal availableAfter = await GetCustomerAvailableBalanceAsync(customerId, currency, ct);

        _logger.LogInformation(
            "metric:client_credit_application_reversed | CustomerId={CustomerId} Currency={Currency} Amount={Amount} TargetReservaId={TargetReservaId}",
            customerId, currency, amountReturnedToPocket, targetReservaId);

        return new ClientCreditApplicationResultDto
        {
            ApplicationPublicId = withdrawal.PublicId,
            EntryPublicId = entry.PublicId,
            Currency = currency,
            Amount = amountReturnedToPocket,
            TargetReservaPublicId = targetReservaPublicId,
            IsReversal = true,
            AvailableBalanceAfter = availableAfter,
        };
    }

    /// <summary>
    /// FC4: persiste lo ya armado en el ChangeTracker (retiros + puentes + auditoria staged, O el soft-delete del
    /// puente + re-incremento del bolsillo) y recalcula la deuda de la reserva destino, TODO atomico. Contra un
    /// provider RELACIONAL usa una transaccion envolvente para que la SaveChanges principal y la del persister
    /// (que hace su propia SaveChanges) commiteen juntas; en InMemory (tests) corre el mismo cuerpo sin
    /// transaccion (el provider no las soporta). Replica el patron de <see cref="WithdrawAsync"/>.
    /// </summary>
    private async Task PersistApplyAndRecalcAsync(int targetReservaId, CancellationToken ct)
    {
        if (_db.Database.IsRelational())
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _db.Database.BeginTransactionAsync(ct);
                await _db.SaveChangesAsync(ct);
                await ReservaMoneyPersister.PersistAsync(_db, targetReservaId, ct);
                await transaction.CommitAsync(ct);
            });
        }
        else
        {
            await _db.SaveChangesAsync(ct);
            await ReservaMoneyPersister.PersistAsync(_db, targetReservaId, ct);
        }
    }

    // =========================================================================
    // Tanda D1 (2026-07-16): saldo a favor del cliente APLICADO CONTRA UNA MULTA + neteo automatico al
    // devolver. Reusa el mismo modelo de puente que la aplicacion a otra reserva (ClientCreditWithdrawal +
    // Payment puente), con dos diferencias: el Payment lleva LinkedInvoiceId=la ND (el enganche que hace que
    // DebitNoteOutstandingLookup lo cuente como cobrado) y AffectsReservaBalance=false (no toca el saldo
    // operativo de la reserva ya anulada, igual que un cobro real de multa).
    //
    // DISCIPLINA DE RETRY (por que TODO — lecturas frescas Y Add() — vive DENTRO del closure reintentable):
    // el patron histórico del modulo (WithdrawAsync/TryApplyCustomerCreditOnceAsync/PaymentService.CreatePaymentAsync)
    // arma las entidades UNA vez, FUERA del bloque que reintenta, porque ahi solo hay UNA entidad. Aca el neteo
    // puede tocar VARIAS Notas de Debito en un solo pedido (un Add() por cada una); si el Add() quedara fuera
    // del closure y Npgsql reintentara la transaccion entera ante un error transitorio, el ChangeTracker
    // acumularia entidades DUPLICADAS de la corrida anterior (fila de pago repetida = plata fantasma). Por eso
    // el closure reintentable arranca con _db.ChangeTracker.Clear() y hace TODO adentro (lecturas frescas +
    // gate + Add() + SaveChanges): cada intento es una unidad limpia y autocontenida, segura de repetir.
    // =========================================================================

    /// <summary>Tanda D1: una multa ABIERTA (ND aprobada con CAE, saldo pendiente &gt; 0) candidata a recibir saldo a favor.</summary>
    private sealed record OpenPenaltyRow(
        int BcId,
        int ReservaId,
        Guid ReservaPublicId,
        string NumeroReserva,
        int DebitNoteId,
        Guid DebitNotePublicId,
        string DebitNoteDisplayNumber,
        decimal ImporteTotal,
        decimal Outstanding,
        DateTime DebitNoteCreatedAt);

    /// <summary>
    /// Tanda D1 (fix N2/N3 post-review): multas FIRMES de un cliente en UNA moneda, mas antigua primero (por
    /// fecha de la ND — FIFO, mismo criterio de antiguedad que el resto del modulo). Reusa la MISMA rama de
    /// <c>CancellationPenaltyRules.LiveDebitNotePredicate</c> que ya usa la bandeja de multas del cliente
    /// (solo "ND emitida y no anulada": una multa sin ND todavia, o en revision, no es un open item cobrable) +
    /// <c>DebitNoteOutstandingLookup</c>/<c>DebitNoteOutstandingRules</c> — la MISMA fuente que el cartel de la
    /// ficha, para que "cuanto debe" nunca diverja entre pantallas.
    ///
    /// <para><b>Regla del dueño ("el neteo descuenta TODO lo que debe")</b>: a proposito SIN filtro por
    /// vendedor responsable NI por estado de la reserva. Es deuda DEL CLIENTE, no del vendedor — filtrar por
    /// vendedor haria que el neteo devuelva de mas si el cliente tiene una multa en una reserva de OTRO
    /// vendedor. Tampoco filtra por reserva Cancelled/PendingOperatorRefund: una cancelacion PARCIAL (ADR-025)
    /// puede dejar una ND firme sobre una reserva que sigue VIVA, y esa deuda tambien hay que netearla. La
    /// autorizacion de ownership se valida en el ENDPOINT (cobranzas.edit), no aca.</para>
    /// </summary>
    private async Task<List<OpenPenaltyRow>> LoadOpenPenaltiesForCustomerAsync(
        int customerId, string currency, CancellationToken ct)
    {
        var normalizedCurrency = Monedas.Normalizar(currency);

        // Solo la rama "ND VIVA emitida" (ver el XML-doc de CancellationPenaltyRules): una multa Issuing o
        // UnderReview no tiene comprobante fiscal aprobado, asi que no es un open item cobrable todavia. El
        // join directo contra bc.Reserva.PayerId reemplaza el filtro previo por estado de reserva.
        var liveRows = await _db.BookingCancellations
            .AsNoTracking()
            .Where(bc => bc.Reserva.PayerId == customerId
                && bc.DebitNoteStatus == DebitNoteStatus.Issued
                && bc.DebitNoteInvoiceId != null
                && bc.DebitNoteInvoice != null
                && bc.DebitNoteInvoice.AnnulmentStatus != AnnulmentStatus.Succeeded)
            .Select(bc => new
            {
                bc.Id,
                bc.ReservaId,
                bc.DebitNoteInvoiceId,
                ReservaPublicId = bc.Reserva.PublicId,
                bc.Reserva.NumeroReserva
            })
            .ToListAsync(ct);
        if (liveRows.Count == 0) return new List<OpenPenaltyRow>();

        // Dedupe por ND (riesgo de seguridad senalado en review): si mas de una BC apuntara a la MISMA ND
        // (por ejemplo, una BC padre y una BC hija de una cancelacion multi-operador compartiendo la misma
        // Nota de Debito), sin este paso el neteo la aplicaria DOS VECES contra el mismo pool del cliente.
        var dedupedRows = liveRows
            .GroupBy(r => r.DebitNoteInvoiceId!.Value)
            .Select(g => g.First())
            .ToList();

        var debitNoteIds = dedupedRows.Select(r => r.DebitNoteInvoiceId!.Value).ToList();
        var debitNotes = await _db.Invoices
            .AsNoTracking()
            .Where(i => debitNoteIds.Contains(i.Id))
            .Select(i => new
            {
                i.Id,
                i.PublicId,
                i.TipoComprobante,
                i.ImporteTotal,
                i.MonId,
                i.PuntoDeVenta,
                i.NumeroComprobante,
                i.Resultado,
                i.CreatedAt
            })
            .ToDictionaryAsync(i => i.Id, ct);

        var creditedByDebitNote = await DebitNoteOutstandingLookup.LoadCreditedAmountsAsync(_db, debitNoteIds, ct);
        var collectedByDebitNote = await DebitNoteOutstandingLookup.LoadCollectedAmountsAsync(_db, debitNoteIds, ct);

        var result = new List<OpenPenaltyRow>();
        foreach (var row in dedupedRows)
        {
            if (!debitNotes.TryGetValue(row.DebitNoteInvoiceId!.Value, out var debitNote)) continue;
            if (debitNote.Resultado != "A" || !InvoiceComprobanteHelpers.IsDebitNote(debitNote.TipoComprobante)) continue;

            var debitNoteCurrency = Monedas.Normalizar(ArcaCurrencyMapper.ToIso(debitNote.MonId));
            if (!string.Equals(debitNoteCurrency, normalizedCurrency, StringComparison.Ordinal)) continue;

            var outstanding = TravelApi.Domain.Reservations.DebitNoteOutstandingRules.ComputeOutstanding(
                debitNote.ImporteTotal,
                creditedByDebitNote.GetValueOrDefault(debitNote.Id),
                collectedByDebitNote.GetValueOrDefault(debitNote.Id));
            if (outstanding <= 0m) continue;

            result.Add(new OpenPenaltyRow(
                BcId: row.Id,
                ReservaId: row.ReservaId,
                ReservaPublicId: row.ReservaPublicId,
                NumeroReserva: row.NumeroReserva,
                DebitNoteId: debitNote.Id,
                DebitNotePublicId: debitNote.PublicId,
                DebitNoteDisplayNumber: $"{debitNote.PuntoDeVenta:D5}-{debitNote.NumeroComprobante:D8}",
                ImporteTotal: debitNote.ImporteTotal,
                Outstanding: outstanding,
                DebitNoteCreatedAt: debitNote.CreatedAt));
        }

        // FIFO por antiguedad de la ND; empate estable por BcId ascendente (para que, si el neteo llega a
        // tocar varias BCs en la misma transaccion, siempre las visite en el MISMO orden entre corridas).
        return result
            .OrderBy(r => r.DebitNoteCreatedAt)
            .ThenBy(r => r.BcId)
            .ToList();
    }

    /// <summary>Tanda D1: re-lee credited/collected FRESCOS de una ND puntual y recalcula su pendiente. Solo lecturas.</summary>
    private async Task<decimal> RecomputeFreshOutstandingAsync(int debitNoteId, decimal importeTotal, CancellationToken ct)
    {
        var ids = new List<int> { debitNoteId };
        var credited = await DebitNoteOutstandingLookup.LoadCreditedAmountsAsync(_db, ids, ct);
        var collected = await DebitNoteOutstandingLookup.LoadCollectedAmountsAsync(_db, ids, ct);
        return TravelApi.Domain.Reservations.DebitNoteOutstandingRules.ComputeOutstanding(
            importeTotal, credited.GetValueOrDefault(debitNoteId), collected.GetValueOrDefault(debitNoteId));
    }

    /// <summary>Tanda D1: bolsillos del cliente con saldo, en UNA moneda, TRACKEADOS (se van a mutar), orden FIFO.</summary>
    private async Task<List<ClientCreditEntry>> LoadFifoCreditEntriesAsync(int customerId, string currency, CancellationToken ct)
    {
        var entries = await _db.ClientCreditEntries
            .Where(e => e.CustomerId == customerId && e.RemainingBalance > 0m)
            .ToListAsync(ct);
        return entries
            .Where(e => Monedas.Normalizar(e.Currency) == currency)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .ToList();
    }

    /// <summary>
    /// Tanda D1: NUCLEO sincronico (sin I/O) de "aplicar <paramref name="amountToApply"/> contra UNA ND",
    /// drenando <paramref name="currencyEntriesFifo"/> — una lista YA CARGADA y TRACKEADA que el caller puede
    /// reusar entre VARIAS llamadas (el neteo aplica contra N NDs con el MISMO pool de bolsillos, mutandolo en
    /// memoria a medida que cada ND consume su parte; no se puede re-consultar la base a mitad de camino
    /// porque nada esta guardado todavia). Arma (Add, sin SaveChanges) el/los <see cref="ClientCreditWithdrawal"/>
    /// + Payment puente + auditoria STAGED. Tira <see cref="BusinessInvariantViolationException"/> INV-085 si
    /// <paramref name="amountToApply"/> supera lo que queda en el pool.
    /// </summary>
    private (ClientCreditWithdrawal FirstWithdrawal, ClientCreditEntry FirstEntry) ApplyCreditToPenaltyCoreOnEntries(
        List<ClientCreditEntry> currencyEntriesFifo,
        Customer customer,
        string currency,
        decimal amountToApply,
        int debitNoteId,
        Guid debitNotePublicId,
        int reservaId,
        Guid reservaPublicId,
        string userId,
        string? userName)
    {
        decimal pool = currencyEntriesFifo.Sum(e => e.RemainingBalance);
        if (amountToApply > pool)
        {
            throw new BusinessInvariantViolationException(
                "El monto a aplicar supera el saldo a favor disponible del cliente en esa moneda.",
                invariantCode: "INV-085");
        }

        decimal remainingToApply = amountToApply;
        ClientCreditWithdrawal? firstWithdrawal = null;
        ClientCreditEntry? firstEntry = null;

        foreach (var entry in currencyEntriesFifo)
        {
            if (remainingToApply <= 0m) break;
            if (entry.RemainingBalance <= 0m) continue; // ya agotado por una aplicacion previa de ESTE MISMO pedido

            decimal take = Math.Min(entry.RemainingBalance, remainingToApply);
            entry.RemainingBalance = ReservationEconomicPolicy.RoundCurrency(entry.RemainingBalance - take);
            if (entry.RemainingBalance <= 0m)
            {
                entry.RemainingBalance = 0m;
                entry.IsFullyConsumed = true;
            }
            remainingToApply = ReservationEconomicPolicy.RoundCurrency(remainingToApply - take);

            var withdrawal = new ClientCreditWithdrawal
            {
                ClientCreditEntryId = entry.Id,
                Entry = entry,
                Kind = WithdrawalKind.AppliedToNewBooking,
                Amount = take,
                ExecutedAt = DateTime.UtcNow,
                ExecutedByUserId = userId,
                ExecutedByUserName = userName ?? string.Empty,
                ManualCashMovementId = null,
                ApprovalRequestId = null,
            };
            _db.ClientCreditWithdrawals.Add(withdrawal);
            firstWithdrawal ??= withdrawal;
            firstEntry ??= entry;

            // Payment puente POSITIVO: NO mueve caja, y a diferencia del puente de "aplicado a otra reserva"
            // lleva LinkedInvoiceId=la ND (el enganche con DebitNoteOutstandingLookup) y
            // AffectsReservaBalance=false (no infla ni desinfla el saldo OPERATIVO de la reserva ya anulada,
            // igual que un cobro real de multa vigente desde 44fcea6).
            var bridge = new Payment
            {
                ReservaId = reservaId,
                LinkedInvoiceId = debitNoteId,
                Amount = take,
                Currency = currency,
                ImputedCurrency = null,
                Method = AppliedCreditBridge.PenaltyBridgeMethod,
                AffectsCash = false,
                AffectsReservaBalance = false,
                EntryType = PaymentEntryTypes.Payment,
                Status = "Paid",
                PaidAt = DateTime.UtcNow,
                Notes = $"Saldo a favor aplicado a multa (bolsillo {entry.PublicId}).",
                CreatedByUserId = userId,
                CreatedByUserName = userName,
                AppliedFromCreditWithdrawal = withdrawal,
            };
            _db.Payments.Add(bridge);

            _auditService.StageBusinessEvent(
                action: AuditActions.ClientCreditAppliedToPenalty,
                entityName: AuditActions.ClientCreditWithdrawalEntityName,
                entityId: withdrawal.PublicId.ToString(),
                details: JsonSerializer.Serialize(new
                {
                    withdrawalPublicId = withdrawal.PublicId,
                    entryPublicId = entry.PublicId,
                    customerPublicId = customer.PublicId,
                    debitNotePublicId,
                    reservaPublicId,
                    currency,
                    amount = take,
                }),
                userId: userId,
                userName: userName);
        }

        return (firstWithdrawal!, firstEntry!);
    }

    // =========================================================================
    // ApplyCustomerCreditToPenaltyAsync (con retry de concurrencia)
    // =========================================================================

    public async Task<ClientCreditApplicationResultDto> ApplyCustomerCreditToPenaltyAsync(
        int customerId,
        ApplyCreditToPenaltyRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        if (request.Amount <= 0m)
        {
            throw new ArgumentException("El monto a aplicar debe ser mayor a 0.", nameof(request));
        }
        if (!Monedas.EsSoportada(request.Currency))
        {
            throw new ArgumentException("Moneda no soportada.", nameof(request));
        }

        await EnsureFeatureFlagOnAsync(ct);

        for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            try
            {
                return await TryApplyCustomerCreditToPenaltyOnceAsync(customerId, request, userId, userName, ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogWarning(ex,
                    "ApplyCustomerCreditToPenaltyAsync concurrency conflict on attempt {Attempt}/{Max} for customer {CustomerId}. Retrying.",
                    attempt + 1, MaxConcurrencyRetries, customerId);

                _db.ChangeTracker.Clear();
                if (attempt == MaxConcurrencyRetries - 1) throw;
                await Task.Delay((int)Math.Pow(4, attempt) * 100, ct);
            }
        }

        // Inalcanzable en la practica (el loop de arriba siempre retorna o relanza en el ultimo intento), pero
        // si algun dia se alcanzara el mensaje NO debe filtrar el nombre del metodo ni quedar en ingles.
        throw new InvalidOperationException("No pudimos completar la operación por un problema de concurrencia. Probá de nuevo.");
    }

    private async Task<ClientCreditApplicationResultDto> TryApplyCustomerCreditToPenaltyOnceAsync(
        int customerId,
        ApplyCreditToPenaltyRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        string currency = Monedas.Normalizar(request.Currency);
        decimal amount = ReservationEconomicPolicy.RoundCurrency(request.Amount);

        ClientCreditApplicationResultDto? result = null;

        async Task ApplyAndPersistAsync()
        {
            // Ver la nota "DISCIPLINA DE RETRY" arriba: el closure entero arranca limpio.
            _db.ChangeTracker.Clear();

            var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == customerId, ct)
                ?? throw new KeyNotFoundException("Cliente no encontrado");

            var debitNote = await _db.Invoices
                .AsNoTracking()
                .Where(i => i.PublicId == request.DebitNotePublicId)
                .Select(i => new { i.Id, i.PublicId })
                .FirstOrDefaultAsync(ct)
                ?? throw new KeyNotFoundException("Nota de Debito no encontrada");

            var bc = await _db.BookingCancellations
                .Include(b => b.Reserva)
                .FirstOrDefaultAsync(b => b.DebitNoteInvoiceId == debitNote.Id, ct)
                ?? throw new KeyNotFoundException("La Nota de Debito no corresponde a ninguna multa de cancelacion.");

            if (bc.Reserva.PayerId != customerId)
            {
                throw new BusinessInvariantViolationException(
                    "La multa no pertenece a este cliente. No se puede aplicar saldo de un cliente a la multa de otro.",
                    invariantCode: "INV-093");
            }

            var ownerScope = await GetTargetReservaOwnerScopeOrNullAsync(ct);
            if (ownerScope is not null
                && !string.Equals(bc.Reserva.ResponsibleUserId, ownerScope, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException(
                    "La reserva de la multa no esta asignada al usuario actual. No se puede aplicar saldo a una " +
                    "multa a cargo de otro vendedor.");
            }

            if (bc.DebitNoteStatus != DebitNoteStatus.Issued)
            {
                throw new BusinessInvariantViolationException(
                    "La multa todavía no tiene un comprobante fiscal vigente (se está emitiendo, o el que tenía " +
                    "se deshizo); no se puede aplicar saldo hasta que tenga uno aprobado.",
                    invariantCode: "INV-CLICREDIT-PENALTY-CAE");
            }

            // Gate + tope, LECTURA FRESCA (misma regla que un cobro real de multa): dos applies concurrentes
            // contra la MISMA ND no pueden validar ambos contra el mismo saldo pendiente.
            await CancelledDebitNoteCollectionGate.EnsureCollectableAsync(
                _db, bc.ReservaId, debitNote.Id, currency, amount, ct);

            var currencyEntries = await LoadFifoCreditEntriesAsync(customerId, currency, ct);

            var (firstWithdrawal, firstEntry) = ApplyCreditToPenaltyCoreOnEntries(
                currencyEntries, customer, currency, amount,
                debitNote.Id, debitNote.PublicId, bc.ReservaId, bc.Reserva.PublicId, userId, userName);

            await _db.SaveChangesAsync(ct);

            decimal availableAfter = await GetCustomerAvailableBalanceAsync(customerId, currency, ct);

            _logger.LogInformation(
                "metric:client_credit_applied_to_penalty | CustomerId={CustomerId} Currency={Currency} Amount={Amount} DebitNoteId={DebitNoteId}",
                customerId, currency, amount, debitNote.Id);

            result = new ClientCreditApplicationResultDto
            {
                ApplicationPublicId = firstWithdrawal.PublicId,
                EntryPublicId = firstEntry.PublicId,
                Currency = currency,
                Amount = amount,
                TargetReservaPublicId = bc.Reserva.PublicId,
                DebitNotePublicId = debitNote.PublicId,
                IsReversal = false,
                AvailableBalanceAfter = availableAfter,
            };
        }

        if (_db.Database.IsRelational())
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _db.Database.BeginTransactionAsync(
                    System.Data.IsolationLevel.Serializable, ct);
                await ApplyAndPersistAsync();
                await transaction.CommitAsync(ct);
            });
        }
        else
        {
            await ApplyAndPersistAsync();
        }

        return result!;
    }

    // =========================================================================
    // Neteo automatico en devolucion: preview (solo lectura) + confirmacion atomica.
    // =========================================================================

    public async Task<RefundNettingPreviewDto> GetCustomerRefundNettingPreviewAsync(
        int customerId, string currency, CancellationToken ct)
    {
        if (!Monedas.EsSoportada(currency))
        {
            throw new ArgumentException("Moneda no soportada.", nameof(currency));
        }
        var normalizedCurrency = Monedas.Normalizar(currency);

        var customerExists = await _db.Customers.AsNoTracking().AnyAsync(c => c.Id == customerId, ct);
        if (!customerExists) throw new KeyNotFoundException("Cliente no encontrado");

        decimal availableCredit = await GetCustomerAvailableBalanceAsync(customerId, normalizedCurrency, ct);
        var openPenalties = await LoadOpenPenaltiesForCustomerAsync(customerId, normalizedCurrency, ct);

        var lines = openPenalties
            .Select(p => new RefundNettingPenaltyLineDto
            {
                ReservaPublicId = p.ReservaPublicId,
                NumeroReserva = p.NumeroReserva,
                DebitNotePublicId = p.DebitNotePublicId,
                DebitNoteDisplayNumber = p.DebitNoteDisplayNumber,
                OutstandingAmount = p.Outstanding,
            })
            .ToList();

        decimal totalOpenPenalties = ReservationEconomicPolicy.RoundCurrency(lines.Sum(l => l.OutstandingAmount));
        decimal netToRefund = Math.Max(0m, ReservationEconomicPolicy.RoundCurrency(availableCredit - totalOpenPenalties));

        return new RefundNettingPreviewDto
        {
            Currency = normalizedCurrency,
            AvailableCredit = availableCredit,
            OpenPenalties = lines,
            TotalOpenPenalties = totalOpenPenalties,
            NetToRefund = netToRefund,
            PlainExplanation = BuildRefundPlainExplanation(availableCredit, totalOpenPenalties, netToRefund, normalizedCurrency),
        };
    }

    /// <summary>Tanda D1: texto en criollo del preview de neteo, sin cifras internas ni jerga.</summary>
    private static string BuildRefundPlainExplanation(
        decimal availableCredit, decimal totalOpenPenalties, decimal netToRefund, string currency)
    {
        var prefix = string.Equals(currency, Monedas.USD, StringComparison.Ordinal) ? "US$" : "$";

        if (totalOpenPenalties <= 0m)
        {
            return $"Te devolvemos {prefix}{FormatMoneyPlain(netToRefund)} = todo tu saldo a favor " +
                   $"(no tenés multas pendientes en {currency}).";
        }

        decimal remainingPenaltyDebt = Math.Max(0m, ReservationEconomicPolicy.RoundCurrency(totalOpenPenalties - availableCredit));
        if (remainingPenaltyDebt > 0m)
        {
            return $"Tu saldo a favor ({prefix}{FormatMoneyPlain(availableCredit)}) no alcanza para cubrir tus " +
                   $"multas pendientes ({prefix}{FormatMoneyPlain(totalOpenPenalties)}): se aplica todo contra " +
                   $"multas y quedan {prefix}{FormatMoneyPlain(remainingPenaltyDebt)} sin cubrir. No hay nada para devolver.";
        }

        if (netToRefund <= 0m)
        {
            return $"Tu saldo a favor ({prefix}{FormatMoneyPlain(availableCredit)}) se usa completo para pagar " +
                   $"tus multas pendientes ({prefix}{FormatMoneyPlain(totalOpenPenalties)}); no queda nada para devolver.";
        }

        return $"Te devolvemos {prefix}{FormatMoneyPlain(netToRefund)} = {prefix}{FormatMoneyPlain(availableCredit)} " +
               $"a favor − {prefix}{FormatMoneyPlain(totalOpenPenalties)} de multa.";
    }

    /// <summary>Formato criollo de un monto para texto (es-AR: punto de miles, sin decimales si es entero).</summary>
    private static string FormatMoneyPlain(decimal amount)
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo("es-AR");
        bool hasCents = amount != Math.Truncate(amount);
        return amount.ToString(hasCents ? "N2" : "N0", culture);
    }

    // =========================================================================
    // RefundCustomerCreditWithNettingAsync (con retry de concurrencia)
    // =========================================================================

    public async Task<RefundWithNettingResultDto> RefundCustomerCreditWithNettingAsync(
        int customerId,
        RefundWithNettingRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        if (!Monedas.EsSoportada(request.Currency))
        {
            throw new ArgumentException("Moneda no soportada.", nameof(request));
        }

        // Gate de exposicion de datos: el mensaje de error NO debe nombrar los tokens internos del contrato
        // (PhysicalCash/Transfer) — el contrato de la API sigue aceptando esos mismos valores, solo cambia el
        // TEXTO que ve el usuario final.
        var refundKind = request.RefundMethod switch
        {
            "PhysicalCash" => WithdrawalKind.PhysicalCash,
            "Transfer" => WithdrawalKind.Transfer,
            _ => throw new ArgumentException(
                "Elegí una forma de devolución válida: efectivo o transferencia.", nameof(request)),
        };

        await EnsureFeatureFlagOnAsync(ct);

        for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            try
            {
                return await TryRefundCustomerCreditWithNettingOnceAsync(customerId, request, refundKind, userId, userName, ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogWarning(ex,
                    "RefundCustomerCreditWithNettingAsync concurrency conflict on attempt {Attempt}/{Max} for customer {CustomerId}. Retrying.",
                    attempt + 1, MaxConcurrencyRetries, customerId);

                _db.ChangeTracker.Clear();
                if (attempt == MaxConcurrencyRetries - 1) throw;
                await Task.Delay((int)Math.Pow(4, attempt) * 100, ct);
            }
        }

        // Inalcanzable en la practica (el loop de arriba siempre retorna o relanza en el ultimo intento), pero
        // si algun dia se alcanzara el mensaje NO debe filtrar el nombre del metodo ni quedar en ingles.
        throw new InvalidOperationException("No pudimos completar la operación por un problema de concurrencia. Probá de nuevo.");
    }

    private async Task<RefundWithNettingResultDto> TryRefundCustomerCreditWithNettingOnceAsync(
        int customerId,
        RefundWithNettingRequest request,
        WithdrawalKind refundKind,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        string currency = Monedas.Normalizar(request.Currency);
        RefundWithNettingResultDto? result = null;

        async Task RefundAndNetAsync()
        {
            // Ver la nota "DISCIPLINA DE RETRY" arriba: el closure entero arranca limpio y hace TODO adentro
            // (lecturas frescas, gate por ND, Add() y SaveChanges), para poder repetirse entero sin dejar
            // basura duplicada en el ChangeTracker.
            _db.ChangeTracker.Clear();

            var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == customerId, ct)
                ?? throw new KeyNotFoundException("Cliente no encontrado");

            decimal availableCreditBefore = await GetCustomerAvailableBalanceAsync(customerId, currency, ct);
            if (availableCreditBefore <= 0m)
            {
                throw new BusinessInvariantViolationException(
                    "No hay saldo a favor disponible en esa moneda para devolver.",
                    invariantCode: "INV-CLICREDIT-NOCREDIT");
            }

            var openPenalties = await LoadOpenPenaltiesForCustomerAsync(customerId, currency, ct);
            var currencyEntries = await LoadFifoCreditEntriesAsync(customerId, currency, ct);

            // 1) Netear FIFO contra cada multa abierta, con el pendiente RE-LEIDO fresco (revalidacion
            //    acumulada por ND, M2): si otra operacion ya la toco desde que armamos openPenalties, se
            //    respeta el numero nuevo (incluso si eso significa saltearla por completo).
            decimal poolRemaining = availableCreditBefore;
            decimal totalAppliedToPenalties = 0m;
            var penaltyApplications = new List<ClientCreditApplicationResultDto>();
            var nettedAgainst = new List<(string NumeroReserva, decimal Amount)>();

            foreach (var penalty in openPenalties)
            {
                if (poolRemaining <= 0m) break;

                var freshOutstanding = await RecomputeFreshOutstandingAsync(penalty.DebitNoteId, penalty.ImporteTotal, ct);
                if (freshOutstanding <= 0m) continue; // ya se salvo por otra via mientras tanto

                decimal take = ReservationEconomicPolicy.RoundCurrency(Math.Min(poolRemaining, freshOutstanding));
                if (take <= 0m) continue;

                var (firstWithdrawal, firstEntry) = ApplyCreditToPenaltyCoreOnEntries(
                    currencyEntries, customer, currency, take,
                    penalty.DebitNoteId, penalty.DebitNotePublicId, penalty.ReservaId, penalty.ReservaPublicId,
                    userId, userName);

                poolRemaining = ReservationEconomicPolicy.RoundCurrency(poolRemaining - take);
                totalAppliedToPenalties = ReservationEconomicPolicy.RoundCurrency(totalAppliedToPenalties + take);
                nettedAgainst.Add((penalty.NumeroReserva, take));

                penaltyApplications.Add(new ClientCreditApplicationResultDto
                {
                    ApplicationPublicId = firstWithdrawal.PublicId,
                    EntryPublicId = firstEntry.PublicId,
                    Currency = currency,
                    Amount = take,
                    TargetReservaPublicId = penalty.ReservaPublicId,
                    DebitNotePublicId = penalty.DebitNotePublicId,
                    IsReversal = false,
                    AvailableBalanceAfter = poolRemaining,
                });
            }

            decimal netToRefund = poolRemaining;

            // 2) El neto (si sobra algo) se devuelve como egreso REAL de caja, drenando los MISMOS bolsillos
            //    (currencyEntries, ya parcialmente consumidos por el paso 1) FIFO. Ley 25.345 se valida sobre
            //    el NETO COMPLETO, ANTES de tocar nada — nunca fragmentado por bolsillo (fraccionar el mismo
            //    egreso en varios movimientos chicos para esquivar el tope seria eludir la ley a proposito).
            Guid? withdrawalPublicId = null;
            if (netToRefund > 0m)
            {
                if (refundKind == WithdrawalKind.PhysicalCash)
                {
                    var settings = await _settings.GetEntityAsync(ct);
                    if (netToRefund > settings.Ley25345ThresholdAmount)
                    {
                        throw new BusinessInvariantViolationException(
                            $"Ley 25.345: no se puede devolver en efectivo más de ${CurrencyDisplayFormat.Amount(settings.Ley25345ThresholdAmount)}. " +
                            $"El neto a devolver es ${CurrencyDisplayFormat.Amount(netToRefund)}. Usá transferencia bancaria en su lugar.",
                            invariantCode: "INV-094");
                    }
                    if (netToRefund > settings.PhysicalRefundAlertThreshold)
                    {
                        _logger.LogWarning(
                            "metric:physical_refund_alert | CustomerId={CustomerId} Amount={Amount} Threshold={Threshold} UserId={UserId}",
                            customerId, netToRefund, settings.PhysicalRefundAlertThreshold, userId);
                        _auditService.StageBusinessEvent(
                            action: AuditActions.ClientCreditPhysicalRefundAlert,
                            entityName: AuditActions.ClientCreditEntryEntityName,
                            entityId: customer.PublicId.ToString(),
                            details: JsonSerializer.Serialize(new
                            {
                                customerPublicId = customer.PublicId,
                                amount = netToRefund,
                                currency,
                                threshold = settings.PhysicalRefundAlertThreshold,
                            }),
                            userId: userId,
                            userName: userName);
                    }
                }

                var receiptText = BuildRefundReceiptText(availableCreditBefore, nettedAgainst, netToRefund, currency);

                decimal remainingNet = netToRefund;
                foreach (var entry in currencyEntries)
                {
                    if (remainingNet <= 0m) break;
                    if (entry.RemainingBalance <= 0m) continue;

                    decimal take = ReservationEconomicPolicy.RoundCurrency(Math.Min(entry.RemainingBalance, remainingNet));
                    var withdrawalRequest = new WithdrawClientCreditRequest(
                        Kind: refundKind,
                        Amount: take,
                        PaymentMethodOverride: null,
                        AppliedToReservaPublicId: null,
                        ApprovalRequestPublicId: null,
                        Reference: request.Reference);

                    var withdrawal = BuildWithdrawalAndMovement(
                        entry, withdrawalRequest, refundKind, userId, userName,
                        descriptionOverride: receiptText);

                    // OJO trainee/junior: BuildWithdrawalAndMovement NO decrementa RemainingBalance (ese paso
                    // vive en WithdrawAsync.PersistAndFinalizeAsync, que aca NO se usa — el neteo arma su
                    // propio flujo atomico multi-destino). Hay que hacerlo a mano aca, igual criterio que el
                    // resto del modulo (redondeo + IsFullyConsumed cuando llega a 0).
                    entry.RemainingBalance = ReservationEconomicPolicy.RoundCurrency(entry.RemainingBalance - take);
                    if (entry.RemainingBalance <= 0m)
                    {
                        entry.RemainingBalance = 0m;
                        entry.IsFullyConsumed = true;
                    }

                    withdrawalPublicId ??= withdrawal.PublicId;
                    remainingNet = ReservationEconomicPolicy.RoundCurrency(remainingNet - take);

                    _auditService.StageBusinessEvent(
                        action: AuditActions.ClientCreditWithdrawn,
                        entityName: AuditActions.ClientCreditWithdrawalEntityName,
                        entityId: withdrawal.PublicId.ToString(),
                        details: JsonSerializer.Serialize(new
                        {
                            withdrawalPublicId = withdrawal.PublicId,
                            entryPublicId = entry.PublicId,
                            customerPublicId = customer.PublicId,
                            kind = withdrawal.Kind.ToString(),
                            amount = take,
                            currency,
                            nettedAgainst = nettedAgainst.Select(n => new { numeroReserva = n.NumeroReserva, amount = n.Amount }),
                        }),
                        userId: userId,
                        userName: userName);
                }
            }

            await _db.SaveChangesAsync(ct);

            var finalReceiptText = netToRefund > 0m
                ? BuildRefundReceiptText(availableCreditBefore, nettedAgainst, netToRefund, currency)
                : BuildRefundReceiptText(availableCreditBefore, nettedAgainst, 0m, currency);

            _logger.LogInformation(
                "metric:client_credit_refund_with_netting | CustomerId={CustomerId} Currency={Currency} TotalAppliedToPenalties={TotalAppliedToPenalties} NetRefunded={NetRefunded}",
                customerId, currency, totalAppliedToPenalties, netToRefund);

            result = new RefundWithNettingResultDto
            {
                Currency = currency,
                AvailableCreditBefore = availableCreditBefore,
                PenaltyApplications = penaltyApplications,
                TotalAppliedToPenalties = totalAppliedToPenalties,
                NetRefunded = netToRefund,
                RefundMethod = request.RefundMethod,
                WithdrawalPublicId = withdrawalPublicId,
                ReceiptText = finalReceiptText,
            };
        }

        if (_db.Database.IsRelational())
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _db.Database.BeginTransactionAsync(
                    System.Data.IsolationLevel.Serializable, ct);
                await RefundAndNetAsync();
                await transaction.CommitAsync(ct);
            });
        }
        else
        {
            await RefundAndNetAsync();
        }

        return result!;
    }

    /// <summary>
    /// Tanda D1: texto del recibo del egreso con el desglose exacto acordado con el dueño: saldo a favor, cada
    /// multa descontada y el total devuelto (o la aclaracion de que no queda nada si el neto es 0).
    /// </summary>
    private static string BuildRefundReceiptText(
        decimal availableCreditBefore,
        IReadOnlyList<(string NumeroReserva, decimal Amount)> nettedAgainst,
        decimal netRefunded,
        string currency)
    {
        var prefix = string.Equals(currency, Monedas.USD, StringComparison.Ordinal) ? "US$" : "$";
        var lines = new List<string>
        {
            "Devolución de saldo a favor",
            $"Saldo a favor {prefix}{FormatMoneyPlain(availableCreditBefore)}",
        };
        foreach (var (numeroReserva, amount) in nettedAgainst)
        {
            lines.Add($"Menos multa {numeroReserva} −{prefix}{FormatMoneyPlain(amount)}");
        }
        lines.Add(netRefunded > 0m
            ? $"Total devuelto {prefix}{FormatMoneyPlain(netRefunded)}"
            : "No queda saldo para devolver.");
        return string.Join("\n", lines);
    }

    /// <summary>FC4: saldo a favor disponible (Σ RemainingBalance) del cliente en una moneda, leido fresco.</summary>
    private async Task<decimal> GetCustomerAvailableBalanceAsync(int customerId, string currency, CancellationToken ct)
    {
        var rows = await _db.ClientCreditEntries
            .Where(e => e.CustomerId == customerId && e.RemainingBalance > 0m)
            .Select(e => new { e.Currency, e.RemainingBalance })
            .ToListAsync(ct);
        return rows
            .Where(r => Monedas.Normalizar(r.Currency) == currency)
            .Sum(r => r.RemainingBalance);
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
            // Gate de exposicion de datos: el mensaje NUNCA debe nombrar la llave interna del flag.
            // El controller devuelve ex.Message tal cual al usuario final (no-programador).
            throw new InvalidOperationException(
                "Esta operación no está disponible por ahora. Probá de nuevo más tarde.");
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
                $"El monto solicitado (${CurrencyDisplayFormat.Amount(request.Amount)}) supera el saldo disponible " +
                $"(${CurrencyDisplayFormat.Amount(entry.RemainingBalance)}) del cliente.",
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
        string? approvalRequestId = null,
        string? descriptionOverride = null)
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

        // Tanda D1 (2026-07-16): el neteo en devolucion arma su propio texto de recibo con el desglose exacto
        // (saldo a favor menos cada multa descontada). El builder solo sabe armar una Description generica
        // ("Retiro credito cliente ..."); el caller que SI conoce el desglose la pisa aca.
        if (!string.IsNullOrWhiteSpace(descriptionOverride))
        {
            movement.Description = descriptionOverride;
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
