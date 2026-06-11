using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class TreasuryService : ITreasuryService
{
    private readonly AppDbContext _dbContext;
    private readonly IEntityReferenceResolver _entityReferenceResolver;

    public TreasuryService(AppDbContext dbContext, IEntityReferenceResolver entityReferenceResolver)
    {
        _dbContext = dbContext;
        _entityReferenceResolver = entityReferenceResolver;
    }

    public async Task<TreasurySummaryDto> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var startOfMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        // Estados de Reserva que cuentan para tesoreria / cuentas por cobrar (AR).
        // ADR-020 (2026-06-07): InManagement (En gestion) reemplaza al viejo Sold; ToSettle es el
        // file post-viaje pendiente de liquidar, su saldo sigue siendo cuenta por cobrar.
        // Quotation/Budget/Lost no entran (no hay AR exigible todavia).
        var activeStatuses = new[]
        {
            EstadoReserva.InManagement,
            EstadoReserva.Confirmed,
            EstadoReserva.Traveling,
            EstadoReserva.ToSettle
        };

        var accountsReceivable = await _dbContext.Reservas
            .Where(r => activeStatuses.Contains(r.Status) && r.Balance > 0)
            .SumAsync(r => (decimal?)r.Balance, cancellationToken) ?? 0m;

        // ADR-021 Capa 6: cuentas por cobrar POR MONEDA del saldo. Se agrega en SQL contra la tabla hija
        // ReservaMoneyByCurrency (no contra el escalar surrogate, que mezclaria monedas). Join explicito
        // contra Reservas (no nav implicita) para correr igual en Postgres e InMemory; el filtro de estado
        // se aplica sobre la reserva del join y solo se cuentan saldos positivos (lo que deben).
        var arByCurrencyQuery =
            from row in _dbContext.ReservaMoneyByCurrency
            join reservaPadre in _dbContext.Reservas on row.ReservaId equals reservaPadre.Id
            where activeStatuses.Contains(reservaPadre.Status) && row.Balance > 0
            select new { row.Currency, row.Balance };

        var accountsReceivableByCurrency = (await arByCurrencyQuery
            .GroupBy(x => x.Currency)
            .Select(g => new { Currency = g.Key, Amount = g.Sum(x => x.Balance) })
            .ToListAsync(cancellationToken))
            .Select(x => new CurrencyAmountDto
            {
                Currency = x.Currency,
                Amount = EconomicRulesHelper.RoundCurrency(x.Amount)
            })
            .ToList();

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

        var cashSummary = await GetCashSummaryAsync(cancellationToken);
        var (cashInByCurrency, cashOutByCurrency) = await GetCashByCurrencyAsync(cancellationToken);

        return new TreasurySummaryDto
        {
            AccountsReceivable = EconomicRulesHelper.RoundCurrency(accountsReceivable),
            AfipEligiblePending = EconomicRulesHelper.RoundCurrency(afipEligiblePending),
            CashInThisMonth = cashSummary.CashInThisMonth,
            CashOutThisMonth = cashSummary.CashOutThisMonth,
            NetCashThisMonth = cashSummary.NetCashThisMonth,
            AccountsReceivableByCurrency = accountsReceivableByCurrency,
            CashInByCurrency = cashInByCurrency,
            CashOutByCurrency = cashOutByCurrency
        };
    }

    /// <summary>
    /// ADR-021 Capa 6: entradas y salidas de caja del mes SEPARADAS por la moneda REAL del movimiento.
    /// Es el arqueo de lo que efectivamente entro/salio: un cobro cruzado entra a caja en su moneda real
    /// (Payment.Currency), NO en la moneda imputada al saldo. Los movimientos manuales hoy son ARS (su
    /// entidad no tiene columna de moneda; ver ADR-021 §3, no se agrega schema en este lote).
    /// </summary>
    private async Task<(List<CurrencyAmountDto> CashIn, List<CurrencyAmountDto> CashOut)> GetCashByCurrencyAsync(
        CancellationToken cancellationToken)
    {
        var startOfMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        // Entradas: cobros de cliente por moneda real + movimientos manuales de ingreso (ARS).
        var cashInPayments = await _dbContext.Payments
            .Where(p => !p.IsDeleted && p.AffectsCash && p.Status != "Cancelled" && p.PaidAt >= startOfMonth)
            .GroupBy(p => p.Currency)
            .Select(g => new { Currency = g.Key, Amount = g.Sum(p => p.Amount) })
            .ToListAsync(cancellationToken);

        var cashInManual = await _dbContext.ManualCashMovements
            .Where(m => !m.IsVoided && m.Direction == CashMovementDirections.Income && m.OccurredAt >= startOfMonth)
            .SumAsync(m => (decimal?)m.Amount, cancellationToken) ?? 0m;

        // Salidas: pagos a proveedor por moneda real + movimientos manuales de egreso (ARS).
        var cashOutSuppliers = await _dbContext.SupplierPayments
            .Where(p => p.PaidAt >= startOfMonth)
            .GroupBy(p => p.Currency)
            .Select(g => new { Currency = g.Key, Amount = g.Sum(p => p.Amount) })
            .ToListAsync(cancellationToken);

        var cashOutManual = await _dbContext.ManualCashMovements
            .Where(m => !m.IsVoided && m.Direction == CashMovementDirections.Expense && m.OccurredAt >= startOfMonth)
            .SumAsync(m => (decimal?)m.Amount, cancellationToken) ?? 0m;

        var cashIn = MergeIntoCurrencyTotals(cashInPayments.Select(x => (x.Currency, x.Amount)), arsManual: cashInManual);
        var cashOut = MergeIntoCurrencyTotals(cashOutSuppliers.Select(x => (x.Currency, x.Amount)), arsManual: cashOutManual);
        return (cashIn, cashOut);
    }

    /// <summary>
    /// Junta totales por moneda con el monto manual (que es ARS) y redondea. Normaliza la moneda
    /// (null/vacio -> ARS) para que los pagos legacy sin moneda caigan en la linea ARS.
    /// </summary>
    private static List<CurrencyAmountDto> MergeIntoCurrencyTotals(
        IEnumerable<(string? Currency, decimal Amount)> rows, decimal arsManual)
    {
        var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var (currency, amount) in rows)
        {
            var key = Monedas.Normalizar(currency);
            totals[key] = totals.TryGetValue(key, out var current) ? current + amount : amount;
        }

        if (arsManual != 0m)
        {
            totals[Monedas.ARS] = totals.TryGetValue(Monedas.ARS, out var existing) ? existing + arsManual : arsManual;
        }

        return totals
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => new CurrencyAmountDto
            {
                Currency = kvp.Key,
                Amount = EconomicRulesHelper.RoundCurrency(kvp.Value)
            })
            .ToList();
    }

    public async Task<CashSummaryDto> GetCashSummaryAsync(CancellationToken cancellationToken)
    {
        var startOfMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

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

        // ADR-021 Capa 7: el desglose por moneda reusa EXACTAMENTE la misma agregacion por moneda real
        // que /treasury/summary (GetCashByCurrencyAsync), para no duplicar la formula. Se cruzan las dos
        // listas (in / out) en filas por moneda con su neto.
        var (cashInByCurrency, cashOutByCurrency) = await GetCashByCurrencyAsync(cancellationToken);
        var cashByCurrency = BuildCashByCurrencyRows(cashInByCurrency, cashOutByCurrency);

        return new CashSummaryDto
        {
            CashInThisMonth = cashInThisMonth,
            CashOutThisMonth = cashOutThisMonth,
            NetCashThisMonth = EconomicRulesHelper.RoundCurrency(cashInThisMonth - cashOutThisMonth),
            CashByCurrency = cashByCurrency
        };
    }

    /// <summary>
    /// ADR-021 Capa 7: cruza las dos listas por moneda (entradas / salidas) en una sola fila por moneda
    /// con su neto. Toma la union de monedas presentes en cualquiera de las dos (una moneda con solo
    /// salidas, o solo entradas, igual aparece). Ordena por moneda para que el shape sea estable.
    /// </summary>
    private static List<CashByCurrencyDto> BuildCashByCurrencyRows(
        List<CurrencyAmountDto> cashIn, List<CurrencyAmountDto> cashOut)
    {
        var inByCurrency = cashIn.ToDictionary(x => x.Currency, x => x.Amount, StringComparer.Ordinal);
        var outByCurrency = cashOut.ToDictionary(x => x.Currency, x => x.Amount, StringComparer.Ordinal);

        var allCurrencies = inByCurrency.Keys
            .Union(outByCurrency.Keys, StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal);

        var rows = new List<CashByCurrencyDto>();
        foreach (var currency in allCurrencies)
        {
            var cashInAmount = inByCurrency.TryGetValue(currency, out var inAmount) ? inAmount : 0m;
            var cashOutAmount = outByCurrency.TryGetValue(currency, out var outAmount) ? outAmount : 0m;
            rows.Add(new CashByCurrencyDto
            {
                Currency = currency,
                CashInThisMonth = cashInAmount,
                CashOutThisMonth = cashOutAmount,
                NetCashThisMonth = EconomicRulesHelper.RoundCurrency(cashInAmount - cashOutAmount)
            });
        }

        return rows;
    }

    public async Task<PagedResponse<CashMovementDto>> GetMovementsAsync(TreasuryMovementsQuery query, CancellationToken cancellationToken)
    {
        var normalizedSearch = query.Search?.Trim().ToLowerInvariant();

        var paymentMovements = _dbContext.Payments
            .AsNoTracking()
            .Where(p => !p.IsDeleted && p.AffectsCash && p.Status != "Cancelled")
            .Where(p => string.IsNullOrWhiteSpace(normalizedSearch) ||
                p.Method.ToLower().Contains(normalizedSearch) ||
                p.Reference != null && p.Reference.ToLower().Contains(normalizedSearch) ||
                p.Notes != null && p.Notes.ToLower().Contains(normalizedSearch) ||
                p.Reserva != null && p.Reserva.NumeroReserva.ToLower().Contains(normalizedSearch))
            .Select(p => new CashMovementDto
            {
                SourceType = "CustomerPayment",
                SourcePublicId = p.PublicId,
                Direction = CashMovementDirections.Income,
                Amount = p.Amount,
                Currency = p.Currency,   // ADR-021: moneda REAL del cobro (caja)
                OccurredAt = p.PaidAt,
                Method = p.Method,
                Category = null,
                Description = p.Notes ?? "Cobranza de cliente",
                Reference = p.Reference,
                ReservaPublicId = p.Reserva != null ? (Guid?)p.Reserva.PublicId : null,
                NumeroReserva = p.Reserva != null ? p.Reserva.NumeroReserva : null,
                SupplierPublicId = null,
                SupplierName = null,
                IsManual = false
            });

        var supplierMovements = _dbContext.SupplierPayments
            .AsNoTracking()
            .Where(p => string.IsNullOrWhiteSpace(normalizedSearch) ||
                p.Method.ToLower().Contains(normalizedSearch) ||
                p.Reference != null && p.Reference.ToLower().Contains(normalizedSearch) ||
                p.Notes != null && p.Notes.ToLower().Contains(normalizedSearch) ||
                p.Supplier.Name.ToLower().Contains(normalizedSearch) ||
                p.Reserva != null && p.Reserva.NumeroReserva.ToLower().Contains(normalizedSearch))
            .Select(p => new CashMovementDto
            {
                SourceType = "SupplierPayment",
                SourcePublicId = p.PublicId,
                Direction = CashMovementDirections.Expense,
                Amount = p.Amount,
                Currency = p.Currency,   // ADR-021: moneda REAL del egreso (caja)
                OccurredAt = p.PaidAt,
                Method = p.Method,
                Category = null,
                Description = p.Notes ?? "Pago a proveedor",
                Reference = p.Reference,
                ReservaPublicId = p.Reserva != null ? (Guid?)p.Reserva.PublicId : null,
                NumeroReserva = p.Reserva != null ? p.Reserva.NumeroReserva : null,
                SupplierPublicId = p.Supplier.PublicId,
                SupplierName = p.Supplier.Name,
                IsManual = false
            });

        var manualMovements = _dbContext.ManualCashMovements
            .AsNoTracking()
            .Where(m => !m.IsVoided)
            .Where(m => string.IsNullOrWhiteSpace(normalizedSearch) ||
                m.Description.ToLower().Contains(normalizedSearch) ||
                m.Method.ToLower().Contains(normalizedSearch) ||
                m.Reference != null && m.Reference.ToLower().Contains(normalizedSearch) ||
                m.Category.ToLower().Contains(normalizedSearch) ||
                m.RelatedReserva != null && m.RelatedReserva.NumeroReserva.ToLower().Contains(normalizedSearch) ||
                m.RelatedSupplier != null && m.RelatedSupplier.Name.ToLower().Contains(normalizedSearch))
            .Select(m => new CashMovementDto
            {
                SourceType = "ManualAdjustment",
                SourcePublicId = m.PublicId,
                Direction = m.Direction,
                Amount = m.Amount,
                Currency = Monedas.ARS,  // ADR-021: el movimiento manual no tiene moneda propia hoy (siempre ARS)
                OccurredAt = m.OccurredAt,
                Method = m.Method,
                Category = m.Category,
                Description = m.Description,
                Reference = m.Reference,
                ReservaPublicId = m.RelatedReserva != null ? (Guid?)m.RelatedReserva.PublicId : null,
                NumeroReserva = m.RelatedReserva != null ? m.RelatedReserva.NumeroReserva : null,
                SupplierPublicId = m.RelatedSupplier != null ? (Guid?)m.RelatedSupplier.PublicId : null,
                SupplierName = m.RelatedSupplier != null ? m.RelatedSupplier.Name : null,
                IsManual = true
            });

        var movements = paymentMovements
            .Concat(supplierMovements)
            .Concat(manualMovements);

        if (!string.Equals(query.Direction, "all", StringComparison.OrdinalIgnoreCase))
        {
            var direction = string.Equals(query.Direction, "income", StringComparison.OrdinalIgnoreCase)
                ? CashMovementDirections.Income
                : string.Equals(query.Direction, "expense", StringComparison.OrdinalIgnoreCase)
                    ? CashMovementDirections.Expense
                    : query.Direction;
            movements = movements.Where(movement => movement.Direction == direction);
        }

        if (!string.Equals(query.SourceType, "all", StringComparison.OrdinalIgnoreCase))
        {
            movements = movements.Where(movement => movement.SourceType == query.SourceType);
        }

        movements = !string.Equals(query.SortDir, "asc", StringComparison.OrdinalIgnoreCase)
            ? movements.OrderByDescending(movement => movement.OccurredAt).ThenByDescending(movement => movement.SourcePublicId)
            : movements.OrderBy(movement => movement.OccurredAt).ThenBy(movement => movement.SourcePublicId);

        return await movements.ToPagedResponseAsync(query, cancellationToken);
    }

    public async Task<ManualCashMovementDto> CreateManualMovementAsync(UpsertManualCashMovementRequest request, string createdBy, CancellationToken cancellationToken)
    {
        ValidateManualMovement(request);
        var relatedReservaId = await ResolveReservaIdAsync(request.RelatedReservaPublicId, cancellationToken);
        var relatedSupplierId = await ResolveSupplierIdAsync(request.RelatedSupplierPublicId, cancellationToken);

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
            RelatedReservaId = relatedReservaId,
            RelatedSupplierId = relatedSupplierId
        };

        _dbContext.ManualCashMovements.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return await GetManualMovementDtoAsync(entity.Id, cancellationToken);
    }

    public async Task<ManualCashMovementDto> UpdateManualMovementAsync(int id, UpsertManualCashMovementRequest request, CancellationToken cancellationToken)
    {
        ValidateManualMovement(request);
        var relatedReservaId = await ResolveReservaIdAsync(request.RelatedReservaPublicId, cancellationToken);
        var relatedSupplierId = await ResolveSupplierIdAsync(request.RelatedSupplierPublicId, cancellationToken);

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
        entity.RelatedReservaId = relatedReservaId;
        entity.RelatedSupplierId = relatedSupplierId;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return await GetManualMovementDtoAsync(entity.Id, cancellationToken);
    }

    public async Task DeleteManualMovementAsync(int id, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.ManualCashMovements.FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Movimiento manual no encontrado.");

        entity.IsVoided = true;
        entity.VoidedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<ManualCashMovementDto> GetManualMovementDtoAsync(int id, CancellationToken cancellationToken)
    {
        return await _dbContext.ManualCashMovements
            .AsNoTracking()
            .Where(entity => entity.Id == id)
            .Select(entity => new ManualCashMovementDto
            {
                PublicId = entity.PublicId,
                Direction = entity.Direction,
                Amount = entity.Amount,
                OccurredAt = entity.OccurredAt,
                Method = entity.Method,
                Category = entity.Category,
                Description = entity.Description,
                Reference = entity.Reference,
                CreatedBy = entity.CreatedBy,
                IsVoided = entity.IsVoided,
                RelatedReservaPublicId = entity.RelatedReserva != null ? (Guid?)entity.RelatedReserva.PublicId : null,
                RelatedSupplierPublicId = entity.RelatedSupplier != null ? (Guid?)entity.RelatedSupplier.PublicId : null
            })
            .FirstAsync(cancellationToken);
    }

    private async Task<int?> ResolveReservaIdAsync(string? reservaPublicId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reservaPublicId))
            return null;

        return await _dbContext.Reservas
            .AsNoTracking()
            .ResolveInternalIdAsync(reservaPublicId, cancellationToken);
    }

    private async Task<int?> ResolveSupplierIdAsync(string? supplierPublicId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(supplierPublicId))
            return null;

        return await _dbContext.Suppliers
            .AsNoTracking()
            .ResolveInternalIdAsync(supplierPublicId, cancellationToken);
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
