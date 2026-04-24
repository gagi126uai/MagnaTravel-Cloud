using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class PaymentService : IPaymentService
{
    private readonly AppDbContext _dbContext;
    private readonly IEntityReferenceResolver _entityReferenceResolver;
    private readonly IMapper _mapper;
    private readonly IOperationalFinanceSettingsService _operationalFinanceSettingsService;

    private static readonly string[] ActiveCollectionStatuses =
    {
        EstadoReserva.Reserved,
        EstadoReserva.Operational
    };

    public PaymentService(
        AppDbContext dbContext,
        IEntityReferenceResolver entityReferenceResolver,
        IMapper mapper,
        IOperationalFinanceSettingsService operationalFinanceSettingsService)
    {
        _dbContext = dbContext;
        _entityReferenceResolver = entityReferenceResolver;
        _mapper = mapper;
        _operationalFinanceSettingsService = operationalFinanceSettingsService;
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public async Task<CollectionsSummaryDto> GetCollectionsSummaryAsync(CancellationToken cancellationToken)
    {
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(cancellationToken);
        var today = DateTime.UtcNow.Date;
        var threshold = today.AddDays(Math.Max(settings.UpcomingUnpaidReservationAlertDays, 1));
        var currentMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var pendingReservations = await _dbContext.Reservas
            .AsNoTracking()
            .Where(r => ActiveCollectionStatuses.Contains(r.Status) && r.Balance > 0)
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

        var collectedThisMonth = await _dbContext.Payments
            .AsNoTracking()
            .Where(p =>
                !p.IsDeleted &&
                p.Status != "Cancelled" &&
                p.EntryType == PaymentEntryTypes.Payment &&
                p.Amount > 0 &&
                p.PaidAt >= currentMonth)
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

        var reservationsQuery = _dbContext.Reservas
            .AsNoTracking()
            .Where(r => ActiveCollectionStatuses.Contains(r.Status) && r.Balance > 0);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var normalized = query.Search.Trim().ToLowerInvariant();
            reservationsQuery = reservationsQuery.Where(reserva =>
                reserva.NumeroReserva.ToLower().Contains(normalized) ||
                (reserva.Payer != null && reserva.Payer.FullName.ToLower().Contains(normalized)) ||
                (reserva.ResponsibleUser != null && reserva.ResponsibleUser.FullName.ToLower().Contains(normalized)));
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
            ResponsibleUserName = reserva.ResponsibleUser != null ? reserva.ResponsibleUser.FullName : null,
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
        var paymentsQuery = ApplyPaymentSearch(_dbContext.Payments.AsNoTracking(), query.Search);
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
        var customerPayments = _dbContext.Payments
            .AsNoTracking()
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

        var invoices = _dbContext.Invoices
            .AsNoTracking()
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

        var manualMovements = _dbContext.ManualCashMovements
            .AsNoTracking()
            .Where(movement => !movement.IsVoided)
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
        var payment = await _dbContext.Payments
            .Include(p => p.Reserva)
            .Include(p => p.Receipt)
            .FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);

        if (payment == null)
            throw new KeyNotFoundException("Pago no encontrado.");

        if (payment.EntryType != PaymentEntryTypes.Payment || payment.Amount <= 0)
            throw new InvalidOperationException("Solo los pagos positivos pueden emitir comprobante.");

        if (payment.Receipt != null)
            return _mapper.Map<PaymentReceiptDto>(payment.Receipt);

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

    private async Task RecalculateReservaBalanceAsync(int reservaId, CancellationToken cancellationToken)
    {
        var reserva = await _dbContext.Reservas
            .Include(f => f.Payments)
            .Include(f => f.Servicios)
            .Include(f => f.FlightSegments)
            .Include(f => f.HotelBookings)
            .Include(f => f.TransferBookings)
            .Include(f => f.PackageBookings)
            .FirstOrDefaultAsync(f => f.Id == reservaId, cancellationToken);

        if (reserva == null)
            return;

        reserva.TotalSale =
            (reserva.FlightSegments?.Sum(f => f.SalePrice) ?? 0) +
            (reserva.HotelBookings?.Sum(h => h.SalePrice) ?? 0) +
            (reserva.TransferBookings?.Sum(t => t.SalePrice) ?? 0) +
            (reserva.PackageBookings?.Sum(p => p.SalePrice) ?? 0) +
            (reserva.Servicios?.Sum(r => r.SalePrice) ?? 0);

        reserva.TotalCost =
            (reserva.FlightSegments?.Sum(f => f.NetCost) ?? 0) +
            (reserva.HotelBookings?.Sum(h => h.NetCost) ?? 0) +
            (reserva.TransferBookings?.Sum(t => t.NetCost) ?? 0) +
            (reserva.PackageBookings?.Sum(p => p.NetCost) ?? 0) +
            (reserva.Servicios?.Sum(r => r.NetCost) ?? 0);

        reserva.TotalPaid = reserva.Payments
            .Where(p => p.Status != "Cancelled" && !p.IsDeleted)
            .Sum(p => p.Amount);

        reserva.Balance = reserva.TotalSale - reserva.TotalPaid;
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
