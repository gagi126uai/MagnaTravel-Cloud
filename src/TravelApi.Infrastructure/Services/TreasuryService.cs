using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services.Reservations;

namespace TravelApi.Infrastructure.Services;

public class TreasuryService : ITreasuryService
{
    private readonly AppDbContext _dbContext;
    private readonly IEntityReferenceResolver _entityReferenceResolver;
    // ADR-022 §4.7 (T4): fuente unica AR/AP, compartida con el dashboard. Opcional para no romper
    // instancias existentes en tests; si es null se construye sobre el mismo DbContext.
    private readonly IFinancePositionService? _financePositionService;
    // ADR-022 §4.6 (B3): enmascarado de costo. El Libro de Caja EXPONE egresos a proveedor (costo); un
    // usuario sin cobranzas.see_cost no debe ver esos montos. Opcionales (default null) -> fail-closed
    // (oculta), igual que SupplierService/CostMasking.
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly IUserPermissionResolver? _permissionResolver;

    public TreasuryService(
        AppDbContext dbContext,
        IEntityReferenceResolver entityReferenceResolver,
        IFinancePositionService? financePositionService = null,
        IHttpContextAccessor? httpContextAccessor = null,
        IUserPermissionResolver? permissionResolver = null)
    {
        _dbContext = dbContext;
        _entityReferenceResolver = entityReferenceResolver;
        _financePositionService = financePositionService;
        _httpContextAccessor = httpContextAccessor;
        _permissionResolver = permissionResolver;
    }

    /// <summary>Fuente unica AR/AP; se construye sobre el DbContext del scope si no se inyecto (tests).</summary>
    private IFinancePositionService FinancePosition
        => _financePositionService ?? new FinancePositionService(_dbContext);

    /// <summary>
    /// ADR-022 §4.6 (B3): true si el caller puede ver montos de COSTO (egresos a proveedor, cuentas por
    /// pagar). Mismo criterio que SupplierService/CostMasking: Admin siempre; sin permiso/resolver/HttpContext
    /// es fail-closed (NO ve). Centraliza la decision para enmascarar el libro y el AP en una sola consulta.
    /// </summary>
    private Task<bool> CanSeeCostAsync(CancellationToken cancellationToken)
        => CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, cancellationToken);

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

        // ADR-022 §4.7 (T4): cuentas por cobrar POR MONEDA desde la FUENTE UNICA compartida con el dashboard.
        // Antes esta query vivia inline aca y otra equivalente en ReportService -> podian no cerrar. Ahora
        // ambos leen lo mismo. AR es plata de VENTA -> NO se enmascara.
        var accountsReceivableByCurrency = (await FinancePosition.GetAccountsReceivableByCurrencyAsync(cancellationToken))
            .Select(x => new CurrencyAmountDto { Currency = x.Currency, Amount = x.Amount })
            .ToList();

        // ADR-022 §4.7 (T4): cuentas por PAGAR por moneda, MISMA fuente que el dashboard. Tesoreria antes NO
        // exponia AP; ahora si (campo aditivo). Es dato de COSTO -> se enmascara (lista vacia) sin see_cost.
        var canSeeCost = await CanSeeCostAsync(cancellationToken);
        var accountsPayableByCurrency = canSeeCost
            ? (await FinancePosition.GetAccountsPayableByCurrencyAsync(cancellationToken))
                .Select(x => new CurrencyAmountDto { Currency = x.Currency, Amount = x.Amount })
                .ToList()
            : new List<CurrencyAmountDto>();

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

        // ADR-022 §4.6 (fix S2): la SALIDA de caja (pagos a proveedor + devoluciones de operador) es
        // informacion de costo. Sin cobranzas.see_cost se enmascara igual que AccountsPayableByCurrency:
        // el escalar de compat a 0 y el desglose por moneda a lista vacia (fail-closed). La ENTRADA de caja
        // (cobros = venta) queda visible. NetCash se omite del enmascarado: con CashOut tapado dejaria de ser
        // un neto real, asi que se reporta igual a CashIn (no se filtra el costo por diferencia).
        var cashOutThisMonth = canSeeCost ? cashSummary.CashOutThisMonth : 0m;
        var netCashThisMonth = canSeeCost ? cashSummary.NetCashThisMonth : cashSummary.CashInThisMonth;
        var maskedCashOutByCurrency = canSeeCost ? cashOutByCurrency : new List<CurrencyAmountDto>();

        return new TreasurySummaryDto
        {
            AccountsReceivable = EconomicRulesHelper.RoundCurrency(accountsReceivable),
            AfipEligiblePending = EconomicRulesHelper.RoundCurrency(afipEligiblePending),
            CashInThisMonth = cashSummary.CashInThisMonth,
            CashOutThisMonth = cashOutThisMonth,
            NetCashThisMonth = netCashThisMonth,
            AccountsReceivableByCurrency = accountsReceivableByCurrency,
            AccountsPayableByCurrency = accountsPayableByCurrency,
            CashInByCurrency = cashInByCurrency,
            CashOutByCurrency = maskedCashOutByCurrency
        };
    }

    /// <summary>
    /// ADR-022 §4.6 (capa 4): entradas y salidas de caja del mes SEPARADAS por la moneda REAL del movimiento,
    /// leidas del LIBRO DE CAJA (CashLedgerEntry) en vez de unir Payments+SupplierPayments+ManualCashMovements
    /// al vuelo. Es el arqueo de lo que efectivamente entro/salio: el asiento ya nacio en su moneda real
    /// (un cobro cruzado entra en su moneda, no en la imputada).
    ///
    /// <para><b>Reversas (anular != borrar)</b>: una anulacion NO borra el asiento; crea una REVERSA con la
    /// direccion invertida. Para que el arqueo de del mismo numero que antes (donde un pago borrado simplemente
    /// no contaba), la reversa se imputa con signo NEGATIVO en el bucket de su direccion ORIGINAL, no como un
    /// movimiento positivo en la direccion contraria. Asi: cobro +100 y su anulacion dejan CashIn neto 0 (no
    /// CashIn 100 / CashOut 100). Ver <see cref="LoadSignedCashByCurrencyAsync"/>.</para>
    /// </summary>
    private async Task<(List<CurrencyAmountDto> CashIn, List<CurrencyAmountDto> CashOut)> GetCashByCurrencyAsync(
        CancellationToken cancellationToken)
    {
        var startOfMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var (incomeByCurrency, expenseByCurrency) = await LoadSignedCashByCurrencyAsync(startOfMonth, cancellationToken);

        var cashIn = ToOrderedCurrencyList(incomeByCurrency);
        var cashOut = ToOrderedCurrencyList(expenseByCurrency);
        return (cashIn, cashOut);
    }

    /// <summary>
    /// ADR-022 §4.6: lee el Libro de Caja del mes y suma, por moneda, los ingresos y egresos NETOS (con las
    /// reversas restando dentro del bucket de su direccion original). Devuelve dos diccionarios moneda -&gt; neto.
    /// </summary>
    private async Task<(Dictionary<string, decimal> Income, Dictionary<string, decimal> Expense)>
        LoadSignedCashByCurrencyAsync(DateTime startOfMonth, CancellationToken cancellationToken)
    {
        // Se traen las filas crudas del mes (Currency/Direction/Amount/IsReversal) y se agregan en memoria.
        // El agrupado por "direccion original + signo" no es trivial de expresar en SQL puro de forma legible,
        // y el universo (movimientos de UN mes) es chico; preferimos claridad a una sola query oscura.
        var rows = await _dbContext.CashLedgerEntries
            .AsNoTracking()
            .Where(e => e.OccurredAt >= startOfMonth)
            .Select(e => new { e.Currency, e.Direction, e.Amount, e.IsReversal })
            .ToListAsync(cancellationToken);

        var income = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var expense = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var currency = Monedas.Normalizar(row.Currency);

            // La reversa tiene la direccion INVERTIDA respecto del original. La devolvemos a su bucket original
            // con signo negativo, asi netea contra el asiento que anula sin inflar la otra direccion.
            var bucketIsIncome = row.IsReversal
                ? row.Direction == CashMovementDirections.Expense   // reversa de un Income tiene Direction=Expense
                : row.Direction == CashMovementDirections.Income;
            var signedAmount = row.IsReversal ? -row.Amount : row.Amount;

            var bucket = bucketIsIncome ? income : expense;
            bucket[currency] = bucket.TryGetValue(currency, out var current) ? current + signedAmount : signedAmount;
        }

        return (income, expense);
    }

    /// <summary>
    /// Redondea y ordena un diccionario moneda -&gt; monto en la lista por moneda que consume el front. Las
    /// monedas que netearon a 0 (un cobro y su anulacion en el mismo mes) se omiten para no mostrar lineas en 0.
    /// </summary>
    private static List<CurrencyAmountDto> ToOrderedCurrencyList(Dictionary<string, decimal> totals)
        => totals
            .Select(kvp => new CurrencyAmountDto
            {
                Currency = kvp.Key,
                Amount = EconomicRulesHelper.RoundCurrency(kvp.Value)
            })
            .Where(dto => dto.Amount != 0m)
            .OrderBy(dto => dto.Currency, StringComparer.Ordinal)
            .ToList();

    public async Task<CashSummaryDto> GetCashSummaryAsync(CancellationToken cancellationToken)
    {
        var startOfMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        // ADR-022 §4.6 (capa 4): el arqueo del mes sale del LIBRO DE CAJA (con reversas netendo en su bucket
        // original), no de unir las tres fuentes al vuelo. El desglose por moneda y los escalares salen de la
        // MISMA agregacion para que nunca discrepen entre si.
        var (incomeByCurrency, expenseByCurrency) = await LoadSignedCashByCurrencyAsync(startOfMonth, cancellationToken);

        var cashInByCurrency = ToOrderedCurrencyList(incomeByCurrency);
        var cashOutByCurrency = ToOrderedCurrencyList(expenseByCurrency);

        // Escalares de compat: suma cross-moneda (igual que la version vieja; con todo en ARS coincide con la
        // unica linea ARS). NUNCA se usa para decidir nada por moneda; el detalle real va en CashByCurrency.
        var cashInThisMonth = EconomicRulesHelper.RoundCurrency(cashInByCurrency.Sum(x => x.Amount));
        var cashOutThisMonth = EconomicRulesHelper.RoundCurrency(cashOutByCurrency.Sum(x => x.Amount));

        // ADR-022 §4.6 (fix S2): la salida de caja es informacion de costo (pagos a proveedor + devoluciones
        // de operador). Sin cobranzas.see_cost se enmascara: el escalar CashOut a 0, el desglose por moneda
        // (la columna CashOut de cada fila) a 0 y el neto se reporta igual a la entrada (no se filtra el costo
        // por diferencia). La ENTRADA (cobros = venta) queda visible. Mismo criterio fail-closed que el resto.
        var canSeeCost = await CanSeeCostAsync(cancellationToken);
        var visibleCashOutByCurrency = canSeeCost ? cashOutByCurrency : new List<CurrencyAmountDto>();
        var visibleCashOutThisMonth = canSeeCost ? cashOutThisMonth : 0m;
        var netCashThisMonth = canSeeCost
            ? EconomicRulesHelper.RoundCurrency(cashInThisMonth - cashOutThisMonth)
            : cashInThisMonth;

        var cashByCurrency = BuildCashByCurrencyRows(cashInByCurrency, visibleCashOutByCurrency);

        return new CashSummaryDto
        {
            CashInThisMonth = cashInThisMonth,
            CashOutThisMonth = visibleCashOutThisMonth,
            NetCashThisMonth = netCashThisMonth,
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

        // ADR-022 §4.6 (capa 4): los movimientos salen del LIBRO DE CAJA (CashLedgerEntry), no de unir las
        // tres fuentes al vuelo. El libro es su propia verdad: NO se filtra por IsDeleted (el libro nunca
        // borra; una anulacion es una REVERSA, una fila propia con signo invertido — Q4). Cada asiento se
        // proyecta resolviendo Description/Category/Reserva/Supplier por JOIN a traves de su FK de origen.
        var movements = _dbContext.CashLedgerEntries
            .AsNoTracking()
            .Where(e => string.IsNullOrWhiteSpace(normalizedSearch) ||
                e.Method.ToLower().Contains(normalizedSearch) ||
                (e.Payment != null && e.Payment.Reference != null && e.Payment.Reference.ToLower().Contains(normalizedSearch)) ||
                (e.Payment != null && e.Payment.Notes != null && e.Payment.Notes.ToLower().Contains(normalizedSearch)) ||
                (e.SupplierPayment != null && e.SupplierPayment.Reference != null && e.SupplierPayment.Reference.ToLower().Contains(normalizedSearch)) ||
                (e.SupplierPayment != null && e.SupplierPayment.Notes != null && e.SupplierPayment.Notes.ToLower().Contains(normalizedSearch)) ||
                (e.SupplierPayment != null && e.SupplierPayment.Supplier.Name.ToLower().Contains(normalizedSearch)) ||
                (e.ManualCashMovement != null && e.ManualCashMovement.Description.ToLower().Contains(normalizedSearch)) ||
                (e.ManualCashMovement != null && e.ManualCashMovement.Category.ToLower().Contains(normalizedSearch)) ||
                (e.ManualCashMovement != null && e.ManualCashMovement.Reference != null && e.ManualCashMovement.Reference.ToLower().Contains(normalizedSearch)) ||
                (e.Reserva != null && e.Reserva.NumeroReserva.ToLower().Contains(normalizedSearch)) ||
                (e.Supplier != null && e.Supplier.Name.ToLower().Contains(normalizedSearch)))
            .Select(e => new CashMovementDto
            {
                // DisplaySourceType (front contract): el front solo conoce CustomerPayment/SupplierPayment/
                // ManualAdjustment. Los asientos de cancelacion (OperatorRefund/ClientCreditWithdrawal) SON
                // movimientos manuales desde la vista del front, asi que se colapsan a "ManualAdjustment"
                // (mismo comportamiento que antes, cuando se surfaceaban como ManualCashMovement).
                SourceType =
                    e.SourceType == CashLedgerSourceTypes.CustomerPayment ? "CustomerPayment"
                    : e.SourceType == CashLedgerSourceTypes.SupplierPayment ? "SupplierPayment"
                    : "ManualAdjustment",
                // SourcePublicId = PublicId del ORIGEN (no del asiento): el front lo usa para editar/borrar el
                // movimiento manual (PUT/DELETE /treasury/manual-movements/{publicId}). Para reversas, el FK de
                // origen se conserva, asi que apunta al mismo origen.
                SourcePublicId =
                    e.Payment != null ? e.Payment.PublicId
                    : e.SupplierPayment != null ? e.SupplierPayment.PublicId
                    : e.ManualCashMovement != null ? e.ManualCashMovement.PublicId
                    : e.PublicId,
                Direction = e.Direction,
                Amount = e.Amount,
                Currency = e.Currency,   // moneda REAL de caja (ya nacio asi en el asiento)
                OccurredAt = e.OccurredAt,
                Method = e.Method,
                Category = e.ManualCashMovement != null ? e.ManualCashMovement.Category : null,
                Description =
                    e.Payment != null ? (e.Payment.Notes ?? "Cobranza de cliente")
                    : e.SupplierPayment != null ? (e.SupplierPayment.Notes ?? "Pago a proveedor")
                    : e.ManualCashMovement != null ? e.ManualCashMovement.Description
                    : string.Empty,
                Reference =
                    e.Payment != null ? e.Payment.Reference
                    : e.SupplierPayment != null ? e.SupplierPayment.Reference
                    : e.ManualCashMovement != null ? e.ManualCashMovement.Reference
                    : null,
                ReservaPublicId = e.Reserva != null ? (Guid?)e.Reserva.PublicId : null,
                NumeroReserva = e.Reserva != null ? e.Reserva.NumeroReserva : null,
                SupplierPublicId = e.Supplier != null ? (Guid?)e.Supplier.PublicId : null,
                SupplierName = e.Supplier != null ? e.Supplier.Name : null,
                // IsManual = false solo para cobro/pago directo; los de cancelacion y ajustes son "manuales".
                IsManual = e.SourceType != CashLedgerSourceTypes.CustomerPayment
                        && e.SourceType != CashLedgerSourceTypes.SupplierPayment,
                // Tipo crudo del asiento (no colapsado): lo usa el enmascarado de costo de abajo para tapar
                // los refund de operador sin tapar los ajustes manuales genuinos ni la devolucion al cliente.
                LedgerSourceType = e.SourceType
            });

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

        var page = await movements.ToPagedResponseAsync(query, cancellationToken);

        // ADR-022 §4.6 (B3 + fix S2): enmascarado de COSTO. El libro expone montos que son informacion de
        // costo. Un usuario sin cobranzas.see_cost no debe ver esos numeros. Se anula el monto de:
        //   - pagos a proveedor (LedgerSourceType=SupplierPayment), incluidas sus reversas; y
        //   - devoluciones recibidas del operador (LedgerSourceType=OperatorRefund): el monto que el operador
        //     devuelve es informacion de costo (RK-9), por eso tambien se tapa.
        // NO se enmascaran: cobros del cliente (venta), ajustes manuales genuinos (gasto propio que el cajero
        // sin see_cost si debe ver) ni la devolucion FISICA al cliente (ClientCreditWithdrawal, no es costo).
        // Se usa LedgerSourceType (tipo crudo del asiento), no el SourceType colapsado del front. El resto del
        // movimiento (metodo, fecha, proveedor, referencia) sigue visible: se oculta el numero, no el egreso.
        if (!await CanSeeCostAsync(cancellationToken))
        {
            foreach (var movement in page.Items.Where(m =>
                m.LedgerSourceType == CashLedgerSourceTypes.SupplierPayment ||
                m.LedgerSourceType == CashLedgerSourceTypes.OperatorRefund))
            {
                movement.Amount = 0m;
            }
        }

        return page;
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
            // ADR-022 §4.12 (T2): moneda real del gasto/ajuste. null/vacio -> ARS (Normalizar lo resuelve).
            Currency = Monedas.Normalizar(request.Currency),
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

        // ADR-022 §4.4: el asiento de caja (gasto/ajuste manual puro) se escribe en la MISMA SaveChanges.
        // SourceType = ManualAdjustment (no tiene FK de cancelacion). La moneda sale del propio manual
        // (entity.Currency, default ARS hasta que la capa 6 agregue Currency al request).
        var manualLedgerEntry = TravelApi.Domain.Helpers.CashLedgerEntryFactory.ForManualMovement(
            entity, currencyOverride: null, actorUserId: entity.CreatedBy, actorUserName: entity.CreatedBy);
        _dbContext.CashLedgerEntries.Add(manualLedgerEntry);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return await GetManualMovementDtoAsync(entity.Id, cancellationToken);
    }

    /// <summary>
    /// ADR-022 §4.5: marca el asiento vigente de un ManualCashMovement como revertido e inserta su
    /// reversa (orden estricto del indice unico parcial). NO hace SaveChanges. Si no hay asiento vigente
    /// (movimiento legacy sin backfill), no hace nada.
    /// </summary>
    private async Task ReverseLiveManualMovementLedgerEntryAsync(int manualMovementId, CancellationToken cancellationToken)
    {
        var live = await _dbContext.CashLedgerEntries
            .FirstOrDefaultAsync(
                e => e.ManualCashMovementId == manualMovementId && !e.IsReversal && !e.IsReversed,
                cancellationToken);
        if (live is null) return;

        live.IsReversed = true;
        var reversal = TravelApi.Domain.Helpers.CashLedgerEntryFactory.Reverse(
            live, DateTime.UtcNow, live.CreatedByUserId, live.CreatedByUserName);
        _dbContext.CashLedgerEntries.Add(reversal);
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
        // ADR-022 §4.12 (T2): moneda real del gasto/ajuste. null/vacio -> ARS.
        entity.Currency = Monedas.Normalizar(request.Currency);
        entity.OccurredAt = request.OccurredAt == default ? entity.OccurredAt : request.OccurredAt.ToUniversalTime();
        entity.Method = request.Method.Trim();
        entity.Category = request.Category.Trim();
        entity.Description = request.Description.Trim();
        entity.Reference = request.Reference?.Trim();
        entity.RelatedReservaId = relatedReservaId;
        entity.RelatedSupplierId = relatedSupplierId;

        // ADR-022 §4.5: editar el monto/moneda del manual = reversa del asiento viejo + asiento nuevo.
        await ReverseLiveManualMovementLedgerEntryAsync(entity.Id, cancellationToken);
        var updatedLedgerEntry = TravelApi.Domain.Helpers.CashLedgerEntryFactory.ForManualMovement(
            entity, currencyOverride: null, actorUserId: entity.CreatedBy, actorUserName: entity.CreatedBy);
        _dbContext.CashLedgerEntries.Add(updatedLedgerEntry);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return await GetManualMovementDtoAsync(entity.Id, cancellationToken);
    }

    public async Task DeleteManualMovementAsync(int id, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.ManualCashMovements.FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Movimiento manual no encontrado.");

        entity.IsVoided = true;
        entity.VoidedAt = DateTime.UtcNow;

        // ADR-022 §4.5: anular el manual NO borra su asiento: se marca IsReversed=true y se inserta su
        // reversa, asi la caja netea a 0 sin reescribir la historia.
        await ReverseLiveManualMovementLedgerEntryAsync(entity.Id, cancellationToken);

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

        // ADR-022 §4.13 (T3, Q3): bloqueo DURO. Un movimiento manual NO puede impersonar un cobro de cliente
        // ni un pago a proveedor: esos hechos tienen su puerta unica (Payment / SupplierPayment). Permitirlo
        // crearia una segunda puerta para la misma plata (doble conteo en el Libro de Caja). El resto de
        // categorias (gastos, ajustes) es libre.
        if (ManualCashMovementCategoryRules.IsBlocked(request.Category))
            throw new ArgumentException(
                "Esa categoria corresponde a un cobro de cliente o pago a proveedor; usá la pantalla de cobros/pagos, no un movimiento manual.");

        if (string.IsNullOrWhiteSpace(request.Description))
            throw new ArgumentException("La descripcion es obligatoria.");

        // ADR-022 §4.12 (T2): si vino moneda, debe ser soportada. null/vacio = ARS (legacy), no se valida.
        if (!string.IsNullOrWhiteSpace(request.Currency) && !Monedas.EsSoportada(Monedas.Normalizar(request.Currency)))
            throw new ArgumentException($"Moneda no soportada: '{request.Currency}'.");
    }
}
