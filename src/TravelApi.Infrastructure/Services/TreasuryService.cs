using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class TreasuryService : ITreasuryService
{
    private readonly AppDbContext _dbContext;

    public TreasuryService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<TreasurySummaryDto> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var startOfMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var activeStatuses = new[] { EstadoReserva.Reserved, EstadoReserva.Operational };

        var accountsReceivable = await _dbContext.Reservas
            .Where(r => activeStatuses.Contains(r.Status) && r.Balance > 0)
            .SumAsync(r => (decimal?)r.Balance, cancellationToken) ?? 0m;

        var settledReservations = await _dbContext.Reservas
            .Where(r => activeStatuses.Contains(r.Status) && r.Balance <= 0)
            .Select(r => new
            {
                r.Id,
                r.TotalSale
            })
            .ToListAsync(cancellationToken);

        decimal afipEligiblePending = 0m;
        if (settledReservations.Count > 0)
        {
            var reservaIds = settledReservations.Select(r => r.Id).ToList();
            var invoicedByReserva = await _dbContext.Invoices
                .Where(i => i.ReservaId.HasValue && reservaIds.Contains(i.ReservaId.Value) && i.Resultado == "A")
                .GroupBy(i => i.ReservaId!.Value)
                .Select(g => new
                {
                    ReservaId = g.Key,
                    Net = g.Sum(i => i.TipoComprobante == 3 || i.TipoComprobante == 8 || i.TipoComprobante == 13 || i.TipoComprobante == 53
                        ? -i.ImporteTotal
                        : i.ImporteTotal)
                })
                .ToListAsync(cancellationToken);

            afipEligiblePending = settledReservations.Sum(reserva =>
            {
                var alreadyInvoiced = invoicedByReserva.FirstOrDefault(x => x.ReservaId == reserva.Id)?.Net ?? 0m;
                var pending = EconomicRulesHelper.RoundCurrency(reserva.TotalSale - alreadyInvoiced);
                return pending > 0 ? pending : 0m;
            });
        }

        var cashInPayments = await _dbContext.Payments
            .Where(p => !p.IsDeleted && p.AffectsCash && p.Status != "Cancelled" && p.PaidAt >= startOfMonth)
            .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;

        var cashInManual = await _dbContext.ManualCashMovements
            .Where(m => !m.IsVoided && m.Direction == CashMovementDirections.Income && m.OccurredAt >= startOfMonth)
            .SumAsync(m => (decimal?)m.Amount, cancellationToken) ?? 0m;

        var cashOutSuppliers = await _dbContext.SupplierPayments
            .Where(p => p.PaidAt >= startOfMonth)
            .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;

        var cashOutManual = await _dbContext.ManualCashMovements
            .Where(m => !m.IsVoided && m.Direction == CashMovementDirections.Expense && m.OccurredAt >= startOfMonth)
            .SumAsync(m => (decimal?)m.Amount, cancellationToken) ?? 0m;

        var cashInThisMonth = EconomicRulesHelper.RoundCurrency(cashInPayments + cashInManual);
        var cashOutThisMonth = EconomicRulesHelper.RoundCurrency(cashOutSuppliers + cashOutManual);

        return new TreasurySummaryDto
        {
            AccountsReceivable = EconomicRulesHelper.RoundCurrency(accountsReceivable),
            AfipEligiblePending = EconomicRulesHelper.RoundCurrency(afipEligiblePending),
            CashInThisMonth = cashInThisMonth,
            CashOutThisMonth = cashOutThisMonth,
            NetCashThisMonth = EconomicRulesHelper.RoundCurrency(cashInThisMonth - cashOutThisMonth)
        };
    }

    public async Task<IReadOnlyList<CashMovementDto>> GetMovementsAsync(CancellationToken cancellationToken)
    {
        var paymentMovements = await _dbContext.Payments
            .AsNoTracking()
            .Include(p => p.Reserva)
            .Where(p => !p.IsDeleted && p.AffectsCash && p.Status != "Cancelled")
            .Select(p => new CashMovementDto
            {
                SourceType = "CustomerPayment",
                SourceId = p.Id.ToString(),
                Direction = CashMovementDirections.Income,
                Amount = p.Amount,
                OccurredAt = p.PaidAt,
                Method = p.Method,
                Description = p.Notes ?? "Cobranza de cliente",
                Reference = p.Reference,
                ReservaId = p.ReservaId,
                NumeroReserva = p.Reserva != null ? p.Reserva.NumeroReserva : null,
                IsManual = false
            })
            .ToListAsync(cancellationToken);

        var supplierMovements = await _dbContext.SupplierPayments
            .AsNoTracking()
            .Include(p => p.Reserva)
            .Include(p => p.Supplier)
            .Select(p => new CashMovementDto
            {
                SourceType = "SupplierPayment",
                SourceId = p.Id.ToString(),
                Direction = CashMovementDirections.Expense,
                Amount = p.Amount,
                OccurredAt = p.PaidAt,
                Method = p.Method,
                Description = p.Notes ?? "Pago a proveedor",
                Reference = p.Reference,
                ReservaId = p.ReservaId,
                NumeroReserva = p.Reserva != null ? p.Reserva.NumeroReserva : null,
                SupplierId = p.SupplierId,
                SupplierName = p.Supplier.Name,
                IsManual = false
            })
            .ToListAsync(cancellationToken);

        var manualMovements = await _dbContext.ManualCashMovements
            .AsNoTracking()
            .Include(m => m.RelatedReserva)
            .Include(m => m.RelatedSupplier)
            .Where(m => !m.IsVoided)
            .Select(m => new CashMovementDto
            {
                SourceType = "ManualAdjustment",
                SourceId = m.Id.ToString(),
                Direction = m.Direction,
                Amount = m.Amount,
                OccurredAt = m.OccurredAt,
                Method = m.Method,
                Description = m.Description,
                Reference = m.Reference,
                ReservaId = m.RelatedReservaId,
                NumeroReserva = m.RelatedReserva != null ? m.RelatedReserva.NumeroReserva : null,
                SupplierId = m.RelatedSupplierId,
                SupplierName = m.RelatedSupplier != null ? m.RelatedSupplier.Name : null,
                IsManual = true
            })
            .ToListAsync(cancellationToken);

        return paymentMovements
            .Concat(supplierMovements)
            .Concat(manualMovements)
            .OrderByDescending(m => m.OccurredAt)
            .ToList();
    }

    public async Task<ManualCashMovementDto> CreateManualMovementAsync(UpsertManualCashMovementRequest request, string createdBy, CancellationToken cancellationToken)
    {
        ValidateManualMovement(request);

        var entity = new ManualCashMovement
        {
            Direction = request.Direction,
            Amount = EconomicRulesHelper.RoundCurrency(request.Amount),
            OccurredAt = request.OccurredAt == default ? DateTime.UtcNow : request.OccurredAt.ToUniversalTime(),
            Method = request.Method.Trim(),
            Category = request.Category.Trim(),
            Description = request.Description.Trim(),
            Reference = request.Reference?.Trim(),
            CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? "System" : createdBy,
            RelatedReservaId = request.RelatedReservaId,
            RelatedSupplierId = request.RelatedSupplierId
        };

        _dbContext.ManualCashMovements.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return MapManual(entity);
    }

    public async Task<ManualCashMovementDto> UpdateManualMovementAsync(int id, UpsertManualCashMovementRequest request, CancellationToken cancellationToken)
    {
        ValidateManualMovement(request);

        var entity = await _dbContext.ManualCashMovements.FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Movimiento manual no encontrado.");

        if (entity.IsVoided)
            throw new InvalidOperationException("No se puede editar un movimiento anulado.");

        entity.Direction = request.Direction;
        entity.Amount = EconomicRulesHelper.RoundCurrency(request.Amount);
        entity.OccurredAt = request.OccurredAt == default ? entity.OccurredAt : request.OccurredAt.ToUniversalTime();
        entity.Method = request.Method.Trim();
        entity.Category = request.Category.Trim();
        entity.Description = request.Description.Trim();
        entity.Reference = request.Reference?.Trim();
        entity.RelatedReservaId = request.RelatedReservaId;
        entity.RelatedSupplierId = request.RelatedSupplierId;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return MapManual(entity);
    }

    public async Task DeleteManualMovementAsync(int id, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.ManualCashMovements.FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Movimiento manual no encontrado.");

        entity.IsVoided = true;
        entity.VoidedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static ManualCashMovementDto MapManual(ManualCashMovement entity)
    {
        return new ManualCashMovementDto
        {
            Id = entity.Id,
            Direction = entity.Direction,
            Amount = entity.Amount,
            OccurredAt = entity.OccurredAt,
            Method = entity.Method,
            Category = entity.Category,
            Description = entity.Description,
            Reference = entity.Reference,
            CreatedBy = entity.CreatedBy,
            IsVoided = entity.IsVoided,
            RelatedReservaId = entity.RelatedReservaId,
            RelatedSupplierId = entity.RelatedSupplierId
        };
    }

    private static void ValidateManualMovement(UpsertManualCashMovementRequest request)
    {
        if (request.Direction != CashMovementDirections.Income && request.Direction != CashMovementDirections.Expense)
            throw new ArgumentException("La direccion del movimiento es invalida.");
        if (request.Amount <= 0)
            throw new ArgumentException("El monto debe ser mayor a 0.");
        if (string.IsNullOrWhiteSpace(request.Method))
            throw new ArgumentException("El metodo es obligatorio.");
        if (string.IsNullOrWhiteSpace(request.Category))
            throw new ArgumentException("La categoria es obligatoria.");
        if (string.IsNullOrWhiteSpace(request.Description))
            throw new ArgumentException("La descripcion es obligatoria.");
    }
}
