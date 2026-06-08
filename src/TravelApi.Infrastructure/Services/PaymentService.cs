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
using TravelApi.Infrastructure.Persistence;
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

    // Estados de Reserva considerados "cobrables" (tienen saldo que se le puede pedir al cliente).
    // ADR-020 (2026-06-07): InManagement (En gestion) reemplaza al viejo Sold — la sena se cobra
    // durante la gestion ("la plata viene despues, el si alcanza"). ToSettle (post-viaje) con saldo
    // pendiente sigue siendo cobrable. Quotation/Budget/Lost NO entran (todavia no hay venta cerrada).
    private static readonly string[] ActiveCollectionStatuses =
    {
        EstadoReserva.InManagement,
        EstadoReserva.Confirmed,
        EstadoReserva.Traveling,
        EstadoReserva.ToSettle
    };

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
            .Where(r => ActiveCollectionStatuses.Contains(r.Status) && r.Balance > 0);
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

        var collectedQuery = _dbContext.Payments
            .AsNoTracking()
            .Where(p =>
                !p.IsDeleted &&
                p.Status != "Cancelled" &&
                p.EntryType == PaymentEntryTypes.Payment &&
                p.Amount > 0 &&
                p.PaidAt >= currentMonth);
        if (ownerScope is not null)
        {
            collectedQuery = collectedQuery.Where(p => p.Reserva != null && p.Reserva.ResponsibleUserId == ownerScope);
        }

        var collectedThisMonth = await collectedQuery
            .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;

        return new CollectionsSummaryDto
        {
            PendingAmount = EconomicRulesHelper.RoundCurrency(pendingAmount),
            CollectedThisMonth = EconomicRulesHelper.RoundCurrency(collectedThisMonth),
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
            .Where(r => ActiveCollectionStatuses.Contains(r.Status) && r.Balance > 0);
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
        return await workItemsQuery.ToPagedResponseAsync(query, cancellationToken);
    }

    public async Task<PagedResponse<PaymentDto>> GetAllPaymentsAsync(PaymentsListQuery query, CancellationToken cancellationToken)
    {
        // B1.15 Fase 2a (FIX 5): filter mine. Sin cobranzas.view_all, restringimos a
        // los pagos cuya reserva tiene al user actual como ResponsibleUserId.
        var ownerScope = await GetOwnerScopeOrNullAsync(cancellationToken);

        var paymentsQuery = ApplyPaymentSearch(_dbContext.Payments.AsNoTracking(), query.Search);
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
        // solo eventos (payments/invoices/movements) cuya reserva esta a cargo del user.
        // Los manualMovements con RelatedReserva null se excluyen para roles sin view_all
        // — si Caja necesita verlos, ese permiso es independiente y futuro.
        var ownerScope = await GetOwnerScopeOrNullAsync(cancellationToken);

        var customerPaymentsBase = _dbContext.Payments.AsNoTracking().AsQueryable();
        var invoicesBase = _dbContext.Invoices.AsNoTracking().AsQueryable();
        var manualMovementsBase = _dbContext.ManualCashMovements.AsNoTracking().Where(m => !m.IsVoided);
        if (ownerScope is not null)
        {
            customerPaymentsBase = customerPaymentsBase.Where(p => p.Reserva != null && p.Reserva.ResponsibleUserId == ownerScope);
            invoicesBase = invoicesBase.Where(i => i.Reserva != null && i.Reserva.ResponsibleUserId == ownerScope);
            manualMovementsBase = manualMovementsBase.Where(m => m.RelatedReserva != null && m.RelatedReserva.ResponsibleUserId == ownerScope);
        }

        var customerPayments = customerPaymentsBase
            .Where(payment => string.IsNullOrWhiteSpace(normalizedSearch) ||
                payment.Reference != null && payment.Reference.ToLower().Contains(normalizedSearch) ||
                payment.Method.ToLower().Contains(normalizedSearch) ||
                payment.Notes != null && payment.Notes.ToLower().Contains(normalizedSearch) ||
                payment.Reserva != null && payment.Reserva.NumeroReserva.ToLower().Contains(normalizedSearch))
            .Select(payment => new FinanceHistoryItemDto
            {
                PublicId = payment.PublicId,
                EntityType = "payment",
                OccurredAt = payment.PaidAt,
                Amount = payment.Amount,
                Kind = payment.EntryType == PaymentEntryTypes.CreditNoteReversal ? "Reversion" : "Cobranza",
                Title = payment.EntryType == PaymentEntryTypes.CreditNoteReversal
                    ? "Reversion por nota de credito"
                    : "Cobranza recibida",
                Subtitle = payment.Reserva != null
                    ? "Reserva " + payment.Reserva.NumeroReserva
                    : "Sin reserva",
                ReservaPublicId = payment.Reserva != null ? (Guid?)payment.Reserva.PublicId : null,
                NumeroReserva = payment.Reserva != null ? payment.Reserva.NumeroReserva : null,
                Reference = payment.Reference,
                Method = payment.Method,
                PaymentEntryType = payment.EntryType,
                ReceiptPublicId = payment.Receipt != null ? (Guid?)payment.Receipt.PublicId : null,
                ReceiptNumber = payment.Receipt != null ? payment.Receipt.ReceiptNumber : null,
                ReceiptStatus = payment.Receipt != null ? payment.Receipt.Status : null,
                InvoiceTipoComprobante = null,
                InvoiceResultado = null,
                InvoiceWasForced = false,
                InvoiceForceReason = null,
                MovementSourceType = null,
                MovementDirection = null,
                IsManual = false
            });

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
                Kind = invoice.TipoComprobante == 3 || invoice.TipoComprobante == 8 || invoice.TipoComprobante == 13 || invoice.TipoComprobante == 53
                    ? "Nota de credito"
                    : "Factura AFIP",
                Title = invoice.TipoComprobante == 1 ? "Factura A" :
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
                    "Comp. " + invoice.TipoComprobante,
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
                IsManual = false
            });

        var manualMovements = manualMovementsBase
            .Where(movement => string.IsNullOrWhiteSpace(normalizedSearch) ||
                movement.Description.ToLower().Contains(normalizedSearch) ||
                movement.Reference != null && movement.Reference.ToLower().Contains(normalizedSearch) ||
                movement.Method.ToLower().Contains(normalizedSearch) ||
                movement.RelatedReserva != null && movement.RelatedReserva.NumeroReserva.ToLower().Contains(normalizedSearch) ||
                movement.RelatedSupplier != null && movement.RelatedSupplier.Name.ToLower().Contains(normalizedSearch))
            .Select(movement => new FinanceHistoryItemDto
            {
                PublicId = movement.PublicId,
                EntityType = "movement",
                OccurredAt = movement.OccurredAt,
                Amount = movement.Direction == CashMovementDirections.Expense ? -movement.Amount : movement.Amount,
                Kind = "Caja",
                Title = movement.Description,
                Subtitle = movement.Reference ??
                    (movement.RelatedReserva != null
                        ? movement.RelatedReserva.NumeroReserva
                        : movement.RelatedSupplier != null
                            ? movement.RelatedSupplier.Name
                            : "Ajuste manual"),
                ReservaPublicId = movement.RelatedReserva != null ? (Guid?)movement.RelatedReserva.PublicId : null,
                NumeroReserva = movement.RelatedReserva != null ? movement.RelatedReserva.NumeroReserva : null,
                Reference = movement.Reference,
                Method = movement.Method,
                PaymentEntryType = null,
                ReceiptPublicId = null,
                ReceiptNumber = null,
                ReceiptStatus = null,
                InvoiceTipoComprobante = null,
                InvoiceResultado = null,
                InvoiceWasForced = false,
                InvoiceForceReason = null,
                MovementSourceType = "ManualAdjustment",
                MovementDirection = movement.Direction,
                IsManual = true
            });

        var timeline = customerPayments
            .Concat(invoices)
            .Concat(manualMovements);

        timeline = query.IsSortDescending()
            ? timeline.OrderByDescending(item => item.OccurredAt).ThenByDescending(item => item.PublicId)
            : timeline.OrderBy(item => item.OccurredAt).ThenBy(item => item.PublicId);

        return await timeline.ToPagedResponseAsync(query, cancellationToken);
    }

    public async Task<IEnumerable<PaymentDto>> GetPaymentsForReservaAsync(int ReservaId, CancellationToken cancellationToken)
    {
        return await _dbContext.Payments
            .AsNoTracking()
            .Include(p => p.Receipt)
            .Where(p => p.ReservaId == ReservaId)
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

        if (reserva.Status == EstadoReserva.Budget)
            throw new InvalidOperationException("No se pueden registrar pagos en una Reserva en estado Presupuesto. Pasala a Reservado primero.");

        if (request.Amount <= 0)
            throw new ArgumentException("El monto debe ser mayor a 0.");

        var payment = new Payment
        {
            ReservaId = reservaId,
            Amount = EconomicRulesHelper.RoundCurrency(request.Amount),
            Method = string.IsNullOrWhiteSpace(request.Method) ? "Transfer" : request.Method.Trim(),
            Reference = request.Reference?.Trim(),
            Notes = request.Notes?.Trim(),
            PaidAt = DateTime.UtcNow,
            Status = "Paid",
            EntryType = PaymentEntryTypes.Payment,
            AffectsCash = true
        };

        _dbContext.Payments.Add(payment);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await RecalculateReservaBalanceAsync(reservaId, cancellationToken);

        var created = await _dbContext.Payments
            .Include(p => p.Receipt)
            .FirstAsync(p => p.Id == payment.Id, cancellationToken);

        return _mapper.Map<PaymentDto>(created);
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

        var receipt = new PaymentReceipt
        {
            PaymentId = payment.Id,
            ReservaId = payment.ReservaId ?? throw new InvalidOperationException("El pago no esta vinculado a una reserva."),
            Amount = payment.Amount,
            IssuedAt = DateTime.UtcNow,
            Status = PaymentReceiptStatuses.Issued,
            ReceiptNumber = await GenerateReceiptNumberAsync(cancellationToken)
        };

        _dbContext.PaymentReceipts.Add(receipt);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return _mapper.Map<PaymentReceiptDto>(receipt);
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

        await _dbContext.SaveChangesAsync(cancellationToken);

        if (payment.ReservaId.HasValue)
            await RecalculateReservaBalanceAsync(payment.ReservaId.Value, cancellationToken);

        return payment.PublicId;
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

    private async Task<string> GenerateReceiptNumberAsync(CancellationToken cancellationToken)
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
        var reserva = await _dbContext.Reservas
            .Include(f => f.Payments)
            .Include(f => f.Servicios)
            .Include(f => f.FlightSegments)
            .Include(f => f.HotelBookings)
            .Include(f => f.TransferBookings)
            .Include(f => f.PackageBookings)
            .Include(f => f.AssistanceBookings)
            .FirstOrDefaultAsync(f => f.Id == reservaId, cancellationToken);

        if (reserva == null)
            return;

        var summary = TravelApi.Domain.Reservations.ReservaMoneyCalculator.Calculate(reserva);
        reserva.TotalSale = summary.TotalSale;
        reserva.ConfirmedSale = summary.ConfirmedSale; // ADR-020: venta confirmada (alimenta el saldo)
        reserva.TotalCost = summary.TotalCost;
        reserva.TotalPaid = summary.TotalPaid;
        reserva.Balance = summary.Balance;

        await _dbContext.SaveChangesAsync(cancellationToken);
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

        payment.Amount = EconomicRulesHelper.RoundCurrency(request.Amount);
        payment.Method = request.Method;
        payment.Reference = request.Reference;
        payment.Notes = request.Notes;

        await _dbContext.SaveChangesAsync(cancellationToken);

        if (payment.ReservaId.HasValue)
            await RecalculateReservaBalanceAsync(payment.ReservaId.Value, cancellationToken);
    }

    public async Task DeletePaymentAsync(string paymentPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var paymentId = await ResolveRequiredIdAsync<Payment>(paymentPublicIdOrLegacyId, cancellationToken);
        var payment = await _dbContext.Payments.FindAsync(new object[] { paymentId }, cancellationToken);

        if (payment == null)
            throw new KeyNotFoundException("Pago no encontrado.");

        // C28: bloquear si tiene Receipt (Issued/Voided) o RelatedInvoiceId.
        // Voided tambien bloquea: el recibo ocupa numeracion correlativa y debe
        // preservarse para auditoria — ARCA + Contable 2026-05-06.
        var blockReason = await DeleteGuards.GetPaymentDeleteBlockReasonAsync(_dbContext, paymentId, cancellationToken);
        if (blockReason != null)
        {
            _logger.LogWarning(
                "DeletePaymentAsync rejected. PaymentId={PaymentId} ReservaId={ReservaId}. Reason={Reason}",
                paymentId, payment.ReservaId, blockReason);
            throw new InvalidOperationException(blockReason);
        }

        // Soft delete
        payment.IsDeleted = true;
        payment.DeletedAt = DateTime.UtcNow;

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
