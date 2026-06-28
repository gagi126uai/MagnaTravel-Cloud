using System.Security.Claims;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using TravelApi.Application.DTOs;
using TravelApi.Application.Exceptions;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services.Reservations;

namespace TravelApi.Infrastructure.Services;

public class PaymentService : IPaymentService
{
    private readonly AppDbContext _dbContext;
    private readonly IEntityReferenceResolver _entityReferenceResolver;
    private readonly IMapper _mapper;
    private readonly IOperationalFinanceSettingsService _operationalFinanceSettingsService;
    private readonly ILogger<PaymentService> _logger;
    // B1.15 Fase 2a (FIX 5): opcionales para no romper unit tests con ctor de 5 args.
    private readonly IUserPermissionResolver? _permissionResolver;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    // B1.15 (2026-05-11): workflow de aprobaciones para anular receipts. Opcionales:
    // los tests unitarios viejos pueden construir el service sin estos.
    private readonly IApprovalRequestService? _approvalService;
    private readonly IApprovalPolicyService? _approvalPolicyService;
    private readonly IAuditService? _auditService;

    // Estados de Reserva que tienen DEUDA COBRABLE (cuenta por cobrar viva). Lo usan la worklist y el summary
    // de cobranza para listar a quien se le puede cobrar.
    //
    // ADR-033 (2026-06-16, A3/E2): pasa a la lista de DEUDA (EstadoReserva.SaleFirmStatuses, INCLUYE Closed),
    // misma fuente que el AR / dashboard / saldo del cliente. Antes (ADR-032) usaba ActiveCollectionStatuses
    // (sin Closed) -> una reserva Finalizada con deuda no aparecia en la cobranza aunque se le pudiera cobrar
    // (la nueva regla de A1 si lo permite). Ahora el front de cobranza y la regla de alta de cobro coinciden.
    // Las queries filtran ademas Balance > 0, asi que una Closed saldada no aparece.
    private static readonly string[] CollectableDebtStatuses = EstadoReserva.SaleFirmStatuses;

    public PaymentService(
        AppDbContext dbContext,
        IEntityReferenceResolver entityReferenceResolver,
        IMapper mapper,
        IOperationalFinanceSettingsService operationalFinanceSettingsService,
        ILogger<PaymentService> logger,
        IUserPermissionResolver? permissionResolver = null,
        IHttpContextAccessor? httpContextAccessor = null,
        IApprovalRequestService? approvalService = null,
        IApprovalPolicyService? approvalPolicyService = null,
        IAuditService? auditService = null)
    {
        _dbContext = dbContext;
        _entityReferenceResolver = entityReferenceResolver;
        _mapper = mapper;
        _operationalFinanceSettingsService = operationalFinanceSettingsService;
        _logger = logger;
        _permissionResolver = permissionResolver;
        _httpContextAccessor = httpContextAccessor;
        _approvalService = approvalService;
        _approvalPolicyService = approvalPolicyService;
        _auditService = auditService;
        QuestPDF.Settings.License = LicenseType.Community;
    }

    /// <summary>
    /// ADR-035 (2026-06-19): arma el contexto de capacidades para los gates de PAGO (registrar / editar /
    /// borrar). Estas dos capacidades solo consultan <see cref="Reserva.Status"/> y <see cref="Reserva.Balance"/>,
    /// por eso aca se construye un contexto LIVIANO sin tocar la base de datos: las banderas fiscales/voucher/
    /// candado quedan en false porque no influyen en CanRegisterPayment ni CanEditOrDeletePayment (la
    /// inmutabilidad fiscal del cobro la enforza despues MutationGuards, la defensa final). El armado COMPLETO
    /// del contexto (con CAE/voucher/candado reales) vive en ReservaService al construir el DTO de la reserva.
    /// </summary>
    private static ReservaCapabilityContext BuildCapabilityContext(Reserva reserva)
        => new(
            Status: reserva.Status,
            Balance: reserva.Balance,
            HasLiveCae: false,
            HasLiveVoucher: false,
            HasLiveEditAuth: false,
            HasAnyPayment: false);

    /// <summary>
    /// ADR-035 (2026-06-19): PRIMERA COMPUERTA de editar/borrar cobro. Resuelve el estado de la reserva del
    /// pago y consulta <see cref="ReservaCapabilities.CanEditOrDeletePayment"/>: en los estados terminales
    /// {Closed, Cancelled, Lost, PendingOperatorRefund} corta con 409 ("para corregir, anula el cobro"). En
    /// los demas no hace nada (los guards fiscales/de puente siguen despues). Un pago sin reserva asociada
    /// (caso raro) no se bloquea por esta compuerta.
    /// </summary>
    private async Task EnsurePaymentEditableByStateAsync(Payment payment, CancellationToken cancellationToken)
    {
        if (payment.ReservaId is not int reservaId)
            return;

        var status = await _dbContext.Reservas
            .AsNoTracking()
            .Where(r => r.Id == reservaId)
            .Select(r => r.Status)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(status))
            return;

        // Balance no influye en CanEditOrDeletePayment (solo el estado); va 0 por simplicidad.
        var capability = ReservaCapabilityPolicy
            .For(new ReservaCapabilityContext(status, Balance: 0m, false, false, false, false))
            .CanEditOrDeletePayment;
        if (!capability.Allowed)
        {
            _logger.LogWarning(
                "Payment edit/delete rejected by state gate. PaymentId={PaymentId} ReservaId={ReservaId} Status={Status}.",
                payment.Id, reservaId, status);
            throw new InvalidOperationException(capability.Reason);
        }
    }

    /// <summary>
    /// B1.15 Fase 2a (FIX 5): null si Admin o user con cobranzas.view_all (=> ve todo);
    /// si no, devuelve el currentUserId que sera usado para filtrar payments por
    /// Reserva.ResponsibleUserId. Si no hay user resoluble, devuelve un sentinel
    /// imposible "__no_user__".
    /// </summary>
    private async Task<string?> GetOwnerScopeOrNullAsync(CancellationToken ct)
    {
        var httpUser = _httpContextAccessor?.HttpContext?.User;
        if (httpUser is null) return null; // tests unitarios sin HttpContext: comportamiento legacy
        if (httpUser.IsInRole("Admin")) return null;

        var currentUserId = httpUser.FindFirstValue(ClaimTypes.NameIdentifier);
        if (_permissionResolver is null || string.IsNullOrEmpty(currentUserId))
        {
            // No podemos resolver permisos => fail-safe: filtrar por user actual o sentinel.
            return string.IsNullOrEmpty(currentUserId) ? "__no_user__" : currentUserId;
        }

        var perms = await _permissionResolver.GetPermissionsAsync(currentUserId, ct);
        if (perms.Contains(Permissions.CobranzasViewAll))
        {
            return null; // ve todo
        }

        return currentUserId; // filter mine
    }

    public async Task<CollectionsSummaryDto> GetCollectionsSummaryAsync(CancellationToken cancellationToken)
    {
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(cancellationToken);
        var today = DateTime.UtcNow.Date;
        var threshold = today.AddDays(Math.Max(settings.UpcomingUnpaidReservationAlertDays, 1));
        var currentMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        // B1.15 Fase 2a (FIX 5): si el user NO tiene cobranzas.view_all, los totales
        // del summary se calculan sobre las reservas a su cargo unicamente.
        var ownerScope = await GetOwnerScopeOrNullAsync(cancellationToken);

        var reservasQuery = _dbContext.Reservas
            .AsNoTracking()
            .Where(r => CollectableDebtStatuses.Contains(r.Status) && r.Balance > 0);
        if (ownerScope is not null)
        {
            reservasQuery = reservasQuery.Where(r => r.ResponsibleUserId == ownerScope);
        }

        var pendingReservations = await reservasQuery
            .Select(r => new
            {
                r.Id,
                r.PublicId,
                r.Balance,
                r.StartDate
            })
            .ToListAsync(cancellationToken);

        var pendingAmount = pendingReservations.Sum(r => EconomicRulesHelper.RoundCurrency(r.Balance));
        var urgentReservations = pendingReservations
            .Where(r => r.StartDate.HasValue && r.StartDate.Value.Date >= today && r.StartDate.Value.Date <= threshold)
            .ToList();

        // "Cobrado este mes" = plata REAL nueva que entró a caja este mes. Filtros:
        //  - no anulada / no borrada;
        //  - EntryType Payment (no reversas de NC);
        //  - Amount > 0 (es un ingreso, no un ajuste negativo);
        //  - AffectsCash == true: EXCLUYE los pagos PUENTE (saldo a favor aplicado de otra reserva,
        //    sobrepago, anulacion->credito). Esos bajan la deuda del destino pero NO son plata nueva: contarlos
        //    inflaba el KPI con plata que ya habia entrado en otra reserva (o que nunca entró). Ver
        //    OverpaymentCreditConverter / AppliedCreditBridge / CancellationToClientCreditConverter (todos
        //    crean Payment con AffectsCash=false).
        var collectedQuery = _dbContext.Payments
            .AsNoTracking()
            .Where(p =>
                !p.IsDeleted &&
                p.Status != "Cancelled" &&
                p.EntryType == PaymentEntryTypes.Payment &&
                p.AffectsCash &&
                p.Amount > 0 &&
                p.PaidAt >= currentMonth);
        if (ownerScope is not null)
        {
            collectedQuery = collectedQuery.Where(p => p.Reserva != null && p.Reserva.ResponsibleUserId == ownerScope);
        }

        // Agrupado POR MONEDA real del cobro (Payment.Currency): nunca se suman ARS + USD en un total.
        // Currency puede venir null/vacio en pagos legacy -> se normaliza a "ARS" (regla legacy del sistema).
        var collectedByCurrencyRaw = await collectedQuery
            .GroupBy(p => p.Currency)
            .Select(g => new { Currency = g.Key, Amount = g.Sum(p => p.Amount) })
            .ToListAsync(cancellationToken);

        var collectedByCurrency = collectedByCurrencyRaw
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Currency) ? "ARS" : x.Currency, StringComparer.Ordinal)
            .Select(g => new CurrencyAmountDto
            {
                Currency = g.Key,
                Amount = EconomicRulesHelper.RoundCurrency(g.Sum(x => x.Amount)),
            })
            .OrderBy(x => x.Currency, StringComparer.Ordinal)
            .ToList();

        // Escalar de compat: suma cross-moneda (igual que los demas escalares historicos). El detalle real,
        // sin mezclar monedas, viaja en CollectedThisMonthByCurrency.
        var collectedThisMonth = collectedByCurrency.Sum(x => x.Amount);

        // (2026-06-26, P1=B) Saldo a favor APLICADO este mes (linea chica "+ $X aplicados de saldo a favor",
        // debajo del cobrado real). NO es plata nueva: son los Payment PUENTE POSITIVOS de aplicacion de credito
        // — MISMO criterio canonico que AppliedCreditBridge.IsAppliedCreditBridge (Method "SaldoAFavorAplicado",
        // AffectsCash=false, AppliedFromCreditWithdrawalId != null), expandido inline porque ese helper recibe la
        // entidad ya materializada y no se traduce a SQL. Solo los POSITIVOS de APLICACION: el puente NEGATIVO de
        // sobrepago usa otro Method/FK (OriginalPaymentId) y el de anulacion->credito tampoco matchea este Method.
        var creditApplicationsQuery = _dbContext.Payments
            .AsNoTracking()
            .Where(p =>
                !p.IsDeleted &&
                p.Status != "Cancelled" &&
                p.Method == AppliedCreditBridge.BridgeMethod &&
                !p.AffectsCash &&
                p.AppliedFromCreditWithdrawalId != null &&
                p.Amount > 0 &&
                p.PaidAt >= currentMonth);
        if (ownerScope is not null)
        {
            creditApplicationsQuery = creditApplicationsQuery.Where(p => p.Reserva != null && p.Reserva.ResponsibleUserId == ownerScope);
        }

        var creditApplicationsRaw = await creditApplicationsQuery
            .GroupBy(p => p.Currency)
            .Select(g => new { Currency = g.Key, Amount = g.Sum(p => p.Amount) })
            .ToListAsync(cancellationToken);

        var creditApplicationsByCurrency = creditApplicationsRaw
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Currency) ? "ARS" : x.Currency, StringComparer.Ordinal)
            .Select(g => new CurrencyAmountDto
            {
                Currency = g.Key,
                Amount = EconomicRulesHelper.RoundCurrency(g.Sum(x => x.Amount)),
            })
            .OrderBy(x => x.Currency, StringComparer.Ordinal)
            .ToList();

        return new CollectionsSummaryDto
        {
            PendingAmount = EconomicRulesHelper.RoundCurrency(pendingAmount),
            CollectedThisMonth = EconomicRulesHelper.RoundCurrency(collectedThisMonth),
            CollectedThisMonthByCurrency = collectedByCurrency,
            CreditApplicationsThisMonthByCurrency = creditApplicationsByCurrency,
            UrgentReservationsCount = urgentReservations.Count,
            UrgentPendingAmount = EconomicRulesHelper.RoundCurrency(urgentReservations.Sum(r => r.Balance)),
            BlockedOperationalCount = settings.RequireFullPaymentForOperativeStatus ? pendingReservations.Count : 0,
            BlockedVoucherCount = settings.RequireFullPaymentForVoucher ? pendingReservations.Count : 0
        };
    }

    public async Task<PagedResponse<CollectionWorkItemDto>> GetCollectionsWorklistAsync(CollectionWorklistQuery query, CancellationToken cancellationToken)
    {
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(cancellationToken);
        var today = DateTime.UtcNow.Date;
        var threshold = today.AddDays(Math.Max(settings.UpcomingUnpaidReservationAlertDays, 1));

        // B1.15 Fase 2a (FIX 5): filter mine. Sin cobranzas.view_all el vendedor solo ve sus reservas.
        var ownerScope = await GetOwnerScopeOrNullAsync(cancellationToken);

        var reservationsQuery = _dbContext.Reservas
            .AsNoTracking()
            .Where(r => CollectableDebtStatuses.Contains(r.Status) && r.Balance > 0);
        if (ownerScope is not null)
        {
            reservationsQuery = reservationsQuery.Where(r => r.ResponsibleUserId == ownerScope);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var normalized = query.Search.Trim().ToLowerInvariant();
            reservationsQuery = reservationsQuery.Where(reserva =>
                reserva.NumeroReserva.ToLower().Contains(normalized) ||
                (reserva.Payer != null && reserva.Payer.FullName.ToLower().Contains(normalized)) ||
                (reserva.ResponsibleUserName != null && reserva.ResponsibleUserName.ToLower().Contains(normalized)));
        }

        var blocksOperational = settings.RequireFullPaymentForOperativeStatus;
        var blocksVoucher = settings.RequireFullPaymentForVoucher;

        if (!string.Equals(query.Urgency, "all", StringComparison.OrdinalIgnoreCase))
        {
            reservationsQuery = query.Urgency.Trim().ToLowerInvariant() switch
            {
                "urgent" => reservationsQuery.Where(reserva =>
                    reserva.StartDate.HasValue &&
                    reserva.StartDate.Value.Date >= today &&
                    reserva.StartDate.Value.Date <= threshold),
                "blocked" => blocksOperational || blocksVoucher
                    ? reservationsQuery
                    : reservationsQuery.Where(_ => false),
                _ => reservationsQuery
            };
        }

        var workItemsQuery = reservationsQuery.Select(reserva => new CollectionWorkItemDto
        {
            ReservaPublicId = reserva.PublicId,
            NumeroReserva = reserva.NumeroReserva,
            CustomerName = reserva.Payer != null ? reserva.Payer.FullName : "Consumidor Final",
            StartDate = reserva.StartDate,
            ResponsibleUserName = reserva.ResponsibleUserName,
            TotalSale = reserva.TotalSale,
            TotalPaid = reserva.TotalPaid,
            Balance = reserva.Balance,
            CollectionStatus = reserva.TotalPaid > 0 ? "Parcial" : "Pendiente",
            UrgencyStatus =
                reserva.StartDate.HasValue &&
                reserva.StartDate.Value.Date >= today &&
                reserva.StartDate.Value.Date <= threshold
                    ? "Urgente"
                    : "Normal",
            BlocksOperational = blocksOperational,
            BlocksVoucher = blocksVoucher
        });

        workItemsQuery = ApplyCollectionWorkItemOrdering(workItemsQuery, query);
        var paged = await workItemsQuery.ToPagedResponseAsync(query, cancellationToken);

        // ADR-035 (2026-06-19): la worklist necesita la moneda de cada reserva para que el modal de cobro NO
        // mande el pago sin moneda (un cobro sobre una reserva en USD se registraria en ARS y corromperia el
        // libro multimoneda). Llenamos MonedaPrincipal + PorMoneda en BATCH (una sola query por la pagina),
        // leyendo la misma tabla materializada que usa el listado de reservas. Sin N+1.
        await FillCurrencyForWorklistAsync(paged.Items, cancellationToken);
        return paged;
    }

    /// <summary>
    /// ADR-035 (2026-06-19): llena <c>MonedaPrincipal</c> y <c>PorMoneda</c> de cada fila de la worklist de
    /// cobranza. Lee el detalle de saldo por moneda desde la tabla materializada <c>ReservaMoneyByCurrency</c>
    /// en UNA sola query por los PublicId de la pagina (mismo patron que el listado de reservas: sin N+1, sin
    /// traer las colecciones de cada reserva). La moneda principal sale del helper de dominio compartido
    /// <see cref="ReservaPrimaryCurrency"/>, la MISMA regla que el detalle de la reserva.
    ///
    /// <para>La worklist de cobranza no expone costos, asi que <c>TotalCost</c> por moneda llega en 0
    /// (no se proyecta el costo real de la tabla).</para>
    /// </summary>
    private async Task FillCurrencyForWorklistAsync(
        IReadOnlyList<CollectionWorkItemDto> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return;

        var publicIds = items.Select(i => i.ReservaPublicId).ToList();

        // Una fila por (reserva, moneda). Join explicito contra Reservas (no nav implicita) para resolver el
        // PublicId con el que matchear el DTO y correr igual en Postgres e InMemory.
        var rows = await (
            from row in _dbContext.ReservaMoneyByCurrency.AsNoTracking()
            join reservaPadre in _dbContext.Reservas.AsNoTracking() on row.ReservaId equals reservaPadre.Id
            where publicIds.Contains(reservaPadre.PublicId)
            select new
            {
                ReservaPublicId = reservaPadre.PublicId,
                row.Currency,
                row.TotalSale,
                row.ConfirmedSale,
                row.TotalPaid,
                row.Balance
            }).ToListAsync(cancellationToken);

        var byReserva = rows
            .GroupBy(row => row.ReservaPublicId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var item in items)
        {
            if (!byReserva.TryGetValue(item.ReservaPublicId, out var reservaRows))
            {
                // Sin filas hijas (reserva legacy sin backfill): dejamos PorMoneda vacio y MonedaPrincipal null.
                // El front cae a su default historico (ARS), igual que hoy. No inventamos una moneda aca.
                continue;
            }

            // Orden alfabetico estable por moneda: es el mismo orden que usa el detalle de la reserva antes de
            // resolver la moneda principal, asi el desempate del helper es identico en ambos lugares.
            var ordered = reservaRows
                .OrderBy(row => row.Currency, StringComparer.Ordinal)
                .ToList();

            item.PorMoneda = ordered
                .Select(row => new ReservaMoneyLineDto
                {
                    Currency = row.Currency,
                    TotalSale = row.TotalSale,
                    ConfirmedSale = row.ConfirmedSale,
                    TotalCost = 0m, // la worklist de cobranza no expone costos
                    TotalPaid = row.TotalPaid,
                    Balance = row.Balance
                })
                .ToList();

            var lines = ordered
                .Select(row => (row.Currency, row.Balance))
                .ToList();
            item.MonedaPrincipal = ReservaPrimaryCurrency.Resolve(lines);
        }
    }

    public async Task<PagedResponse<PaymentDto>> GetAllPaymentsAsync(PaymentsListQuery query, CancellationToken cancellationToken)
    {
        // B1.15 Fase 2a (FIX 5): filter mine. Sin cobranzas.view_all, restringimos a
        // los pagos cuya reserva tiene al user actual como ResponsibleUserId.
        var ownerScope = await GetOwnerScopeOrNullAsync(cancellationToken);

        var paymentsQuery = ApplyPaymentSearch(_dbContext.Payments.AsNoTracking(), query.Search);
        // ADR-022 §4.9 (fix S1-bis) + FC4 (2026-06-14): la lista global de pagos (GET /payments) proyecta a
        // PaymentDto, que expone Notes; los Payment puente (sobrepago Y saldo a favor aplicado) son respaldo
        // interno, no cobros reales -> se excluyen AMBOS. Predicado UNICO centralizado en AppliedCreditBridge
        // para no repetir las dos condiciones en cada sitio (y que no se desincronicen).
        paymentsQuery = paymentsQuery.Where(p => !(
            (p.Method == OverpaymentCreditCleanup.BridgeMethod && !p.AffectsCash && p.OriginalPaymentId != null)
            || (p.Method == AppliedCreditBridge.BridgeMethod && !p.AffectsCash && p.AppliedFromCreditWithdrawalId != null)));
        if (ownerScope is not null)
        {
            paymentsQuery = paymentsQuery.Where(p => p.Reserva != null && p.Reserva.ResponsibleUserId == ownerScope);
        }
        paymentsQuery = ApplyPaymentOrdering(paymentsQuery, query);

        return await paymentsQuery
            .AsNoTracking()
            .ProjectTo<PaymentDto>(_mapper.ConfigurationProvider)
            .ToPagedResponseAsync(query, cancellationToken);
    }

    public async Task<IEnumerable<PaymentDto>> GetPaymentsForReservaAsync(string reservaPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, cancellationToken);
        return await GetPaymentsForReservaAsync(reservaId, cancellationToken);
    }

    public async Task<PagedResponse<FinanceHistoryItemDto>> GetHistoryAsync(FinanceHistoryQuery query, CancellationToken cancellationToken)
    {
        var normalizedSearch = query.Search?.Trim().ToLowerInvariant();
        // B1.15 Fase 2a (FIX 5): filter mine. Sin cobranzas.view_all el historial expone
        // solo eventos cuya reserva esta a cargo del user. Los asientos de libro sin reserva
        // (ajustes manuales puros, pagos a proveedor "a cuenta") se excluyen para roles sin
        // view_all — si Caja necesita verlos, ese permiso es independiente (caja.view).
        var ownerScope = await GetOwnerScopeOrNullAsync(cancellationToken);

        // ADR-023 T2: la PLATA del historial sale del LIBRO DE CAJA (CashLedgerEntry), la MISMA fuente que
        // la pantalla de Caja (TreasuryService.GetMovementsAsync). Antes se unian Payments + ManualCashMovements
        // al vuelo, con tres bugs: (1) el puente de sobrepago aparecia como fila fantasma negativa; (2) los
        // pagos a proveedor eran invisibles; (3) una anulacion (soft-delete) DESAPARECIA en vez de mostrarse.
        // Leyendo del libro los tres se resuelven por construccion: el puente no tiene asiento (AffectsCash=false),
        // los pagos a proveedor SI tienen asiento, y una anulacion es un par original+reversa visible.
        var ledgerBase = _dbContext.CashLedgerEntries.AsNoTracking().AsQueryable();
        var invoicesBase = _dbContext.Invoices.AsNoTracking().AsQueryable();
        if (ownerScope is not null)
        {
            // Owner-scope: solo asientos de una reserva a cargo del user. Los asientos SIN reserva
            // (pagos a proveedor a cuenta, ajustes manuales puros) quedan fuera — correcto: un Vendedor
            // no ve el libro completo (M4 del review).
            ledgerBase = ledgerBase.Where(e => e.Reserva != null && e.Reserva.ResponsibleUserId == ownerScope);
            invoicesBase = invoicesBase.Where(i => i.Reserva != null && i.Reserva.ResponsibleUserId == ownerScope);
        }

        var ledgerRows = ledgerBase
            .Where(e => string.IsNullOrWhiteSpace(normalizedSearch) ||
                e.Method.ToLower().Contains(normalizedSearch) ||
                (e.Payment != null && e.Payment.Reference != null && e.Payment.Reference.ToLower().Contains(normalizedSearch)) ||
                (e.Payment != null && e.Payment.Notes != null && e.Payment.Notes.ToLower().Contains(normalizedSearch)) ||
                (e.SupplierPayment != null && e.SupplierPayment.Reference != null && e.SupplierPayment.Reference.ToLower().Contains(normalizedSearch)) ||
                (e.SupplierPayment != null && e.SupplierPayment.Notes != null && e.SupplierPayment.Notes.ToLower().Contains(normalizedSearch)) ||
                (e.ManualCashMovement != null && e.ManualCashMovement.Description.ToLower().Contains(normalizedSearch)) ||
                (e.ManualCashMovement != null && e.ManualCashMovement.Reference != null && e.ManualCashMovement.Reference.ToLower().Contains(normalizedSearch)) ||
                (e.Reserva != null && e.Reserva.NumeroReserva.ToLower().Contains(normalizedSearch)) ||
                (e.Supplier != null && e.Supplier.Name.ToLower().Contains(normalizedSearch)))
            .Select(e => new FinanceHistoryItemDto
            {
                // PublicId del ORIGEN (no del asiento), igual que TreasuryService: el front lo usa para abrir
                // el cobro/pago/movimiento. Para una reversa, conserva el FK de origen, asi apunta al mismo.
                PublicId =
                    e.Payment != null ? e.Payment.PublicId
                    : e.SupplierPayment != null ? e.SupplierPayment.PublicId
                    : e.ManualCashMovement != null ? e.ManualCashMovement.PublicId
                    : e.PublicId,
                // EntityType: se conserva "payment" para cobros de cliente y "movement" para el resto, para
                // minimizar el impacto en el front actual (decision documentada, recomendada por el ADR).
                EntityType = e.SourceType == CashLedgerSourceTypes.CustomerPayment ? "payment" : "movement",
                OccurredAt = e.OccurredAt,
                // Signo: ingreso positivo, egreso negativo. Para una reversa el signo ya viene invertido en su
                // Direction (la reversa de un Income es un Expense), asi que el par original+reversa netea 0.
                Amount = e.Direction == CashMovementDirections.Expense ? -e.Amount : e.Amount,
                // Kind/Title derivados del origen. Una reversa se rotula "Anulacion" para que se distinga del
                // hecho original (INV-T2-2: la anulacion nunca pasa desapercibida).
                Kind = e.IsReversal ? "Anulacion"
                    : e.SourceType == CashLedgerSourceTypes.CustomerPayment ? "Cobranza"
                    : e.SourceType == CashLedgerSourceTypes.SupplierPayment ? "Pago a proveedor"
                    : e.SourceType == CashLedgerSourceTypes.OperatorRefund ? "Devolucion de operador"
                    : e.SourceType == CashLedgerSourceTypes.ClientCreditWithdrawal ? "Devolucion al cliente"
                    : "Caja",
                Title = e.IsReversal
                    ? (e.SourceType == CashLedgerSourceTypes.CustomerPayment ? "Anulacion de cobranza"
                        : e.SourceType == CashLedgerSourceTypes.SupplierPayment ? "Anulacion de pago a proveedor"
                        : "Anulacion de movimiento")
                    : e.SourceType == CashLedgerSourceTypes.CustomerPayment ? "Cobranza recibida"
                    : e.SourceType == CashLedgerSourceTypes.SupplierPayment ? "Pago a proveedor"
                    : e.SourceType == CashLedgerSourceTypes.OperatorRefund ? "Devolucion recibida del operador"
                    : e.SourceType == CashLedgerSourceTypes.ClientCreditWithdrawal ? "Devolucion fisica al cliente"
                    : (e.ManualCashMovement != null ? e.ManualCashMovement.Description : "Movimiento de caja"),
                Subtitle =
                    e.Reserva != null ? "Reserva " + e.Reserva.NumeroReserva
                    : e.Supplier != null ? e.Supplier.Name
                    : e.ManualCashMovement != null && e.ManualCashMovement.Reference != null ? e.ManualCashMovement.Reference
                    : "Sin reserva",
                ReservaPublicId = e.Reserva != null ? (Guid?)e.Reserva.PublicId : null,
                NumeroReserva = e.Reserva != null ? e.Reserva.NumeroReserva : null,
                Reference =
                    e.Payment != null ? e.Payment.Reference
                    : e.SupplierPayment != null ? e.SupplierPayment.Reference
                    : e.ManualCashMovement != null ? e.ManualCashMovement.Reference
                    : null,
                Method = e.Method,
                PaymentEntryType = null,
                ReceiptPublicId = e.Payment != null && e.Payment.Receipt != null ? (Guid?)e.Payment.Receipt.PublicId : null,
                ReceiptNumber = e.Payment != null && e.Payment.Receipt != null ? e.Payment.Receipt.ReceiptNumber : null,
                ReceiptStatus = e.Payment != null && e.Payment.Receipt != null ? e.Payment.Receipt.Status : null,
                InvoiceTipoComprobante = null,
                InvoiceResultado = null,
                InvoiceWasForced = false,
                InvoiceForceReason = null,
                // MovementSourceType: se COLAPSA para compat del front (igual que TreasuryService:319-322):
                // CustomerPayment/SupplierPayment se conservan, todo lo demas viaja como "ManualAdjustment".
                MovementSourceType =
                    e.SourceType == CashLedgerSourceTypes.CustomerPayment ? "CustomerPayment"
                    : e.SourceType == CashLedgerSourceTypes.SupplierPayment ? "SupplierPayment"
                    : "ManualAdjustment",
                MovementDirection = e.Direction,
                // IsManual = todo lo que no es cobro de cliente ni pago directo a proveedor (ajustes, cancelaciones).
                IsManual = e.SourceType != CashLedgerSourceTypes.CustomerPayment
                        && e.SourceType != CashLedgerSourceTypes.SupplierPayment,
                Currency = e.Currency,          // moneda REAL de caja. Fin del bug "todo ARS".
                IsReversal = e.IsReversal,
                AmountMasked = false,           // se setea en el masking post-paginacion (T2.4)
                // LedgerSourceType CRUDO (no colapsado): sobre este campo decide el enmascarado de costo (B2).
                LedgerSourceType = e.SourceType,
                // ADR-024 item 4: vinculo informativo cobro->factura. Solo aplica a cobros de cliente (los
                // que tienen Payment); el resto queda null. No afecta saldos ni enmascarado.
                LinkedInvoicePublicId = e.Payment != null && e.Payment.LinkedInvoice != null
                    ? (Guid?)e.Payment.LinkedInvoice.PublicId
                    : null
            });

        // ADR-023 T2.3: las filas de COMPROBANTE (factura/NC/ND) NO son caja: se conservan desde Invoices.
        // Decision del dueño (OPS-INV-001): se muestran TODAS, marcadas claro por estado — no se filtra por
        // Resultado. Una factura rechazada ("R") o anulada (AnnulmentStatus=Succeeded) se muestra con un
        // Title/Kind que lo dice, para que nunca pase por aprobada ni desaparezca.
        var invoices = invoicesBase
            .Where(invoice => string.IsNullOrWhiteSpace(normalizedSearch) ||
                invoice.ForceReason != null && invoice.ForceReason.ToLower().Contains(normalizedSearch) ||
                invoice.Reserva != null && invoice.Reserva.NumeroReserva.ToLower().Contains(normalizedSearch) ||
                invoice.NumeroComprobante.ToString().Contains(normalizedSearch))
            .Select(invoice => new FinanceHistoryItemDto
            {
                PublicId = invoice.PublicId,
                EntityType = "invoice",
                OccurredAt = invoice.CreatedAt,
                Amount = invoice.ImporteTotal,
                // Kind distingue el ESTADO del comprobante para que el front lo marque sin ambiguedad.
                // Resultado de ARCA: "A"=aprobado, "R"=rechazado, "PENDING"=en proceso (NUNCA "P" — m1 del review).
                Kind = invoice.AnnulmentStatus == AnnulmentStatus.Succeeded ? "Comprobante anulado"
                    : invoice.Resultado == "R" ? "Comprobante rechazado"
                    : invoice.Resultado == "PENDING" ? "Comprobante en proceso"
                    : invoice.TipoComprobante == 3 || invoice.TipoComprobante == 8 || invoice.TipoComprobante == 13 || invoice.TipoComprobante == 53
                        ? "Nota de credito"
                        : "Factura AFIP",
                Title =
                    (invoice.AnnulmentStatus == AnnulmentStatus.Succeeded ? "[Anulada] "
                        : invoice.Resultado == "R" ? "[Rechazada por ARCA] "
                        : invoice.Resultado == "PENDING" ? "[En proceso] "
                        : "") +
                    (invoice.TipoComprobante == 1 ? "Factura A" :
                    invoice.TipoComprobante == 6 ? "Factura B" :
                    invoice.TipoComprobante == 11 ? "Factura C" :
                    invoice.TipoComprobante == 3 ? "Nota de Credito A" :
                    invoice.TipoComprobante == 8 ? "Nota de Credito B" :
                    invoice.TipoComprobante == 13 ? "Nota de Credito C" :
                    invoice.TipoComprobante == 2 ? "Nota de Debito A" :
                    invoice.TipoComprobante == 7 ? "Nota de Debito B" :
                    invoice.TipoComprobante == 12 ? "Nota de Debito C" :
                    invoice.TipoComprobante == 51 ? "Factura M" :
                    invoice.TipoComprobante == 52 ? "Nota de Debito M" :
                    invoice.TipoComprobante == 53 ? "Nota de Credito M" :
                    "Comp. " + invoice.TipoComprobante),
                Subtitle = invoice.Reserva != null
                    ? "Reserva " + invoice.Reserva.NumeroReserva
                    : "Sin reserva",
                ReservaPublicId = invoice.Reserva != null ? (Guid?)invoice.Reserva.PublicId : null,
                NumeroReserva = invoice.Reserva != null ? invoice.Reserva.NumeroReserva : null,
                Reference = invoice.NumeroComprobante.ToString(),
                Method = null,
                PaymentEntryType = null,
                ReceiptPublicId = null,
                ReceiptNumber = null,
                ReceiptStatus = null,
                InvoiceTipoComprobante = invoice.TipoComprobante,
                InvoiceResultado = invoice.Resultado,
                InvoiceWasForced = invoice.WasForced,
                InvoiceForceReason = invoice.ForceReason,
                MovementSourceType = null,
                MovementDirection = null,
                IsManual = false,
                Currency = null,                // un comprobante no es caja: no lleva moneda de caja
                IsReversal = false,
                AmountMasked = false,
                LedgerSourceType = null,
                LinkedInvoicePublicId = null    // una fila de comprobante no es un cobro: sin vinculo informativo
            });

        // ADR-023 T2.3: el Payment tecnico con EntryType=CreditNoteReversal (AffectsCash=false) YA NO se muestra
        // como fila "Reversion". Razon: no tiene asiento en el libro (no movio caja) y la NC que representa ya
        // aparece por la rama Invoices como comprobante. Mantener la fila "Reversion" duplicaba la NC. La
        // trazabilidad fiscal se conserva en Invoices; aca solo se elimina una representacion redundante.

        var timeline = ledgerRows.Concat(invoices);

        timeline = query.IsSortDescending()
            ? timeline.OrderByDescending(item => item.OccurredAt).ThenByDescending(item => item.PublicId)
            : timeline.OrderBy(item => item.OccurredAt).ThenBy(item => item.PublicId);

        var page = await timeline.ToPagedResponseAsync(query, cancellationToken);

        // ADR-023 T2.4: enmascarado de COSTO, simetrico con Caja (TreasuryService.GetMovementsAsync). Sin
        // cobranzas.see_cost, los egresos que son informacion de costo se ocultan: pagos a proveedor y
        // devoluciones recibidas del operador. IMPORTANTE (review B2): la decision se toma sobre el
        // LedgerSourceType CRUDO, nunca sobre el MovementSourceType colapsado del front (que mete el
        // OperatorRefund dentro de "ManualAdjustment" y filtraria el costo). CostMasking es fail-closed:
        // sin HttpContext/resolver, oculta. NO se enmascaran cobros de cliente, ajustes manuales genuinos
        // ni la devolucion fisica al cliente.
        if (!await CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, cancellationToken))
        {
            foreach (var item in page.Items.Where(i =>
                i.LedgerSourceType == CashLedgerSourceTypes.SupplierPayment ||
                i.LedgerSourceType == CashLedgerSourceTypes.OperatorRefund))
            {
                item.Amount = 0m;
                item.AmountMasked = true;
            }
        }

        return page;
    }

    public async Task<IEnumerable<PaymentDto>> GetPaymentsForReservaAsync(int ReservaId, CancellationToken cancellationToken)
    {
        return await _dbContext.Payments
            .AsNoTracking()
            .Include(p => p.Receipt)
            .Where(p => p.ReservaId == ReservaId)
            // ADR-022 §4.9 (fix S1-bis): excluir el Payment puente del saldo a favor (respaldo interno,
            // AffectsCash=false, monto negativo). Esta es la lista que consume el front en /payments/reserva/{id}
            // (PaymentModal/ReservaDetailPage): cada fila se renderiza con boton borrar/editar, asi que mostrar
            // el puente invitaria al usuario a borrarlo. "Recaudado" se calcula sumando esta lista en el front,
            // por lo que ocultar el puente hace que muestre lo que el cliente pagó de verdad; el saldo grande de
            // la reserva se calcula aparte (server-side) y no cambia.
            // FC4 (2026-06-14): excluir AMBOS puentes (sobrepago + saldo a favor aplicado). El de aplicacion
            // es positivo, asi que sin esto aparece como un "cobro" extra en el historial de la reserva.
            .Where(p => !(
                (p.Method == OverpaymentCreditCleanup.BridgeMethod && !p.AffectsCash && p.OriginalPaymentId != null)
                || (p.Method == AppliedCreditBridge.BridgeMethod && !p.AffectsCash && p.AppliedFromCreditWithdrawalId != null)))
            .OrderByDescending(p => p.PaidAt)
            .ProjectTo<PaymentDto>(_mapper.ConfigurationProvider)
            .ToListAsync(cancellationToken);
    }

    public async Task<PaymentReceiptDto> IssueReceiptAsync(string paymentPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var paymentId = await ResolveRequiredIdAsync<Payment>(paymentPublicIdOrLegacyId, cancellationToken);
        return await IssueReceiptAsync(paymentId, cancellationToken);
    }

    public async Task<PaymentDto> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        var reservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(request.ReservaId, cancellationToken);
        var reserva = await _dbContext.Reservas
            .FirstOrDefaultAsync(r => r.Id == reservaId, cancellationToken);

        if (reserva == null)
            throw new ArgumentException("Reserva no encontrada.");

        // B1.15 Fase 2a (review final): ownership check para POST /api/payments.
        // El attribute RequireOwnership no aplica porque la reserva viene en el body,
        // no en la ruta. Si el user no tiene cobranzas.view_all (Admin/Colaborador) y
        // no es responsable de la reserva, rechazar.
        var ownerScope = await GetOwnerScopeOrNullAsync(cancellationToken);
        if (ownerScope is not null && !string.Equals(reserva.ResponsibleUserId, ownerScope, StringComparison.Ordinal))
        {
            // Sentinel "__no_user__" o user distinto: rechazo. Controller traduce a 403.
            throw new UnauthorizedAccessException("La reserva no esta asignada al usuario actual.");
        }

        // ADR-035 (2026-06-19): PRIMERA COMPUERTA de capacidades. La politica de dominio decide si el estado
        // admite registrar cobro (estado cobrable + deuda). Es la MISMA regla que el front lee para apagar el
        // boton "registrar cobro" con motivo, asi front y back no divergen. NO reemplaza al guard fino de abajo:
        // EnsureCollectable() sigue siendo la defensa final (Balance>0 con el saldo fresco). InvalidOperationException
        // -> 409 en PaymentsController.CreatePayment.
        var paymentCapability = ReservaCapabilityPolicy.For(BuildCapabilityContext(reserva)).CanRegisterPayment;
        if (!paymentCapability.Allowed)
            throw new InvalidOperationException(paymentCapability.Reason);

        // ADR-032 (2026-06-15): regla UNICA de estado cobrable. Antes esto solo bloqueaba Budget y dejaba
        // cobrar en Quotation/Lost/Cancelled/Closed/PendingOperatorRefund/Archived (todos fuera de
        // ActiveCollectionStatuses). Ahora converge en el guard de dominio: solo se cobra en estados
        // cobrables. Lanza InvalidOperationException (mapeada a 409 en PaymentsController.CreatePayment).
        reserva.EnsureCollectable();

        if (request.Amount <= 0)
            throw new ArgumentException("El monto debe ser mayor a 0.");

        var amount = EconomicRulesHelper.RoundCurrency(request.Amount);

        // ADR-021 Capa 7: el usuario puede elegir la fecha del cobro (paidAt). Si no la manda, es ahora.
        // Se lleva a UTC siempre: la columna es timestamptz y EF Core exige Kind=Utc (un DateTime con Kind
        // Local/Unspecified explotaria al guardar en Postgres). Un valor en el pasado es legitimo (cobro
        // registrado tarde): no se bloquea, solo se normaliza la zona.
        var paidAt = NormalizeToUtc(request.PaidAt) ?? DateTime.UtcNow;

        // ADR-021 Capa 4 (§8): el bloque de moneda/TC se valida y resuelve server-side (no se confia en
        // el front). Para un request sin datos de moneda (front viejo) queda ARS no cruzado = identico a hoy.
        var moneyBlock = TravelApi.Domain.Reservations.PaymentCurrencyResolver.Resolve(
            amount: amount,
            rawCurrency: request.Currency,
            rawImputedCurrency: request.ImputedCurrency,
            exchangeRate: request.ExchangeRate,
            exchangeRateSource: request.ExchangeRateSource,
            exchangeRateAt: request.ExchangeRateAt,
            imputedAmount: request.ImputedAmount,
            round: EconomicRulesHelper.RoundCurrency);

        // ADR-024 item 4 (vinculo INFORMATIVO cobro<->factura, 2026-06-12): si el request trae una factura
        // a vincular, la resolvemos y validamos que sea de la MISMA reserva del cobro. El vinculo NO toca
        // saldos ni congela el cobro; es solo un dato de presentacion. Validar la pertenencia evita
        // vincular un cobro a una factura de otra reserva (dato fiscal incoherente).
        var linkedInvoiceId = await ResolveLinkedInvoiceIdOrNullAsync(
            request.LinkedInvoicePublicId, reservaId, cancellationToken);

        var payment = new Payment
        {
            ReservaId = reservaId,
            LinkedInvoiceId = linkedInvoiceId,
            Amount = amount,
            Currency = moneyBlock.Currency,
            ImputedCurrency = moneyBlock.ImputedCurrency,
            ExchangeRate = moneyBlock.ExchangeRate,
            ExchangeRateSource = moneyBlock.ExchangeRateSource,
            ExchangeRateAt = moneyBlock.ExchangeRateAt,
            ImputedAmount = moneyBlock.ImputedAmount,
            Method = string.IsNullOrWhiteSpace(request.Method) ? "Transfer" : request.Method.Trim(),
            Reference = request.Reference?.Trim(),
            Notes = request.Notes?.Trim(),
            PaidAt = paidAt,
            Status = "Paid",
            EntryType = PaymentEntryTypes.Payment,
            AffectsCash = true
        };

        _dbContext.Payments.Add(payment);

        // ADR-022 §4.4: el asiento de caja se escribe en la MISMA SaveChanges que el cobro. Solo los
        // pagos que mueven caja (AffectsCash) generan asiento; aca el alta normal siempre es AffectsCash.
        // La moneda del asiento es la REAL del pago (Payment.Currency), nunca la imputada.
        var (ledgerActorUserId, ledgerActorUserName) = ResolveLedgerActor();
        if (payment.AffectsCash)
        {
            var ledgerEntry = TravelApi.Domain.Helpers.CashLedgerEntryFactory.ForPayment(
                payment, ledgerActorUserId, ledgerActorUserName);
            _dbContext.CashLedgerEntries.Add(ledgerEntry);
        }

        // ARREGLO 1 (atomicidad del cobro, 2026-06-24): el alta de un cobro encadena VARIAS escrituras con su
        // propio SaveChanges (cobro + asiento de caja -> recalculo del saldo + comision -> conversion del
        // sobrepago en saldo a favor del cliente, que a su vez vuelve a recalcular). Antes corrian sueltas,
        // sin transaccion. Si el proceso se cortaba despues de crear el saldo a favor del cliente (credito +
        // puente) pero antes del recalculo final, el excedente quedaba contado DOS veces: como saldo a favor
        // del cliente Y como saldo negativo de la reserva. Las envolvemos todas en UNA transaccion (mismo
        // patron que FC4 / ClientCreditService): o se registra el cobro Y se ajusta todo junto (saldo,
        // sobrepago->credito, comision), o no se toca nada.
        //
        // OJO trainee/junior: la transaccion envolvente solo se puede usar contra un provider RELACIONAL.
        // Los unit tests corren sobre EF InMemory (no soporta transacciones). Por eso ramificamos por
        // IsRelational(): en InMemory ejecutamos el mismo cuerpo sin transaccion (la atomicidad real se
        // valida en integracion Postgres). Las SaveChanges internas participan de la transaccion ambiente
        // cuando existe y NO commitean hasta transaction.CommitAsync.
        if (_dbContext.Database.IsRelational())
        {
            var strategy = _dbContext.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                await PersistNewPaymentAsync();
                await transaction.CommitAsync(cancellationToken);
            });
        }
        else
        {
            await PersistNewPaymentAsync();
        }

        var created = await _dbContext.Payments
            .Include(p => p.Receipt)
            .FirstAsync(p => p.Id == payment.Id, cancellationToken);

        return _mapper.Map<PaymentDto>(created);

        // Cuerpo comun de persistencia (local function) para reusarlo dentro y fuera de la transaccion
        // envolvente sin duplicar codigo. Persiste el cobro + asiento, recalcula el saldo (que ademas
        // devenga la comision) y convierte el sobrepago en saldo a favor del cliente.
        async Task PersistNewPaymentAsync()
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            await RecalculateReservaBalanceAsync(reservaId, cancellationToken);

            // ADR-022 §4.9 (Q1): si el cobro dejo la reserva con saldo a FAVOR del cliente en la moneda del
            // pago, el excedente se convierte en saldo a favor del cliente (ClientCreditEntry) y la reserva
            // queda en 0. Es una IMPUTACION (mueve plata de "saldo de reserva" a "bolsillo del cliente"), NO
            // un movimiento de caja nuevo: el asiento del cobro ya reflejo la plata real que entro.
            await ConvertOverpaymentToClientCreditAsync(payment, ledgerActorUserId, ledgerActorUserName, cancellationToken);
        }
    }

    /// <summary>
    /// ADR-021 Capa 7: lleva una fecha de entrada a UTC para poder persistirla en Postgres (timestamptz
    /// exige Kind=Utc). Unspecified se asume UTC (es lo que manda el front, fecha sin zona); Local se
    /// convierte. Devuelve null si la entrada es null (el caller cae a DateTime.UtcNow).
    /// </summary>
    private static DateTime? NormalizeToUtc(DateTime? value)
    {
        if (value is null) return null;
        var dt = value.Value;
        return dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc)
        };
    }

    public async Task<PaymentReceiptDto> IssueReceiptAsync(int paymentId, CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters: necesitamos ver el Payment incluso si esta soft-deleted
        // para devolver el mensaje correcto. Sin esto, el query filter !IsDeleted lo
        // ocultaria y el caller recibiria "Pago no encontrado" (KeyNotFoundException
        // -> 404) en vez del 409 "anulado/eliminado" que es semanticamente correcto.
        var payment = await _dbContext.Payments
            .IgnoreQueryFilters()
            .Include(p => p.Reserva)
            .Include(p => p.Receipt)
            .FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);

        if (payment == null)
            throw new KeyNotFoundException("Pago no encontrado.");

        // B1.15 (2026-05-11): no emitir comprobantes sobre pagos eliminados o
        // cancelados. ARCA + Contable: un recibo correlativo solo puede emitirse
        // sobre un movimiento vivo. Si el pago esta soft-deleted o Cancelled,
        // emitir un recibo dejaria numero correlativo huerfano y trazabilidad rota.
        if (payment.IsDeleted || string.Equals(payment.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "No se puede emitir el comprobante porque el pago esta anulado o eliminado.");
        }

        var entryType = string.IsNullOrWhiteSpace(payment.EntryType)
            ? PaymentEntryTypes.Payment
            : payment.EntryType;

        if (entryType != PaymentEntryTypes.Payment || payment.Amount <= 0)
            throw new InvalidOperationException("Solo los pagos positivos pueden emitir comprobante.");

        // FC4 (2026-06-14): el Payment puente de un saldo a favor APLICADO es positivo (pasa el filtro de
        // arriba) pero NO es un cobro real — es el respaldo interno de aplicar el bolsillo a esta reserva, no
        // entro plata a caja. Emitir un recibo/comprobante de caja sobre el seria fiscalmente falso. Se
        // bloquea explicitamente (los campos escalares del puente ya vienen cargados en este query).
        if (AppliedCreditBridge.IsAppliedCreditBridge(payment))
            throw new InvalidOperationException(
                "No se puede emitir comprobante de un saldo a favor aplicado; no es un cobro real.");

        if (string.IsNullOrWhiteSpace(payment.EntryType))
        {
            payment.EntryType = PaymentEntryTypes.Payment;
        }

        if (payment.Receipt != null)
        {
            // B1.15 (2026-05-11): si ya existe un recibo Voided, NO reemitir
            // automaticamente — preservamos la numeracion historica y exigimos
            // intencion explicita. (Issue/emision posterior es un ticket aparte
            // que el plan deja como deuda; por ahora bloqueamos.)
            //
            // ARCA + Contable: numerar un recibo nuevo sobre un payment que ya
            // consumio numero correlativo previamente (anulado) requiere decision
            // operativa explicita y registro fiscal. Por ahora se documenta el
            // caso y se rechaza para no perder trazabilidad accidentalmente.
            if (string.Equals(payment.Receipt.Status, PaymentReceiptStatuses.Voided, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Ya existe un comprobante anulado N° {payment.Receipt.ReceiptNumber} para este pago. " +
                    "No se puede reemitir.");
            }

            // Issued (o cualquier otro estado): idempotente, devuelve el existente.
            return _mapper.Map<PaymentReceiptDto>(payment.Receipt);
        }

        var reservaIdForReceipt = payment.ReservaId
            ?? throw new InvalidOperationException("El pago no esta vinculado a una reserva.");

        var receipt = await CreateReceiptWithCorrelativeNumberAsync(
            payment.Id, reservaIdForReceipt, payment.Amount, cancellationToken);

        return _mapper.Map<PaymentReceiptDto>(receipt);
    }

    /// <summary>
    /// Crea y persiste el recibo interno con su numero correlativo, protegido contra numeros DUPLICADOS
    /// bajo concurrencia (dos cobros que emiten recibo a la vez, o un doble-click). El numero se calcula
    /// fuera de transaccion (CountAsync + 1), asi que sin esta proteccion dos emisiones simultaneas leerian
    /// el mismo Count y generarian el MISMO numero.
    ///
    /// La garantia real la da el indice UNIQUE en PaymentReceipt.ReceiptNumber (ver AppDbContext + migracion
    /// Adr034_M1): si dos inserts compiten por el mismo numero, Postgres rechaza al segundo con
    /// DbUpdateException. Aca lo atrapamos, recomputamos el numero y reintentamos. Mismo patron que
    /// AlertService.UpsertDismissalAsync.
    ///
    /// Nota: el provider InMemory de los tests NO aplica el UNIQUE, por lo que el camino de colision real
    /// solo se ejercita contra Postgres (integracion). Los unit tests validan el formato y que el reintento
    /// recomputa el numero (forzando un choque simulado en GenerateReceiptNumberAsync via override de prueba).
    /// </summary>
    private async Task<PaymentReceipt> CreateReceiptWithCorrelativeNumberAsync(
        int paymentId, int reservaId, decimal amount, CancellationToken cancellationToken)
    {
        // Hasta 5 intentos: cada colision de numero consume un intento. 5 es holgado para el peor caso
        // realista de esta agencia (un punado de cajeros emitiendo a la vez); si se agotaran, propagamos
        // la DbUpdateException original en vez de devolver un recibo con numero potencialmente duplicado.
        const int maxAttempts = 5;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var receipt = new PaymentReceipt
            {
                PaymentId = paymentId,
                ReservaId = reservaId,
                Amount = amount,
                IssuedAt = DateTime.UtcNow,
                Status = PaymentReceiptStatuses.Issued,
                ReceiptNumber = await GenerateReceiptNumberAsync(cancellationToken)
            };

            _dbContext.PaymentReceipts.Add(receipt);

            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                return receipt;
            }
            catch (DbUpdateException) when (attempt < maxAttempts)
            {
                // Carrera: otro recibo gano el mismo numero correlativo y el UNIQUE de Postgres nos
                // rechazo. Sacamos el receipt fallido del tracker, recomputamos el numero (el CountAsync
                // ahora ve la fila del ganador) y reintentamos.
                _dbContext.Entry(receipt).State = EntityState.Detached;

                _logger.LogWarning(
                    "Colision de numero de recibo para el pago {PaymentId} (intento {Attempt}). Recomputando y reintentando.",
                    paymentId, attempt);
            }
        }

        // Inalcanzable: el ultimo intento NO esta cubierto por el filtro `when (attempt < maxAttempts)`,
        // asi que su DbUpdateException se propaga desde dentro del loop. Esta linea solo existe para que
        // el compilador acepte que el metodo siempre retorna o lanza.
        throw new InvalidOperationException(
            "No se pudo generar un numero de recibo unico tras varios intentos.");
    }

    public async Task<byte[]> GetReceiptPdfAsync(string paymentPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var paymentId = await ResolveRequiredIdAsync<Payment>(paymentPublicIdOrLegacyId, cancellationToken);
        return await GetReceiptPdfAsync(paymentId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task VoidReceiptAsync(
        string paymentPublicIdOrLegacyId,
        string? reason,
        string userId,
        string? userName,
        bool requesterIsAdmin,
        CancellationToken cancellationToken)
    {
        // Guard de longitud (sugerencia security + backend reviewer): la columna
        // VoidReason es varchar(500). Sin este check, un Reason > 500 chars
        // termina como DbUpdateException = 500 inesperado para el cliente.
        if (reason != null && reason.Length > 500)
        {
            throw new InvalidOperationException("El motivo no puede superar los 500 caracteres.");
        }

        var paymentId = await ResolveRequiredIdAsync<Payment>(paymentPublicIdOrLegacyId, cancellationToken);

        // IgnoreQueryFilters: el Payment podria estar soft-deleted en escenarios de race,
        // pero igual queremos resolver y devolver el mensaje correcto (no 404).
        var payment = await _dbContext.Payments
            .IgnoreQueryFilters()
            .Include(p => p.Receipt)
            .FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);

        if (payment == null)
        {
            throw new KeyNotFoundException("Pago no encontrado.");
        }

        var receipt = payment.Receipt;
        if (receipt == null || !string.Equals(receipt.Status, PaymentReceiptStatuses.Issued, StringComparison.OrdinalIgnoreCase))
        {
            // Caso: nunca tuvo receipt, o ya esta Voided. Ambos son operaciones invalidas
            // (la 2da ademas es idempotente desde la perspectiva del cliente, pero el
            // contract devuelve 409 para que el frontend refresque su estado y no
            // muestre el boton).
            throw new InvalidOperationException("El comprobante no existe o ya esta anulado.");
        }

        // B1.15 Fase D (2026-05-11): workflow de aprobacion. Si policy requiere
        // aprobacion Y caller no es Admin, exigir ApprovalRequest aprobado.
        // Admin bypassa el workflow (mismo patron que InvoiceService.EnqueueAnnulmentAsync).
        int? approvalRequestId = null;
        if (!requesterIsAdmin && _approvalPolicyService is not null)
        {
            // Fallback true: si no hay policy persistida (DB vieja), exigir aprobacion.
            // Es la opcion conservadora para una operacion fiscal.
            var requiresApproval = await _approvalPolicyService.RequiresApprovalAsync(
                ApprovalRequestType.ReceiptVoidance, fallback: true, cancellationToken);

            if (requiresApproval)
            {
                if (_approvalService is null)
                {
                    throw new InvalidOperationException(
                        "Workflow de aprobaciones no disponible. Contactar al Administrador.");
                }

                // EntityType="PaymentReceipt" + EntityId=receipt.Id. La aprobacion
                // queda ligada al recibo especifico (no al pago) — evita "cheque en
                // blanco" sobre el mismo payment si reemite y revoluciona.
                var approval = await _approvalService.FindActiveApprovedAsync(
                    ApprovalRequestType.ReceiptVoidance, "PaymentReceipt", receipt.Id, userId, cancellationToken);
                if (approval is null)
                {
                    throw new ApprovalRequiredException(
                        ApprovalRequestType.ReceiptVoidance, "PaymentReceipt", receipt.Id);
                }
                approvalRequestId = approval.Id;
                if (string.IsNullOrWhiteSpace(reason))
                {
                    reason = approval.Reason; // hereda motivo declarado al pedir aprobacion
                }
            }
        }

        receipt.Status = PaymentReceiptStatuses.Voided;
        receipt.VoidedAt = DateTime.UtcNow;
        receipt.VoidedByUserId = string.IsNullOrWhiteSpace(userId) ? null : userId;
        receipt.VoidedByUserName = userName;
        receipt.VoidReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Audit trail business event. Best-effort: AuditService no debe romper la
        // operacion principal (su propio try/catch lo loggea).
        if (_auditService is not null)
        {
            await _auditService.LogBusinessEventAsync(
                action: "ReceiptVoided",
                entityName: "PaymentReceipt",
                entityId: receipt.Id.ToString(),
                details: receipt.VoidReason,
                userId: userId,
                userName: userName,
                ct: cancellationToken);
        }
        else
        {
            _logger.LogInformation(
                "PaymentReceipt voided. ReceiptId={ReceiptId} ReceiptNumber={ReceiptNumber} PaymentId={PaymentId} ByUser={UserId} Reason={Reason}",
                receipt.Id, receipt.ReceiptNumber, payment.Id, userId, receipt.VoidReason ?? "<none>");
        }

        // Consumir la aprobacion ahora que la accion se ejecuto exitosamente.
        if (approvalRequestId.HasValue && _approvalService is not null)
        {
            await _approvalService.MarkConsumedAsync(approvalRequestId.Value, cancellationToken);
        }
    }

    public async Task<byte[]> GetReceiptPdfAsync(int paymentId, CancellationToken cancellationToken)
    {
        var payment = await _dbContext.Payments
            .Include(p => p.Reserva)
            .ThenInclude(r => r!.Payer)
            .Include(p => p.Receipt)
            .FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);

        if (payment == null)
            throw new KeyNotFoundException("Pago no encontrado.");

        var receipt = payment.Receipt ?? throw new InvalidOperationException("El pago aun no tiene comprobante emitido.");
        var agency = await _dbContext.AgencySettings.FirstOrDefaultAsync(cancellationToken) ?? new AgencySettings();

        // ADR-041: datos bancarios de la AGENCIA para que el cliente sepa a donde transferir el saldo. Se consulta
        // BankAccount DIRECTO (no el BankAccountService) a proposito: ese servicio ENMASCARA el CBU para los
        // listados de pantalla, pero en el PDF el CBU debe ir COMPLETO (es dato propio de la agencia, destino de
        // transferencia). La funcion pura AgencyBankAccountSelector elige cual mostrar segun la moneda del cobro.
        var agencyBankAccounts = await _dbContext.BankAccounts
            .AsNoTracking()
            .Where(account => account.OwnerType == BankAccountOwnerType.Agency && account.IsActive)
            .ToListAsync(cancellationToken);

        var bankAccountsToShow = TravelApi.Domain.Helpers.AgencyBankAccountSelector
            .SelectForDocument(agencyBankAccounts, payment.Currency);

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.5f, Unit.Centimetre);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(x => x.FontSize(10));

                page.Header().Column(col =>
                {
                    col.Item().Text(agency.AgencyName).FontSize(20).Bold().FontColor(Colors.Blue.Medium);
                    col.Item().Text("Comprobante de pago interno").FontSize(11).SemiBold();
                    col.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
                });

                page.Content().Column(col =>
                {
                    col.Spacing(12);
                    col.Item().Text($"Comprobante {receipt.ReceiptNumber}").FontSize(16).Bold();
                    col.Item().Text($"Emitido: {receipt.IssuedAt:dd/MM/yyyy HH:mm}");
                    col.Item().Text($"Reserva: {payment.Reserva?.NumeroReserva ?? "-"}");
                    col.Item().Text($"Cliente: {payment.Reserva?.Payer?.FullName ?? "Consumidor Final"}");
                    col.Item().Text($"Metodo: {payment.Method}");
                    col.Item().Text($"Referencia: {payment.Reference ?? "-"}");
                    col.Item().Text($"Estado: {receipt.Status}");
                    col.Item().Text($"Importe: {payment.Amount.ToString("C2")}");
                    if (!string.IsNullOrWhiteSpace(payment.Notes))
                        col.Item().Text($"Notas: {payment.Notes}");

                    // ADR-041: pie con los datos bancarios de la agencia. Solo se reserva el slot si HAY cuentas
                    // a mostrar; QuestPDF rompe si a un IContainer no se le dibuja nada, asi que el if es necesario.
                    if (bankAccountsToShow.Count > 0)
                    {
                        col.Item().PaddingTop(6).Element(c =>
                            AgencyBankDetailsPdfSection.Compose(c, bankAccountsToShow));
                    }
                });

                page.Footer().AlignCenter().Text("Documento interno. No reemplaza la factura AFIP.").Italic().FontSize(9).FontColor(Colors.Grey.Darken1);
            });
        });

        return document.GeneratePdf();
    }

    public async Task<IEnumerable<object>> GetDeletedPaymentsAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.Payments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.IsDeleted)
            .OrderByDescending(p => p.DeletedAt)
            .Select(p => new {
                p.PublicId,
                p.Amount,
                p.Method,
                p.Reference,
                p.Status,
                p.PaidAt,
                p.DeletedAt,
                ReservaPublicId = p.Reserva != null ? (Guid?)p.Reserva.PublicId : null,
                NumeroReserva = p.Reserva != null
                    ? p.Reserva.NumeroReserva : null,
                FileName = p.Reserva != null
                    ? p.Reserva.Name : null,
                CustomerName = p.Reserva != null && p.Reserva.Payer != null
                    ? p.Reserva.Payer.FullName : null
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<Guid> RestorePaymentAsync(int id, CancellationToken cancellationToken)
    {
        var payment = await _dbContext.Payments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == id && p.IsDeleted, cancellationToken);

        if (payment == null)
            throw new KeyNotFoundException("Pago eliminado no encontrado.");

        payment.IsDeleted = false;
        payment.DeletedAt = null;

        // ADR-023 T2.5: restaurar un cobro re-asienta en el libro. El delete dejo el asiento original
        // IsReversed=true + su reversa (neto 0). Restaurar NO des-revierte (el libro nunca reescribe una
        // reversa): se crea un asiento vivo NUEVO equivalente al cobro, asi el neto vuelve a +Amount y la
        // historia queda completa (cobro + anulacion + re-cobro). Antes este metodo NO re-asentaba, lo que
        // dejaba un pago vivo sin asiento (par original+reversa neteando 0) — bug de integridad libro<->pagos.
        if (payment.AffectsCash)
        {
            // Guard de idempotencia: si ya hay un asiento vivo para este pago (p.ej. re-restaurar), no duplicar.
            var hasLive = await _dbContext.CashLedgerEntries
                .AnyAsync(e => e.PaymentId == payment.Id && !e.IsReversal && !e.IsReversed, cancellationToken);
            if (!hasLive)
            {
                var (userId, userName) = ResolveLedgerActor();
                var entry = TravelApi.Domain.Helpers.CashLedgerEntryFactory.ForPayment(payment, userId, userName);
                _dbContext.CashLedgerEntries.Add(entry);
            }
        }

        // ARREGLO 1 (atomicidad, 2026-06-24): restaurar un cobro encadena tambien varias escrituras con su
        // propio SaveChanges (limpiar IsDeleted + re-asentar caja -> recalcular saldo + comision -> reconstruir
        // el saldo a favor del sobrepago). Las envolvemos en UNA transaccion (mismo patron que el alta) para
        // que un corte a mitad no deje un cobro vivo sin asiento o el saldo a favor a medias. En InMemory
        // (tests) el provider no soporta transacciones: corremos el mismo cuerpo sin transaccion.
        if (_dbContext.Database.IsRelational())
        {
            var strategy = _dbContext.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                await PersistRestoredPaymentAsync();
                await transaction.CommitAsync(cancellationToken);
            });
        }
        else
        {
            await PersistRestoredPaymentAsync();
        }

        return payment.PublicId;

        // Cuerpo comun de la restauracion (local function). Persiste el cobro revivido + su asiento nuevo,
        // recalcula el saldo (devenga la comision) y reconstruye el saldo a favor del sobrepago si lo hubo.
        async Task PersistRestoredPaymentAsync()
        {
            // El SaveChanges va DESPUES del Add: limpiar IsDeleted y crear el asiento vivo deben ocurrir en la
            // MISMA transaccion. Si se guardara antes del Add, una caida entre ambos dejaria pago vivo sin asiento
            // (justo el bug que se esta arreglando).
            await _dbContext.SaveChangesAsync(cancellationToken);

            if (payment.ReservaId.HasValue)
            {
                await RecalculateReservaBalanceAsync(payment.ReservaId.Value, cancellationToken);

                // fix bug #9 (2026-06-17): al ANULAR el cobro, OverpaymentCreditCleanup limpio el saldo a favor
                // (credito + puente) que ese cobro habia generado. Al RESTAURARLO hay que reconstruirlo, si no
                // queda asimetrico: la reserva vuelve a quedar sobre-pagada con el excedente atrapado, invisible
                // al bolsillo del cliente y a FC4. La conversion es idempotente (solo actua si hay Balance < 0),
                // asi que re-restaurar no duplica el credito.
                //
                // DECISION (conservadora): NO se revalida EnsureCollectable aca. Restaurar es una operacion
                // ADMINISTRATIVA de deshacer una anulacion, no un cobro nuevo: el cobro ya existio y fue legitimo.
                // Gatearla por estado bloquearia deshacer una anulacion equivocada cuando la reserva ya paso a un
                // estado terminal (Cancelada/Perdida), que es justo cuando mas se necesita poder revertir. El alta
                // de cobro nuevo (CreatePaymentAsync / AddPaymentAsync) si exige EnsureCollectable; restaurar no.
                var (creditActorUserId, creditActorUserName) = ResolveLedgerActor();
                await ConvertOverpaymentToClientCreditAsync(
                    payment, creditActorUserId, creditActorUserName, cancellationToken);
            }
        }
    }

    public async Task<Guid> RestorePaymentAsync(string paymentPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var paymentId = await ResolveRequiredIdAsync<Payment>(paymentPublicIdOrLegacyId, cancellationToken);
        return await RestorePaymentAsync(paymentId, cancellationToken);
    }

    private async Task<int> ResolveRequiredIdAsync<TEntity>(string publicIdOrLegacyId, CancellationToken cancellationToken)
        where TEntity : class, IHasPublicId
    {
        var resolved = await _dbContext.Set<TEntity>()
            .AsNoTracking()
            .ResolveInternalIdAsync(publicIdOrLegacyId, cancellationToken);

        if (!resolved.HasValue && int.TryParse(publicIdOrLegacyId, out var legacyId))
        {
            resolved = legacyId;
        }

        return resolved ?? throw new KeyNotFoundException($"{typeof(TEntity).Name} no encontrado.");
    }

    /// <summary>
    /// Calcula el proximo numero correlativo de recibo: "RCP-{anio}-{secuencia:6 digitos}". La secuencia es
    /// el conteo GLOBAL de recibos + 1 (no se reinicia por anio; el anio del prefijo es solo etiqueta, por eso
    /// el numero nunca se repite aunque cambie el anio). Este calculo NO es atomico por si solo — la unicidad
    /// la garantiza el indice UNIQUE de ReceiptNumber + el reintento en CreateReceiptWithCorrelativeNumberAsync.
    ///
    /// virtual: los unit tests lo overridean para simular una colision en el primer intento (InMemory no
    /// aplica el UNIQUE, asi que no hay otra forma de ejercitar el camino de reintento sin Postgres).
    /// </summary>
    protected virtual async Task<string> GenerateReceiptNumberAsync(CancellationToken cancellationToken)
    {
        var next = await _dbContext.PaymentReceipts.CountAsync(cancellationToken) + 1;
        return $"RCP-{DateTime.UtcNow:yyyy}-{next:D6}";
    }

    // P1.5: el saldo se calcula con la FUENTE UNICA DE VERDAD (ReservaMoneyCalculator),
    // la misma que usa ReservaService.UpdateBalanceAsync. Antes esta copia sumaba PLANO
    // (sin el filtro CountsForReservaBalance), por lo que una reserva con servicios Cancelados
    // mostraba un saldo DISTINTO segun que accion lo recalculara (servicio vs pago vs factura).
    // Unificado -> el saldo es consistente y correcto sin importar que disparo el recalculo.
    private async Task RecalculateReservaBalanceAsync(int reservaId, CancellationToken cancellationToken)
    {
        // ADR-021 §4.1/§B5: delega en el persister consolidado (unico punto de escritura de la plata
        // de la reserva). Persiste escalar surrogate + tabla hija ReservaMoneyByCurrency en la misma
        // SaveChangesAsync. La fuente de la cuenta sigue siendo ReservaMoneyCalculator.
        await TravelApi.Infrastructure.Reservations.ReservaMoneyPersister.PersistAsync(_dbContext, reservaId, cancellationToken);
    }

    /// <summary>
    /// ADR-022: resuelve el actor (userId, userName) del HttpContext para escribir la auditoria del
    /// asiento de caja. En tests unitarios sin HttpContext devuelve (null, null) — el asiento queda
    /// con autor desconocido, lo que es aceptable (no rompe; solo pierde el "quien" en ese contexto).
    /// </summary>
    /// <summary>
    /// ADR-024 item 4: resuelve el PublicId de la factura a vincular (informativo) a su Id interno,
    /// validando que pertenezca a la reserva del cobro. Devuelve null si el request no trae vinculo.
    /// Lanza <see cref="ArgumentException"/> (el controller la traduce a 400) si el PublicId no existe,
    /// no es un Guid valido, o la factura es de OTRA reserva.
    /// </summary>
    private async Task<int?> ResolveLinkedInvoiceIdOrNullAsync(
        string? linkedInvoicePublicId, int reservaId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(linkedInvoicePublicId))
        {
            return null;
        }

        if (!Guid.TryParse(linkedInvoicePublicId.Trim(), out var publicId))
        {
            throw new ArgumentException("La factura a vincular no es valida.");
        }

        // Resolvemos por PublicId Y misma reserva en una sola consulta: si la factura es de otra reserva,
        // no la encontramos aca y rechazamos con el mismo mensaje (no filtramos si existe en otra reserva).
        var invoice = await _dbContext.Invoices
            .AsNoTracking()
            .Where(i => i.PublicId == publicId && i.ReservaId == reservaId)
            .Select(i => new { i.Id })
            .FirstOrDefaultAsync(cancellationToken);

        if (invoice is null)
        {
            throw new ArgumentException(
                "La factura a vincular no existe o no pertenece a la misma reserva del cobro.");
        }

        return invoice.Id;
    }

    private (string? UserId, string? UserName) ResolveLedgerActor()
    {
        var httpUser = _httpContextAccessor?.HttpContext?.User;
        if (httpUser is null) return (null, null);
        var userId = httpUser.FindFirstValue(ClaimTypes.NameIdentifier);
        var userName = httpUser.FindFirstValue(ClaimTypes.Name) ?? httpUser.Identity?.Name;
        return (string.IsNullOrWhiteSpace(userId) ? null : userId,
                string.IsNullOrWhiteSpace(userName) ? null : userName);
    }

    /// <summary>
    /// ADR-022 §4.5: marca el asiento vigente de un Payment como revertido e inserta su reversa,
    /// en el ORDEN estricto exigido por el indice unico parcial (marcar viejo IsReversed=true ANTES
    /// de Add de la reversa). NO hace SaveChanges — lo hace el caller dentro de su transaccion.
    /// Si el pago no tiene asiento vigente (legacy sin backfill todavia), no hace nada.
    /// </summary>
    private async Task ReverseLivePaymentLedgerEntryAsync(int paymentId, CancellationToken cancellationToken)
    {
        // ADR-022 §4.5: la mecanica de la reversa vive en CashLedgerPaymentReversal (punto unico, compartido
        // con el path legacy ReservaService.DeletePaymentAsync). Aca solo resolvemos el actor y delegamos.
        var (userId, userName) = ResolveLedgerActor();
        await TravelApi.Infrastructure.Reservations.CashLedgerPaymentReversal.ReverseLivePaymentEntryAsync(
            _dbContext, paymentId, userId, userName, cancellationToken);
    }

    /// <summary>
    /// ADR-022 §4.9 (Q1): convierte el SOBREPAGO de una reserva en saldo a favor del cliente.
    ///
    /// <para>Despues de recalcular el saldo, si la reserva quedo a favor del cliente en la moneda a la que
    /// se imputo el cobro (la fila <c>ReservaMoneyByCurrency.Balance &lt; 0</c> de esa moneda), el excedente
    /// se mueve al bolsillo del cliente como un <see cref="ClientCreditEntry"/> de origen "sobrepago" y la
    /// reserva se recalcula para que quede saldada en 0 en esa moneda. El bolsillo es POR MONEDA.</para>
    ///
    /// <para><b>NO mueve caja</b>: el cobro ya asento la plata real que entro; esto es una imputacion de
    /// posicion del cliente, no un egreso. <b>NO crea asiento de caja.</b></para>
    ///
    /// <para>Precondiciones para convertir: la reserva tiene un pagador (PayerId) y el excedente es &gt; 0.
    /// Sin pagador no hay bolsillo de cliente posible, se deja como esta (no rompe).</para>
    /// </summary>
    private async Task ConvertOverpaymentToClientCreditAsync(
        Payment payment,
        string? actorUserId,
        string? actorUserName,
        CancellationToken cancellationToken)
    {
        // fix bugs #6/#9 (2026-06-17): la mecanica de la conversion vive en OverpaymentCreditConverter
        // (punto unico, compartido con el path legacy ReservaService.AddPaymentAsync y con
        // RestorePaymentAsync). Aca solo delegamos para que el camino canonico quede byte-identico.
        await OverpaymentCreditConverter.ConvertAsync(
            _dbContext, payment, actorUserId, actorUserName, _logger, cancellationToken);
    }

    private static IQueryable<Payment> ApplyPaymentSearch(IQueryable<Payment> query, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return query;
        }

        var normalized = search.Trim().ToLowerInvariant();
        return query.Where(payment =>
            payment.Method.ToLower().Contains(normalized) ||
            payment.Reference != null && payment.Reference.ToLower().Contains(normalized) ||
            payment.Notes != null && payment.Notes.ToLower().Contains(normalized) ||
            payment.Reserva != null && payment.Reserva.NumeroReserva.ToLower().Contains(normalized));
    }

    private static IQueryable<Payment> ApplyPaymentOrdering(IQueryable<Payment> query, PaymentsListQuery request)
    {
        var sortBy = (request.SortBy ?? "paidAt").Trim().ToLowerInvariant();
        var desc = !string.Equals(request.SortDir, "asc", StringComparison.OrdinalIgnoreCase);

        return sortBy switch
        {
            "amount" => desc
                ? query.OrderByDescending(payment => payment.Amount).ThenByDescending(payment => payment.PaidAt)
                : query.OrderBy(payment => payment.Amount).ThenByDescending(payment => payment.PaidAt),
            "numeroreserva" => desc
                ? query.OrderByDescending(payment => payment.Reserva != null ? payment.Reserva.NumeroReserva : string.Empty).ThenByDescending(payment => payment.PaidAt)
                : query.OrderBy(payment => payment.Reserva != null ? payment.Reserva.NumeroReserva : string.Empty).ThenByDescending(payment => payment.PaidAt),
            _ => desc
                ? query.OrderByDescending(payment => payment.PaidAt).ThenByDescending(payment => payment.Id)
                : query.OrderBy(payment => payment.PaidAt).ThenBy(payment => payment.Id)
        };
    }

    public async Task UpdatePaymentAsync(string paymentPublicIdOrLegacyId, UpdatePaymentRequest request, CancellationToken cancellationToken)
    {
        var paymentId = await ResolveRequiredIdAsync<Payment>(paymentPublicIdOrLegacyId, cancellationToken);
        var payment = await _dbContext.Payments.FindAsync(new object[] { paymentId }, cancellationToken);

        if (payment == null)
            throw new KeyNotFoundException("Pago no encontrado.");

        // ADR-022 §4.9 (fix S1-bis): el Payment puente del saldo a favor NO se edita a mano. Cambiarle el monto
        // desincroniza el credito del bolsillo respecto del excedente que sacó de la reserva. Solo el sistema lo
        // manipula (OverpaymentCreditCleanup).
        if (OverpaymentCreditCleanup.IsOverpaymentBridge(payment))
        {
            _logger.LogWarning(
                "UpdatePaymentAsync rejected (direct overpayment-bridge mutation). PaymentId={PaymentId} ReservaId={ReservaId}.",
                paymentId, payment.ReservaId);
            throw new InvalidOperationException(OverpaymentCreditCleanup.DirectBridgeMutationBlockReason);
        }

        // FC4 (2026-06-14): mismo candado para el OTRO puente (saldo a favor aplicado). Editarlo a mano
        // desincroniza el bolsillo del cliente respecto de la deuda que pago en la reserva destino.
        if (AppliedCreditBridge.IsAppliedCreditBridge(payment))
        {
            _logger.LogWarning(
                "UpdatePaymentAsync rejected (direct applied-credit-bridge mutation). PaymentId={PaymentId} ReservaId={ReservaId}.",
                paymentId, payment.ReservaId);
            throw new InvalidOperationException(AppliedCreditBridge.DirectBridgeMutationBlockReason);
        }

        // ADR-033 (2026-06-16, E3/A2): habia quitado TODO gate de estado para editar (editar libremente lo
        // restringian solo los guards fiscal/puente). ADR-035 (2026-06-19) reintroduce un gate ACOTADO a los
        // estados TERMINALES: en {Closed, Cancelled, Lost, PendingOperatorRefund} editar/borrar NO se permite;
        // ahi la correccion es ANULAR con rastro (AnnulPaymentAsync, que NO pasa por esta compuerta a
        // proposito). Sigue permitido editar en los estados vivos (En gestion/Confirmada/...), asi que NO se
        // reabre la cobrabilidad de alta de ADR-033. Es la PRIMERA COMPUERTA; los guards fiscales/de puente de
        // abajo siguen siendo la defensa final. InvalidOperationException -> 409.
        await EnsurePaymentEditableByStateAsync(payment, cancellationToken);

        // B1.15 Fase 0' (CODE-01): inmutabilidad post-recibo / post-CAE. Editar
        // el monto/metodo/referencia de un pago con recibo emitido o ligado a
        // factura AFIP viva rompe la trazabilidad fiscal y la auditoria del
        // recibo entregado al cliente.
        var blockReason = await MutationGuards.GetPaymentMutationBlockReasonAsync(_dbContext, paymentId, cancellationToken);
        if (blockReason != null)
        {
            _logger.LogWarning(
                "UpdatePaymentAsync rejected. PaymentId={PaymentId} ReservaId={ReservaId}. Reason={Reason}",
                paymentId, payment.ReservaId, blockReason);
            throw new InvalidOperationException(blockReason);
        }

        // ADR-021 §2.8 (B3): un pago CRUZADO (moneda real != moneda imputada) es INMUTABLE en su bloque
        // economico. Editar el Amount sin recomputar el equivalente imputado dejaria la caja y el saldo
        // descuadrados; la regla MVP es anular + recrear (igual que no se reescribe una factura). Editar
        // Method/Reference/Notes (datos no economicos) si se permite -> se chequea si el Amount cambia.
        bool isCrossCurrency =
            payment.ImputedCurrency != null &&
            !string.Equals(payment.ImputedCurrency, payment.Currency, StringComparison.OrdinalIgnoreCase);
        if (isCrossCurrency)
        {
            var newAmount = EconomicRulesHelper.RoundCurrency(request.Amount);
            if (newAmount != payment.Amount)
            {
                _logger.LogWarning(
                    "UpdatePaymentAsync rejected (cross-currency amount edit). PaymentId={PaymentId} ReservaId={ReservaId}.",
                    paymentId, payment.ReservaId);
                throw new InvalidOperationException(
                    "No se puede editar el monto de un cobro en moneda distinta a la del saldo. Anulalo y registralo de nuevo.");
            }

            // Solo datos no economicos: el bloque de moneda/TC queda intacto.
            payment.Method = request.Method;
            payment.Reference = request.Reference;
            payment.Notes = request.Notes;
            await _dbContext.SaveChangesAsync(cancellationToken);
            if (payment.ReservaId.HasValue)
                await RecalculateReservaBalanceAsync(payment.ReservaId.Value, cancellationToken);
            return;
        }

        // ADR-022 §4.9 (fix S1): si este cobro genero un saldo a favor de sobrepago YA usado, no se permite
        // editar el monto (subir o bajar): recomputar el excedente destruiria la historia de consumo del
        // bolsillo del cliente. Hay que anular primero el uso de ese saldo a favor. Si el credito esta
        // intacto, se revierte el puente/credito y se RE-CREA desde cero contra el nuevo monto (recalculo
        // limpio), que es la opcion mas auditable: el excedente nuevo nace de la cuenta nueva, no se parchea.
        bool amountChanges = EconomicRulesHelper.RoundCurrency(request.Amount) != payment.Amount;
        if (amountChanges)
        {
            var overpaymentBlock = await OverpaymentCreditCleanup.GetConsumedBlockReasonAsync(_dbContext, paymentId, cancellationToken);
            if (overpaymentBlock != null)
            {
                _logger.LogWarning(
                    "UpdatePaymentAsync rejected (overpayment credit already consumed). PaymentId={PaymentId} ReservaId={ReservaId}.",
                    paymentId, payment.ReservaId);
                throw new InvalidOperationException(overpaymentBlock);
            }
            var (cleanupActorUserId, cleanupActorUserName) = ResolveLedgerActor();
            await OverpaymentCreditCleanup.ReverseOverpaymentArtifactsAsync(
                _dbContext, paymentId, cleanupActorUserId, cleanupActorUserName, cancellationToken);
        }

        payment.Amount = EconomicRulesHelper.RoundCurrency(request.Amount);
        payment.Method = request.Method;
        payment.Reference = request.Reference;
        payment.Notes = request.Notes;

        // ADR-022 §4.5: editar el monto = reversa del asiento viejo + asiento nuevo, en una sola
        // SaveChanges y en este orden (marcar viejo IsReversed ANTES de insertar la reversa y el nuevo,
        // para no violar el indice unico parcial). El libro conserva el rastro viejo (+) -> reversa (-)
        // -> nuevo; la historia no se reescribe. Solo aplica a pagos que mueven caja.
        if (payment.AffectsCash)
        {
            await ReverseLivePaymentLedgerEntryAsync(payment.Id, cancellationToken);
            var (actorUserId, actorUserName) = ResolveLedgerActor();
            var newEntry = TravelApi.Domain.Helpers.CashLedgerEntryFactory.ForPayment(
                payment, actorUserId, actorUserName);
            _dbContext.CashLedgerEntries.Add(newEntry);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        if (payment.ReservaId.HasValue)
            await RecalculateReservaBalanceAsync(payment.ReservaId.Value, cancellationToken);

        // ADR-022 §4.9 (fix S1): tras recalcular con el monto NUEVO, si el cobro sigue sobre-pagando la
        // reserva, se vuelve a crear el saldo a favor por el EXCEDENTE NUEVO (puente + credito frescos). Si
        // el nuevo monto ya no sobrepaga, no crea nada. Solo si el monto cambio (los artefactos viejos ya se
        // revirtieron arriba); editar Method/Reference/Notes sin tocar el monto no toca el sobrepago.
        if (amountChanges && payment.ReservaId.HasValue)
        {
            var (recreateActorUserId, recreateActorUserName) = ResolveLedgerActor();
            await ConvertOverpaymentToClientCreditAsync(payment, recreateActorUserId, recreateActorUserName, cancellationToken);
        }
    }

    public async Task DeletePaymentAsync(string paymentPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var paymentId = await ResolveRequiredIdAsync<Payment>(paymentPublicIdOrLegacyId, cancellationToken);
        var payment = await _dbContext.Payments.FindAsync(new object[] { paymentId }, cancellationToken);

        if (payment == null)
            throw new KeyNotFoundException("Pago no encontrado.");

        // ADR-032 (review): guard de re-entrancy (anti doble-anulacion). FindAsync ignora el filtro global
        // !IsDeleted, asi que un cobro YA anulado puede cargarse aca. Sin este check, borrarlo de nuevo
        // re-correria la limpieza de sobrepago y emitiria un segundo evento de auditoria (el contra-asiento
        // ya es idempotente, pero igual ensucia el rastro). Mismo patron que IssueReceiptAsync (~636).
        EnsureNotAlreadyAnnulled(payment);

        EnsureNotBridge(payment, "DeletePaymentAsync");

        // ADR-033 (2026-06-16, E3/A2): habia quitado el gate de estado para borrar. ADR-035 (2026-06-19) lo
        // reintroduce ACOTADO a los TERMINALES {Closed, Cancelled, Lost, PendingOperatorRefund}: en esos
        // estados un cobro NO se borra, se ANULA con rastro (AnnulPaymentAsync, que NO pasa por esta compuerta
        // a proposito — sigue siendo la salida valida en terminal y deja PaymentAnnulled). En los estados
        // vivos el DELETE sigue permitido y lo restringen los guards fiscal (dentro de DeletePaymentCoreAsync)
        // y de puente (arriba). PRIMERA COMPUERTA; defensa final intacta. InvalidOperationException -> 409.
        await EnsurePaymentEditableByStateAsync(payment, cancellationToken);

        await DeletePaymentCoreAsync(payment, cancellationToken);
    }

    /// <summary>
    /// ADR-032 (2026-06-15, B2 Opcion 1): ANULAR un cobro CON RASTRO. A diferencia del DELETE libre, esta
    /// accion SI opera en reservas terminales (cancelada/cerrada): es la unica forma valida de corregir un
    /// cobro mal cargado en una reserva que ya no es cobrable. NO inventa nada nuevo: reusa exactamente el
    /// mismo mecanismo de reversa que el DELETE (soft-delete <see cref="Payment.IsDeleted"/> + contra-asiento
    /// de caja ADR-022 + limpieza de sobrepago), pero EXENTA del gate de estado de §4.3.
    ///
    /// <para>Mantiene TODOS los guards de integridad: no se anula un puente a mano (lo hace el sistema), y un
    /// cobro con recibo emitido / CAE vivo sigue bloqueado por el guard fiscal — el camino correcto en ese
    /// caso es la anulacion fiscal existente (/receipt/void, /invoices/{id}/annul). No se duplica esa logica.</para>
    ///
    /// <para>Deja rastro de auditoria de negocio (PaymentAnnulled) ademas del soft-delete y el contra-asiento.</para>
    /// </summary>
    public async Task AnnulPaymentAsync(string paymentPublicIdOrLegacyId, string? reason, CancellationToken cancellationToken)
    {
        var paymentId = await ResolveRequiredIdAsync<Payment>(paymentPublicIdOrLegacyId, cancellationToken);
        var payment = await _dbContext.Payments.FindAsync(new object[] { paymentId }, cancellationToken);

        if (payment == null)
            throw new KeyNotFoundException("Pago no encontrado.");

        // ADR-032 (review): guard de re-entrancy (anti doble-anulacion). FindAsync ignora el filtro global
        // !IsDeleted, asi que anular un cobro YA anulado vuelve a correr la limpieza de sobrepago y emite un
        // segundo PaymentAnnulled. Cortamos aca con un 409 claro antes de tocar nada.
        EnsureNotAlreadyAnnulled(payment);

        // Un puente de saldo a favor lo anula el sistema, nunca el usuario (mismo criterio que DELETE).
        EnsureNotBridge(payment, "AnnulPaymentAsync");

        // OJO: a proposito NO llamamos a EnsureReservaPaymentsEditableAsync. Anular es justamente la salida
        // valida en estados terminales. Los guards fiscales y de sobrepago SIGUEN aplicando dentro del core.
        var (actorUserId, actorUserName) = ResolveLedgerActor();
        await DeletePaymentCoreAsync(payment, cancellationToken);

        // ADR-032 (review): el rastro incluye el DELTA economico (monto + moneda) del cobro anulado, ademas
        // del motivo. Asi la auditoria sabe cuanta plata se anulo sin tener que abrir el libro de caja. No se
        // agregan otros datos del pago (cliente, referencia, notas): solo el importe y su moneda.
        var auditDetails = BuildPaymentAnnulledAuditDetails(payment, reason);

        // Rastro de negocio (best-effort, no rompe la anulacion si el audit falla).
        if (_auditService is not null)
        {
            await _auditService.LogBusinessEventAsync(
                action: "PaymentAnnulled",
                entityName: "Payment",
                entityId: payment.Id.ToString(),
                details: auditDetails,
                userId: actorUserId ?? "System",
                userName: actorUserName,
                ct: cancellationToken);
        }
        else
        {
            _logger.LogInformation(
                "Payment annulled. PaymentId={PaymentId} ReservaId={ReservaId} Amount={Amount} Currency={Currency} ByUser={UserId} Reason={Reason}",
                payment.Id, payment.ReservaId, payment.Amount, payment.Currency, actorUserId,
                string.IsNullOrWhiteSpace(reason) ? "<none>" : reason!.Trim());
        }
    }

    /// <summary>
    /// ADR-032 (review): arma el texto de auditoria de una anulacion de cobro. Incluye el importe y la
    /// moneda (delta economico) y, si lo hay, el motivo. Formato estable y legible: "Monto: 300 ARS.
    /// Motivo: cobro mal cargado". Sin datos sensibles del cliente.
    /// </summary>
    private static string BuildPaymentAnnulledAuditDetails(Payment payment, string? reason)
    {
        var economicPart = $"Monto: {payment.Amount} {payment.Currency}.";
        var trimmedReason = reason?.Trim();
        return string.IsNullOrWhiteSpace(trimmedReason)
            ? economicPart
            : $"{economicPart} Motivo: {trimmedReason}";
    }

    /// <summary>
    /// ADR-032 (review): mensaje unico para el guard de re-entrancy (anti doble-anulacion).
    /// </summary>
    private const string AlreadyAnnulledMessage = "El cobro ya está anulado.";

    /// <summary>
    /// ADR-032 (review): corta cualquier intento de anular/borrar un cobro que YA esta anulado
    /// (soft-deleted). El caller lo cargo con FindAsync, que ignora el filtro global !IsDeleted, asi que
    /// puede traer una fila ya borrada. Re-procesarla duplicaria el evento de auditoria y volveria a correr
    /// la limpieza de sobrepago. Tira InvalidOperationException -> el controller la mapea a 409.
    /// </summary>
    private static void EnsureNotAlreadyAnnulled(Payment payment)
    {
        if (payment.IsDeleted)
            throw new InvalidOperationException(AlreadyAnnulledMessage);
    }

    /// <summary>
    /// ADR-032: candado compartido por DELETE y ANNUL para los DOS puentes (sobrepago y saldo a favor
    /// aplicado). Ningun puente se borra/anula a mano: el sistema los maneja (OverpaymentCreditCleanup).
    /// </summary>
    private void EnsureNotBridge(Payment payment, string operationName)
    {
        // ADR-022 §4.9 (fix S1-bis): el Payment puente del saldo a favor NO se borra a mano. Borrarlo deja el
        // credito vivo y devuelve el excedente a la reserva -> el excedente existe dos veces.
        if (OverpaymentCreditCleanup.IsOverpaymentBridge(payment))
        {
            _logger.LogWarning(
                "{Operation} rejected (direct overpayment-bridge mutation). PaymentId={PaymentId} ReservaId={ReservaId}.",
                operationName, payment.Id, payment.ReservaId);
            throw new InvalidOperationException(OverpaymentCreditCleanup.DirectBridgeMutationBlockReason);
        }

        // FC4 (2026-06-14): mismo candado para el puente de saldo a favor aplicado.
        if (AppliedCreditBridge.IsAppliedCreditBridge(payment))
        {
            _logger.LogWarning(
                "{Operation} rejected (direct applied-credit-bridge mutation). PaymentId={PaymentId} ReservaId={ReservaId}.",
                operationName, payment.Id, payment.ReservaId);
            throw new InvalidOperationException(AppliedCreditBridge.DirectBridgeMutationBlockReason);
        }
    }

    /// <summary>
    /// ADR-032: nucleo de reversa de un cobro, COMPARTIDO por DELETE libre y ANNUL. Aplica los guards
    /// fiscales (recibo/CAE) y de sobrepago, hace el soft-delete, el contra-asiento de caja y el recalculo.
    /// NO mira el estado de la reserva — esa decision la toma cada caller (DELETE pone el gate, ANNUL no).
    /// </summary>
    private async Task DeletePaymentCoreAsync(Payment payment, CancellationToken cancellationToken)
    {
        var paymentId = payment.Id;

        // C28: bloquear si tiene Receipt (Issued/Voided) o RelatedInvoiceId.
        // Voided tambien bloquea: el recibo ocupa numeracion correlativa y debe
        // preservarse para auditoria — ARCA + Contable 2026-05-06.
        var blockReason = await DeleteGuards.GetPaymentDeleteBlockReasonAsync(_dbContext, paymentId, cancellationToken);
        if (blockReason != null)
        {
            _logger.LogWarning(
                "DeletePaymentCoreAsync rejected (fiscal guard). PaymentId={PaymentId} ReservaId={ReservaId}. Reason={Reason}",
                paymentId, payment.ReservaId, blockReason);
            throw new InvalidOperationException(blockReason);
        }

        // ADR-022 §4.9 (fix S1): si este cobro genero un saldo a favor de sobrepago YA usado, no se anula
        // (compensar a mano corromperia el bolsillo del cliente). Si esta intacto, se revierte el puente y
        // se anula el credito ANTES del recalculo, asi no queda credito fantasma ni el puente infla la deuda.
        var overpaymentBlock = await OverpaymentCreditCleanup.GetConsumedBlockReasonAsync(_dbContext, paymentId, cancellationToken);
        if (overpaymentBlock != null)
        {
            _logger.LogWarning(
                "DeletePaymentCoreAsync rejected (overpayment credit already consumed). PaymentId={PaymentId} ReservaId={ReservaId}.",
                paymentId, payment.ReservaId);
            throw new InvalidOperationException(overpaymentBlock);
        }
        var (overpaymentActorUserId, overpaymentActorUserName) = ResolveLedgerActor();
        await OverpaymentCreditCleanup.ReverseOverpaymentArtifactsAsync(
            _dbContext, paymentId, overpaymentActorUserId, overpaymentActorUserName, cancellationToken);

        // Soft delete
        payment.IsDeleted = true;
        payment.DeletedAt = DateTime.UtcNow;

        // ADR-022 §4.5: anular un cobro NO borra su asiento. En la misma SaveChanges se marca el asiento
        // vigente IsReversed=true y se inserta su reversa (Direction invertida). El neto original+reversa
        // queda en 0 y la historia no vibra. Solo aplica a pagos que movieron caja.
        if (payment.AffectsCash)
            await ReverseLivePaymentLedgerEntryAsync(payment.Id, cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);

        if (payment.ReservaId.HasValue)
            await RecalculateReservaBalanceAsync(payment.ReservaId.Value, cancellationToken);
    }

    private static IQueryable<CollectionWorkItemDto> ApplyCollectionWorkItemOrdering(
        IQueryable<CollectionWorkItemDto> query,
        CollectionWorklistQuery request)
    {
        var sortBy = (request.SortBy ?? "startDate").Trim().ToLowerInvariant();
        var desc = request.IsSortDescending();

        return sortBy switch
        {
            "balance" => desc
                ? query.OrderByDescending(item => item.Balance).ThenBy(item => item.StartDate ?? DateTime.MaxValue)
                : query.OrderBy(item => item.Balance).ThenBy(item => item.StartDate ?? DateTime.MaxValue),
            "numeroreserva" => desc
                ? query.OrderByDescending(item => item.NumeroReserva).ThenBy(item => item.StartDate ?? DateTime.MaxValue)
                : query.OrderBy(item => item.NumeroReserva).ThenBy(item => item.StartDate ?? DateTime.MaxValue),
            _ => desc
                ? query.OrderByDescending(item => item.StartDate ?? DateTime.MaxValue).ThenByDescending(item => item.Balance)
                : query.OrderBy(item => item.StartDate ?? DateTime.MaxValue).ThenByDescending(item => item.Balance)
        };
    }
}
