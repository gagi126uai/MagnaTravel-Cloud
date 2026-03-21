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
    private readonly IMapper _mapper;

    public PaymentService(AppDbContext dbContext, IMapper mapper)
    {
        _dbContext = dbContext;
        _mapper = mapper;
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public async Task<IEnumerable<PaymentDto>> GetAllPaymentsAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.Payments
            .AsNoTracking()
            .Include(p => p.Reserva)
            .Include(p => p.Receipt)
            .OrderByDescending(p => p.PaidAt)
            .ProjectTo<PaymentDto>(_mapper.ConfigurationProvider)
            .ToListAsync(cancellationToken);
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

    public async Task<PaymentDto> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        var reserva = await _dbContext.Reservas
            .FirstOrDefaultAsync(r => r.Id == request.ReservaId, cancellationToken);

        if (reserva == null)
            throw new ArgumentException("Reserva no encontrada.");

        if (request.Amount <= 0)
            throw new ArgumentException("El monto debe ser mayor a 0.");

        var payment = new Payment
        {
            ReservaId = request.ReservaId,
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
        await RecalculateReservaBalanceAsync(request.ReservaId, cancellationToken);

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
                p.Id,
                p.Amount,
                p.Method,
                p.Reference,
                p.Status,
                p.PaidAt,
                p.DeletedAt,
                p.ReservaId,
                NumeroReserva = p.Reserva != null
                    ? p.Reserva.NumeroReserva : null,
                FileName = p.Reserva != null
                    ? p.Reserva.Name : null,
                CustomerName = p.Reserva != null && p.Reserva.Payer != null
                    ? p.Reserva.Payer.FullName : null
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<int> RestorePaymentAsync(int id, CancellationToken cancellationToken)
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

        return payment.Id;
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
}
