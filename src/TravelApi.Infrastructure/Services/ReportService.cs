using System.Globalization;
using System.Security.Claims;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class ReportService : IReportService
{
    private readonly AppDbContext _dbContext;
    private readonly IBnaExchangeRateService _bnaExchangeRateService;
    // B1.15 Fase 2a (FIX 4): opcionales para no romper unit tests que instancian
    // ReportService con el ctor de 2 args.
    private readonly IUserPermissionResolver? _permissionResolver;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    // ADR-022 §4.7 (T4): fuente unica AR/AP. Opcional (default null) para no romper los unit tests que
    // instancian ReportService sin el; con null se usa el fallback inline (misma query) — ver
    // BuildDashboardByCurrencyAsync.
    private readonly IFinancePositionService? _financePositionService;

    public ReportService(
        AppDbContext dbContext,
        IBnaExchangeRateService bnaExchangeRateService,
        IUserPermissionResolver? permissionResolver = null,
        IHttpContextAccessor? httpContextAccessor = null,
        IFinancePositionService? financePositionService = null)
    {
        _dbContext = dbContext;
        _bnaExchangeRateService = bnaExchangeRateService;
        _permissionResolver = permissionResolver;
        _httpContextAccessor = httpContextAccessor;
        _financePositionService = financePositionService;
    }

    public async Task<DashboardResponse> GetDashboardAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var startOfMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        // B1.15 Fase 2a (FIX 4): resolver scope segun permisos del user actual.
        // - sin cobranzas.see_cost: enmascarar costos / margen / pagos a proveedores y costs/profit del trend.
        // - sin reservas.view_all: filtrar pendientes y proximos por owner.
        var httpUser = _httpContextAccessor?.HttpContext?.User;
        var currentUserId = httpUser?.FindFirstValue(ClaimTypes.NameIdentifier);
        var isAdmin = httpUser?.IsInRole("Admin") ?? false;

        var perms = (_permissionResolver is null || string.IsNullOrEmpty(currentUserId))
            ? null
            : await _permissionResolver.GetPermissionsAsync(currentUserId, cancellationToken);

        var canSeeCost = isAdmin || (perms?.Contains(Permissions.CobranzasSeeCost) ?? false);
        var hasReservasViewAll = isAdmin || (perms?.Contains(Permissions.ReservasViewAll) ?? false);

        var filesByStatus = await _dbContext.Reservas
            .GroupBy(f => f.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var presupuestos = filesByStatus.FirstOrDefault(x => x.Status == EstadoReserva.Budget)?.Count ?? 0;
        var reservados = filesByStatus.FirstOrDefault(x => x.Status == EstadoReserva.Confirmed)?.Count ?? 0;
        var operativos = filesByStatus.FirstOrDefault(x => x.Status == EstadoReserva.Traveling)?.Count ?? 0;
        var cerrados = filesByStatus.FirstOrDefault(x => x.Status == EstadoReserva.Closed)?.Count ?? 0;
        var cancelados = filesByStatus.FirstOrDefault(x => x.Status == EstadoReserva.Cancelled)?.Count ?? 0;

        // ADR-022 (fix #3): solo pagos que MOVIERON caja (AffectsCash). Excluye los Payment "puente" de
        // AffectsCash=false (sobrepago "SaldoAFavor" y reversion de NC) que existen para imputar saldo, no
        // para mover plata: si se contaran, su monto negativo ensuciaria el total de cobranzas del mes.
        var paymentsThisMonth = await _dbContext.Payments
            .Where(p => p.PaidAt >= startOfMonth && !p.IsDeleted && p.AffectsCash)
            .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;

        var outstandingBalance = await _dbContext.Reservas
            .Where(f => f.Status != EstadoReserva.Closed && f.Status != EstadoReserva.Cancelled && f.Status != EstadoReserva.Budget)
            .SumAsync(f => (decimal?)f.Balance, cancellationToken) ?? 0m;

        var salesThisMonth = await _dbContext.Reservas
            .Where(f => f.CreatedAt >= startOfMonth && f.Status != EstadoReserva.Budget && f.Status != EstadoReserva.Cancelled)
            .SumAsync(f => (decimal?)f.TotalSale, cancellationToken) ?? 0m;

        var costsThisMonth = await _dbContext.Reservas
            .Where(f => f.CreatedAt >= startOfMonth && f.Status != EstadoReserva.Budget && f.Status != EstadoReserva.Cancelled)
            .SumAsync(f => (decimal?)f.TotalCost, cancellationToken) ?? 0m;

        var supplierPaymentsThisMonth = await _dbContext.SupplierPayments
            .Where(p => p.PaidAt >= startOfMonth)
            .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;

        var grossMarginThisMonth = salesThisMonth - costsThisMonth;

        // Filter mine para listas operativas: si no tiene reservas.view_all,
        // restringir por ResponsibleUserId == currentUser.
        var upcomingQuery = _dbContext.Reservas
            .Where(f => f.StartDate >= now && f.StartDate <= now.AddDays(7) && f.Status != EstadoReserva.Cancelled);

        // ADR-021 Capa 6 (B2): el top-N de deudoras se calcula POR MONEDA contra la tabla hija
        // ReservaMoneyByCurrency (ordenar por el escalar surrogate mezclaria USD+ARS y daria un ranking
        // sin sentido). Se traen las top 5 de cada moneda; con todo en ARS la lista USD viene vacia y el
        // resultado es identico al top-5 de antes. Join explicito contra Reservas (no nav implicita) para
        // que corra igual en Postgres y en el provider InMemory de los tests; el filtro de owner se aplica
        // sobre la reserva del join.
        if (!hasReservasViewAll)
        {
            // Sentinel imposible si no hay user resoluble (devuelve lista vacia).
            var ownerFilter = string.IsNullOrEmpty(currentUserId) ? "__no_user__" : currentUserId;
            upcomingQuery = upcomingQuery.Where(f => f.ResponsibleUserId == ownerFilter);
        }

        var ownerFilterForPending = hasReservasViewAll
            ? null
            : (string.IsNullOrEmpty(currentUserId) ? "__no_user__" : currentUserId);

        var pendingByCurrencyQuery =
            from row in _dbContext.ReservaMoneyByCurrency
            join reservaPadre in _dbContext.Reservas on row.ReservaId equals reservaPadre.Id
            where row.Balance > 0
                && reservaPadre.Status != EstadoReserva.Closed
                && reservaPadre.Status != EstadoReserva.Cancelled
                && (ownerFilterForPending == null || reservaPadre.ResponsibleUserId == ownerFilterForPending)
            select new { row.Currency, row.Balance, reservaPadre.PublicId, reservaPadre.NumeroReserva, reservaPadre.Name, reservaPadre.Status };

        var pendingReservas = new List<PendingReservaDto>();
        foreach (var currency in Monedas.Soportadas)
        {
            var topForCurrency = await pendingByCurrencyQuery
                .Where(x => x.Currency == currency)
                .OrderByDescending(x => x.Balance)
                .Take(5)
                .Select(x => new PendingReservaDto(
                    x.PublicId,
                    x.NumeroReserva,
                    x.Name,
                    x.Balance,
                    x.Status.ToString(),
                    currency))
                .ToListAsync(cancellationToken);
            pendingReservas.AddRange(topForCurrency);
        }

        var upcomingTrips = await upcomingQuery
            .OrderBy(f => f.StartDate)
            .Take(5)
            .Select(f => new UpcomingTripDto(f.PublicId, f.NumeroReserva, f.Name, f.StartDate!.Value, f.Status.ToString()))
            .ToListAsync(cancellationToken);

        var sixMonthsAgo = startOfMonth.AddMonths(-5);

        var monthlyData = await _dbContext.Reservas
            .Where(f => f.CreatedAt >= sixMonthsAgo && f.Status != EstadoReserva.Budget && f.Status != EstadoReserva.Cancelled)
            .GroupBy(f => new { f.CreatedAt.Year, f.CreatedAt.Month })
            .Select(g => new
            {
                Year = g.Key.Year,
                Month = g.Key.Month,
                TotalSales = g.Sum(f => f.TotalSale),
                TotalCosts = g.Sum(f => f.TotalCost)
            })
            .ToListAsync(cancellationToken);

        var historicalTrend = new List<MonthlyMetricDto>();
        for (int i = 0; i < 6; i++)
        {
            var targetDate = sixMonthsAgo.AddMonths(i);
            var record = monthlyData.FirstOrDefault(m => m.Year == targetDate.Year && m.Month == targetDate.Month);

            var sales = record?.TotalSales ?? 0m;
            var costs = record?.TotalCosts ?? 0m;
            var profit = sales - costs;
            var monthName = CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedMonthName(targetDate.Month);

            // Sin cobranzas.see_cost: enmascarar costs y profit del trend.
            // El sales sigue visible (es informacion de facturacion bruta, no costo).
            if (!canSeeCost)
            {
                historicalTrend.Add(new MonthlyMetricDto(monthName, sales, 0m, 0m));
            }
            else
            {
                historicalTrend.Add(new MonthlyMetricDto(monthName, sales, costs, profit));
            }
        }

        var statusDistribution = new StatusDistributionDto(
            presupuestos,
            reservados,
            operativos,
            cerrados,
            cancelados
        );

        var activePotentialCustomers = await _dbContext.Leads
            .CountAsync(lead => lead.Status != LeadStatus.Won && lead.Status != LeadStatus.Lost, cancellationToken);

        // La cotizacion BNA es INFORMATIVA: nunca debe bloquear el dashboard. GetUsdSellerRateAsync puede
        // disparar un fetch HTTP en vivo a bna.com.ar (timeout interno de 10s) y, si BNA no responde, dejaria la
        // pantalla en skeleton todo ese tiempo. No hay refresher en background, asi que aca acotamos la espera:
        // si la cotizacion no llega en una ventana corta, nos degradamos al ultimo snapshot persistido (lectura
        // local, sin red) y, en ultima instancia, a null. El contrato DashboardResponse.bnaUsdSellerRate admite
        // null y el front ya lo tolera.
        var bnaUsdSellerRate = await GetDashboardBnaRateAsync(cancellationToken);

        // ADR-021 Capa 6: desgloses por moneda (aditivos). Cobros/pagos por moneda REAL del movimiento;
        // ventas/costos por moneda del servicio (tabla hija filtrada por CreatedAt del mes); saldo
        // pendiente y cuentas por pagar por moneda del saldo contra las tablas hijas. CostosDelMes y
        // CuentasPorPagar se enmascaran (lista vacia) si el user no ve costos, igual que los escalares.
        var porMoneda = await BuildDashboardByCurrencyAsync(startOfMonth, canSeeCost, cancellationToken);

        // B1.15 Fase 2a (FIX 4): si el user NO tiene cobranzas.see_cost, ocultar
        // CostosDelMes / MargenBruto / PagosProveedores. Patron consistente con
        // ApplyCostMaskingAsync de ReservaService (mascara con 0 — la decision de
        // null vs 0 contractual queda diferida a B1.15.x).
        return new DashboardResponse(
            Presupuestos: presupuestos,
            Reservados: reservados,
            Operativos: operativos,
            CobrosDelMes: paymentsThisMonth,
            SaldoPendiente: outstandingBalance,
            VentasDelMes: salesThisMonth,
            CostosDelMes: canSeeCost ? costsThisMonth : 0m,
            MargenBruto: canSeeCost ? grossMarginThisMonth : 0m,
            PagosProveedores: canSeeCost ? supplierPaymentsThisMonth : 0m,
            ReservasPendientes: pendingReservas,
            ProximosViajes: upcomingTrips,
            TendenciaHistorica: historicalTrend,
            DistribucionEstados: statusDistribution,
            BnaUsdSellerRate: bnaUsdSellerRate,
            ActivePotentialCustomers: activePotentialCustomers,
            PorMoneda: porMoneda
        );
    }

    /// <summary>
    /// Obtiene la cotizacion BNA para el dashboard SIN que la pantalla quede esperando a Banco Nacion.
    ///
    /// <para>Corre el fetch en vivo (que puede ir a la red, timeout interno de 10s) contra una ventana corta
    /// (<see cref="DashboardBnaTimeout"/>). Si gana la ventana, o si el fetch falla, nos degradamos al ultimo
    /// snapshot persistido en DB (lectura local, sin red). Si ni siquiera hay snapshot persistido, devolvemos
    /// null. NUNCA propaga excepcion: la cotizacion es informativa y no puede tumbar el dashboard.</para>
    ///
    /// <para>El CancellationTokenSource se linkea al token del request para que, si el usuario abandona la
    /// pantalla, tambien se corte el intento en vivo.</para>
    /// </summary>
    private async Task<BnaUsdSellerRateDto?> GetDashboardBnaRateAsync(CancellationToken cancellationToken)
    {
        // Ventana propia para el intento en vivo: si BNA no contesta a tiempo, cancelamos y caemos al snapshot.
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(DashboardBnaTimeout);

        try
        {
            return await _bnaExchangeRateService.GetUsdSellerRateAsync(timeoutSource.Token);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout de la ventana corta (OperationCanceledException por el linked token) o cualquier falla del
            // fetch en vivo. Degradamos al snapshot persistido leyendo con el token ORIGINAL del request (no el
            // ya cancelado). Si el usuario abandono el request, cancellationToken.IsCancellationRequested es true
            // y dejamos que la excepcion de cancelacion del request se propague (no es nuestro caso a degradar).
            return await TryLoadPersistedBnaRateAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Lee el ultimo snapshot BNA persistido sin tocar la red. Si tambien falla (ej. DB), devuelve null en vez de
    /// tumbar el dashboard: la cotizacion es informativa.
    /// </summary>
    private async Task<BnaUsdSellerRateDto?> TryLoadPersistedBnaRateAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _bnaExchangeRateService.GetPersistedUsdSellerRateAsync(cancellationToken);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Cuanto espera el dashboard la cotizacion en vivo antes de degradarse al snapshot persistido. Corto a
    /// proposito: la cotizacion es secundaria y la pantalla no puede quedar bloqueada por un servicio externo.
    /// </summary>
    private static readonly TimeSpan DashboardBnaTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// ADR-021 Capa 6: arma los desgloses por moneda del dashboard. Cada lista nunca mezcla monedas.
    /// Cobros/pagos del mes van por la moneda REAL del movimiento; ventas/costos del mes y saldo
    /// pendiente / cuentas por pagar van por la moneda del saldo contra las tablas hijas materializadas.
    /// CostosDelMes y CuentasPorPagar quedan vacios si <paramref name="canSeeCost"/> es false (mismo
    /// criterio de enmascarado que los escalares).
    /// </summary>
    private async Task<DashboardByCurrencyDto> BuildDashboardByCurrencyAsync(
        DateTime startOfMonth, bool canSeeCost, CancellationToken cancellationToken)
    {
        // Cobros del mes por moneda REAL del cobro.
        var cobros = await SumByCurrencyAsync(
            _dbContext.Payments
                .Where(p => p.PaidAt >= startOfMonth && !p.IsDeleted)
                .GroupBy(p => p.Currency)
                .Select(g => new CurrencyAmount(g.Key, g.Sum(p => p.Amount))),
            cancellationToken);

        // Pagos a proveedor del mes por moneda REAL del egreso.
        var pagosProveedores = await SumByCurrencyAsync(
            _dbContext.SupplierPayments
                .Where(p => p.PaidAt >= startOfMonth)
                .GroupBy(p => p.Currency)
                .Select(g => new CurrencyAmount(g.Key, g.Sum(p => p.Amount))),
            cancellationToken);

        // Ventas/costos del mes por moneda del servicio (tabla hija), filtrando reservas creadas en el mes
        // y excluyendo Budget/Cancelled (mismo filtro que el escalar VentasDelMes/CostosDelMes). Join
        // explicito contra Reservas (no nav implicita) para correr igual en Postgres e InMemory.
        var monthQuery =
            from row in _dbContext.ReservaMoneyByCurrency
            join reservaPadre in _dbContext.Reservas on row.ReservaId equals reservaPadre.Id
            where reservaPadre.CreatedAt >= startOfMonth
                && reservaPadre.Status != EstadoReserva.Budget
                && reservaPadre.Status != EstadoReserva.Cancelled
            select new { row.Currency, row.TotalSale, row.TotalCost };

        var ventas = await SumByCurrencyAsync(
            monthQuery.GroupBy(x => x.Currency).Select(g => new CurrencyAmount(g.Key, g.Sum(x => x.TotalSale))),
            cancellationToken);

        var costos = canSeeCost
            ? await SumByCurrencyAsync(
                monthQuery.GroupBy(x => x.Currency).Select(g => new CurrencyAmount(g.Key, g.Sum(x => x.TotalCost))),
                cancellationToken)
            : new List<CurrencyAmount>();

        // ADR-022 §4.7 (T4): cuentas por cobrar (AR) y por pagar (AP) por moneda salen ahora de la FUENTE
        // UNICA compartida con tesoreria, para que dashboard y tesoreria den EXACTAMENTE el mismo numero.
        // Si no se inyecto el servicio (unit tests con ctor corto), se construye sobre el mismo DbContext.
        var financePosition = _financePositionService ?? new FinancePositionService(_dbContext);

        // AR (cuentas por cobrar): plata de venta -> NO se enmascara.
        var saldoPendiente = (await financePosition.GetAccountsReceivableByCurrencyAsync(cancellationToken))
            .Select(x => new CurrencyAmount(x.Currency, x.Amount))
            .ToList();

        // AP (cuentas por pagar): dato de costo -> se enmascara si no ve costos.
        var cuentasPorPagar = canSeeCost
            ? (await financePosition.GetAccountsPayableByCurrencyAsync(cancellationToken))
                .Select(x => new CurrencyAmount(x.Currency, x.Amount))
                .ToList()
            : new List<CurrencyAmount>();

        return new DashboardByCurrencyDto(
            CobrosDelMes: cobros,
            PagosProveedores: canSeeCost ? pagosProveedores : new List<CurrencyAmount>(),
            VentasDelMes: ventas,
            CostosDelMes: costos,
            SaldoPendiente: saldoPendiente,
            CuentasPorPagar: cuentasPorPagar);
    }

    /// <summary>
    /// Ejecuta el GroupBy-por-Currency en SQL, normaliza la moneda (null/vacio -> ARS) y redondea.
    /// Devuelve la lista ordenada por moneda para que el shape sea estable en los tests.
    /// </summary>
    private static async Task<List<CurrencyAmount>> SumByCurrencyAsync(
        IQueryable<CurrencyAmount> grouped, CancellationToken cancellationToken)
    {
        var raw = await grouped.ToListAsync(cancellationToken);

        // La normalizacion (null -> ARS) se hace en memoria: una columna Currency con default 'ARS' a
        // nivel BD no deberia traer nulls, pero los servicios genericos legacy podrian; agruparlos en ARS.
        var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var item in raw)
        {
            var key = Monedas.Normalizar(item.Currency);
            totals[key] = totals.TryGetValue(key, out var current) ? current + item.Amount : item.Amount;
        }

        return totals
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => new CurrencyAmount(kvp.Key, EconomicRulesHelper.RoundCurrency(kvp.Value)))
            .ToList();
    }

    public async Task<ReportsSummaryResponse> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var totalCustomers = await _dbContext.Customers.CountAsync(cancellationToken);
        var totalReservas = await _dbContext.Reservas.CountAsync(cancellationToken);
        var totalReservations = await _dbContext.Servicios.CountAsync(cancellationToken);
        
        // ADR-022 (fix #3): solo pagos que movieron caja; excluye los Payment puente AffectsCash=false
        // (SaldoAFavor de sobrepago + reversion de NC) que netarian un negativo fantasma en la facturacion.
        var totalRevenue = await _dbContext.Payments.Where(p => !p.IsDeleted && p.AffectsCash).SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;
        var totalCosts = await _dbContext.Reservas.SumAsync(f => (decimal?)f.TotalCost, cancellationToken) ?? 0m;
        var totalSupplierPayments = await _dbContext.SupplierPayments.SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;
        var outstandingBalance = await _dbContext.Reservas.SumAsync(f => (decimal?)f.Balance, cancellationToken) ?? 0m;
        var totalSales = await _dbContext.Reservas.SumAsync(f => (decimal?)f.TotalSale, cancellationToken) ?? 0m;
        var grossMargin = totalSales - totalCosts;

        return new ReportsSummaryResponse(
            totalCustomers,
            totalReservas,
            totalReservations,
            totalRevenue,
            outstandingBalance,
            totalCosts,
            totalSupplierPayments,
            totalSales,
            grossMargin);
    }

    public async Task<object> GetDetailedReportAsync(DateTime? from, DateTime? to, CancellationToken cancellationToken)
    {
        var dateFrom = from?.ToUniversalTime() ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var dateTo = to?.ToUniversalTime() ?? DateTime.UtcNow;

        var filesInPeriod = await _dbContext.Reservas
            .Where(f => f.CreatedAt >= dateFrom && f.CreatedAt <= dateTo 
                && f.Status != EstadoReserva.Budget && f.Status != EstadoReserva.Cancelled)
            .Select(f => new { f.TotalSale, f.TotalCost, f.Balance, f.Status })
            .ToListAsync(cancellationToken);

        var totalSales = filesInPeriod.Sum(f => f.TotalSale);
        var totalCosts = filesInPeriod.Sum(f => f.TotalCost);
        var grossMargin = totalSales - totalCosts;
        var marginPercent = totalSales > 0 ? Math.Round((grossMargin / totalSales) * 100, 1) : 0;

        // ADR-022 (fix #3): solo pagos que movieron caja; excluye los Payment puente AffectsCash=false.
        var customerPayments = await _dbContext.Payments
            .Where(p => p.PaidAt >= dateFrom && p.PaidAt <= dateTo && !p.IsDeleted && p.AffectsCash)
            .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;

        var supplierPayments = await _dbContext.SupplierPayments
            .Where(p => p.PaidAt >= dateFrom && p.PaidAt <= dateTo)
            .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;

        // ADR-021 Capa 7: cuentas por pagar POR MONEDA. Antes esta lista usaba el escalar
        // Supplier.CurrentBalance, que mezcla ARS+USD en un solo numero. Ahora cada fila lleva su
        // moneda: un proveedor que debe en dos monedas produce DOS filas (una por moneda), NUNCA una
        // fila con monto mezclado. Se lee de la tabla hija SupplierBalanceByCurrency (ya materializada
        // por las capas previas). Join explicito contra Suppliers para traer nombre y filtrar activos,
        // y correr igual en Postgres e InMemory.
        var supplierDebtsByCurrency =
            from row in _dbContext.SupplierBalanceByCurrency
            join supplier in _dbContext.Suppliers on row.SupplierId equals supplier.Id
            where supplier.IsActive && row.Balance != 0
            select new { supplier.PublicId, supplier.Name, row.Currency, row.Balance };

        var supplierDebtsRaw = await supplierDebtsByCurrency
            .OrderByDescending(x => x.Balance)
            .ToListAsync(cancellationToken);

        var supplierDebts = supplierDebtsRaw
            .Select(x => new
            {
                x.PublicId,
                x.Name,
                Currency = Monedas.Normalizar(x.Currency),
                CurrentBalance = EconomicRulesHelper.RoundCurrency(x.Balance)
            })
            .ToList();

        var topCustomers = await _dbContext.Reservas
            .Where(f => f.CreatedAt >= dateFrom && f.CreatedAt <= dateTo 
                && f.Status != EstadoReserva.Budget && f.Status != EstadoReserva.Cancelled
                && f.PayerId != null)
            .GroupBy(f => new { f.PayerId, f.Payer!.PublicId, f.Payer!.FullName })
            .Select(g => new { 
                PublicId = g.Key.PublicId,
                Name = g.Key.FullName, 
                TotalSale = g.Sum(f => f.TotalSale),
                FileCount = g.Count(),
                PendingBalance = g.Sum(f => f.Balance)
            })
            .OrderByDescending(x => x.TotalSale)
            .Take(10)
            .ToListAsync(cancellationToken);

        var monthlyBreakdown = await _dbContext.Reservas
            .Where(f => f.CreatedAt >= dateFrom && f.CreatedAt <= dateTo 
                && f.Status != EstadoReserva.Budget && f.Status != EstadoReserva.Cancelled)
            .GroupBy(f => new { f.CreatedAt.Year, f.CreatedAt.Month })
            .Select(g => new {
                Year = g.Key.Year,
                Month = g.Key.Month,
                Sales = g.Sum(f => f.TotalSale),
                Costs = g.Sum(f => f.TotalCost),
                FileCount = g.Count()
            })
            .OrderBy(x => x.Year).ThenBy(x => x.Month)
            .ToListAsync(cancellationToken);

        var monthlyData = monthlyBreakdown.Select(m => new {
            Month = CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedMonthName(m.Month) + " " + m.Year,
            Sales = m.Sales,
            Costs = m.Costs,
            Margin = m.Sales - m.Costs,
            ReservaCount = m.FileCount
        });

        // ADR-021 Capa 7: desglose por moneda del summary del reporte (mismo periodo from/to). Reusa la
        // misma agregacion por moneda que el dashboard (SumByCurrencyAsync + tablas hijas), sin duplicar
        // formulas. Endpoint Admin-only -> canSeeCost = true aca (Admin ve costos): costos/pagos/CxP por
        // moneda van completos. Si el endpoint dejara de ser Admin-only, pasar el flag real de see_cost.
        var porMoneda = await BuildDetailedSummaryByCurrencyAsync(dateFrom, dateTo, canSeeCost: true, cancellationToken);

        return new {
            Period = new { From = dateFrom, To = dateTo },
            Summary = new { TotalSales = totalSales, TotalCosts = totalCosts, GrossMargin = grossMargin, MarginPercent = marginPercent, CustomerPayments = customerPayments, SupplierPayments = supplierPayments, ReservasCount = filesInPeriod.Count, PorMoneda = porMoneda },
            SupplierDebts = supplierDebts,
            TopCustomers = topCustomers,
            MonthlyBreakdown = monthlyData
        };
    }

    /// <summary>
    /// ADR-021 Capa 7: desglose por moneda del summary de /reports/detailed para un periodo [from, to].
    /// Cobros/pagos por moneda REAL del movimiento; ventas/costos por moneda del servicio (tabla hija,
    /// reservas creadas en el periodo, excluye Budget/Cancelled); saldo pendiente y cuentas por pagar por
    /// moneda del saldo contra las tablas hijas. Costos / pagos a proveedor / cuentas por pagar quedan
    /// vacios si <paramref name="canSeeCost"/> es false (mismo enmascarado que el dashboard).
    /// </summary>
    private async Task<DashboardByCurrencyDto> BuildDetailedSummaryByCurrencyAsync(
        DateTime dateFrom, DateTime dateTo, bool canSeeCost, CancellationToken cancellationToken)
    {
        var cobros = await SumByCurrencyAsync(
            _dbContext.Payments
                .Where(p => p.PaidAt >= dateFrom && p.PaidAt <= dateTo && !p.IsDeleted)
                .GroupBy(p => p.Currency)
                .Select(g => new CurrencyAmount(g.Key, g.Sum(p => p.Amount))),
            cancellationToken);

        var pagosProveedores = canSeeCost
            ? await SumByCurrencyAsync(
                _dbContext.SupplierPayments
                    .Where(p => p.PaidAt >= dateFrom && p.PaidAt <= dateTo)
                    .GroupBy(p => p.Currency)
                    .Select(g => new CurrencyAmount(g.Key, g.Sum(p => p.Amount))),
                cancellationToken)
            : new List<CurrencyAmount>();

        var periodQuery =
            from row in _dbContext.ReservaMoneyByCurrency
            join reservaPadre in _dbContext.Reservas on row.ReservaId equals reservaPadre.Id
            where reservaPadre.CreatedAt >= dateFrom && reservaPadre.CreatedAt <= dateTo
                && reservaPadre.Status != EstadoReserva.Budget
                && reservaPadre.Status != EstadoReserva.Cancelled
            select new { row.Currency, row.TotalSale, row.TotalCost };

        var ventas = await SumByCurrencyAsync(
            periodQuery.GroupBy(x => x.Currency).Select(g => new CurrencyAmount(g.Key, g.Sum(x => x.TotalSale))),
            cancellationToken);

        var costos = canSeeCost
            ? await SumByCurrencyAsync(
                periodQuery.GroupBy(x => x.Currency).Select(g => new CurrencyAmount(g.Key, g.Sum(x => x.TotalCost))),
                cancellationToken)
            : new List<CurrencyAmount>();

        // Saldo pendiente (cuentas por cobrar) por moneda: no es un dato del periodo sino el saldo
        // vigente, igual que el escalar de otros reportes. Excluye Closed/Cancelled/Budget.
        var saldoPendienteQuery =
            from row in _dbContext.ReservaMoneyByCurrency
            join reservaPadre in _dbContext.Reservas on row.ReservaId equals reservaPadre.Id
            where reservaPadre.Status != EstadoReserva.Closed
                && reservaPadre.Status != EstadoReserva.Cancelled
                && reservaPadre.Status != EstadoReserva.Budget
                && row.Balance > 0
            select new { row.Currency, row.Balance };

        var saldoPendiente = await SumByCurrencyAsync(
            saldoPendienteQuery.GroupBy(x => x.Currency).Select(g => new CurrencyAmount(g.Key, g.Sum(x => x.Balance))),
            cancellationToken);

        var cuentasPorPagar = canSeeCost
            ? await SumByCurrencyAsync(
                _dbContext.SupplierBalanceByCurrency
                    .Where(row => row.Balance > 0)
                    .GroupBy(row => row.Currency)
                    .Select(g => new CurrencyAmount(g.Key, g.Sum(row => row.Balance))),
                cancellationToken)
            : new List<CurrencyAmount>();

        return new DashboardByCurrencyDto(
            CobrosDelMes: cobros,
            PagosProveedores: pagosProveedores,
            VentasDelMes: ventas,
            CostosDelMes: costos,
            SaldoPendiente: saldoPendiente,
            CuentasPorPagar: cuentasPorPagar);
    }

    public async Task<IEnumerable<object>> GetDetailedReceivablesAsync(CancellationToken cancellationToken)
    {
        // ADR-021 Capa 7: cuentas por cobrar POR MONEDA del cliente. Antes esta lista usaba el escalar
        // Customer.CurrentBalance, que mezcla ARS+USD en un solo numero. El saldo real por moneda no vive
        // en el cliente sino en las reservas (tabla hija ReservaMoneyByCurrency). Se agrega el saldo
        // positivo de las reservas vigentes (no Closed/Cancelled/Budget) por cliente + moneda: un cliente
        // que debe en dos monedas produce DOS filas, NUNCA una fila con monto mezclado.
        //
        // Se excluyen Closed/Cancelled/Budget para que el saldo por moneda sea coherente con el resto de
        // los reportes (la deuda exigible). Por eso el total por cliente puede no coincidir con el escalar
        // legacy Customer.CurrentBalance, que sumaba todo sin filtrar estado.
        var receivablesByCurrency =
            from row in _dbContext.ReservaMoneyByCurrency
            join reservaPadre in _dbContext.Reservas on row.ReservaId equals reservaPadre.Id
            join customer in _dbContext.Customers on reservaPadre.PayerId equals customer.Id
            where row.Balance > 0
                && customer.IsActive
                && reservaPadre.Status != EstadoReserva.Closed
                && reservaPadre.Status != EstadoReserva.Cancelled
                && reservaPadre.Status != EstadoReserva.Budget
            group new { row.Balance, reservaPadre.CreatedAt }
                by new { customer.PublicId, customer.FullName, customer.DocumentNumber, row.Currency }
            into grouped
            select new
            {
                grouped.Key.PublicId,
                grouped.Key.FullName,
                grouped.Key.DocumentNumber,
                grouped.Key.Currency,
                Balance = grouped.Sum(x => x.Balance),
                LastMovementDate = grouped.Max(x => x.CreatedAt)
            };

        var raw = await receivablesByCurrency
            .OrderByDescending(x => x.Balance)
            .ToListAsync(cancellationToken);

        return raw
            .Select(x => new
            {
                x.PublicId,
                x.FullName,
                x.DocumentNumber,
                Currency = Monedas.Normalizar(x.Currency),
                CurrentBalance = EconomicRulesHelper.RoundCurrency(x.Balance),
                x.LastMovementDate
            })
            .ToList();
    }

    public async Task<byte[]> ExportReportAsync(DateTime? from, DateTime? to, bool includeSales, bool includeReceivables, bool includePayables, CancellationToken cancellationToken)
    {
        var dateFrom = from?.ToUniversalTime() ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var dateTo = to?.ToUniversalTime() ?? DateTime.UtcNow;

        using var workbook = new XLWorkbook();

        if (includeSales)
        {
            var salesSheet = workbook.Worksheets.Add("Ventas");
            salesSheet.Cell(1, 1).Value = "Reserva";
            salesSheet.Cell(1, 2).Value = "Cliente";
            salesSheet.Cell(1, 3).Value = "Fecha";
            salesSheet.Cell(1, 4).Value = "Estado";
            salesSheet.Cell(1, 5).Value = "Venta";
            salesSheet.Cell(1, 6).Value = "Costo";
            salesSheet.Cell(1, 7).Value = "Margen";

            var files = await _dbContext.Reservas
                .Include(f => f.Payer)
                .Where(f => f.CreatedAt >= dateFrom && f.CreatedAt <= dateTo 
                    && f.Status != EstadoReserva.Budget && f.Status != EstadoReserva.Cancelled)
                .OrderByDescending(f => f.CreatedAt)
                .ToListAsync(cancellationToken);

            int row = 2;
            foreach (var file in files)
            {
                salesSheet.Cell(row, 1).Value = file.NumeroReserva;
                salesSheet.Cell(row, 2).Value = file.Payer?.FullName ?? "Cliente Ocasional";
                salesSheet.Cell(row, 3).Value = file.CreatedAt;
                salesSheet.Cell(row, 4).Value = file.Status.ToString();
                salesSheet.Cell(row, 5).Value = file.TotalSale;
                salesSheet.Cell(row, 6).Value = file.TotalCost;
                salesSheet.Cell(row, 7).Value = file.TotalSale - file.TotalCost;
                row++;
            }
            
            salesSheet.Range(2, 5, row - 1, 7).Style.NumberFormat.Format = "$ #,##0.00";
            salesSheet.Columns().AdjustToContents();
        }

        if (includeReceivables)
        {
            var debtSheet = workbook.Worksheets.Add("Cuentas por Cobrar");
            debtSheet.Cell(1, 1).Value = "Cliente";
            debtSheet.Cell(1, 2).Value = "Documento";
            debtSheet.Cell(1, 3).Value = "Saldo Deudor";
            
            var debtors = await _dbContext.Customers
                .Where(c => c.CurrentBalance > 0)
                .OrderByDescending(c => c.CurrentBalance)
                .ToListAsync(cancellationToken);

            int row = 2;
            foreach (var debtor in debtors)
            {
                debtSheet.Cell(row, 1).Value = debtor.FullName;
                debtSheet.Cell(row, 2).Value = debtor.DocumentNumber;
                debtSheet.Cell(row, 3).Value = debtor.CurrentBalance;
                row++;
            }

            debtSheet.Range(2, 3, row - 1, 3).Style.NumberFormat.Format = "$ #,##0.00";
            debtSheet.Columns().AdjustToContents();
        }

        if (includePayables)
        {
            var payableSheet = workbook.Worksheets.Add("Cuentas por Pagar");
            payableSheet.Cell(1, 1).Value = "Proveedor";
            payableSheet.Cell(1, 2).Value = "Saldo a Favor";

            var creditors = await _dbContext.Suppliers
                .Where(s => s.CurrentBalance > 0)
                .OrderByDescending(s => s.CurrentBalance)
                .ToListAsync(cancellationToken);

            int row = 2;
            foreach (var creditor in creditors)
            {
                payableSheet.Cell(row, 1).Value = creditor.Name;
                payableSheet.Cell(row, 2).Value = creditor.CurrentBalance;
                row++;
            }

            payableSheet.Range(2, 2, row - 1, 2).Style.NumberFormat.Format = "$ #,##0.00";
            payableSheet.Columns().AdjustToContents();
        }

        if (!workbook.Worksheets.Any())
        {
            var sheet = workbook.Worksheets.Add("Info");
            sheet.Cell(1, 1).Value = "No se seleccionaron reportes para exportar.";
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public async Task<AgencySettings?> GetAgencySettingsAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.AgencySettings.OrderBy(s => s.Id).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<AgencySettings> UpdateAgencySettingsAsync(AgencySettings updated, CancellationToken cancellationToken)
    {
        var settings = await _dbContext.AgencySettings.OrderBy(s => s.Id).FirstOrDefaultAsync(cancellationToken);
        if (settings == null)
        {
            _dbContext.AgencySettings.Add(updated);
            settings = updated;
        }
        else
        {
            settings.AgencyName = updated.AgencyName;
            settings.LegalName = updated.LegalName;
            settings.TaxCondition = updated.TaxCondition;
            settings.ActivityStartDate = updated.ActivityStartDate.HasValue 
                ? DateTime.SpecifyKind(updated.ActivityStartDate.Value, DateTimeKind.Utc) 
                : null;
            settings.TaxId = updated.TaxId;
            settings.Address = updated.Address;
            settings.Phone = updated.Phone;
            settings.Email = updated.Email;
            settings.DefaultCommissionPercent = updated.DefaultCommissionPercent;
            settings.Currency = updated.Currency;
            settings.UpdatedAt = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }

    // ===== BI ANALYTICS =====

    public async Task<List<SellerRankingDto>> GetSellerRankingAsync(DateTime? from, DateTime? to, CancellationToken cancellationToken)
    {
        var dateFrom = from?.ToUniversalTime() ?? new DateTime(DateTime.UtcNow.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var dateTo = to?.ToUniversalTime() ?? DateTime.UtcNow;

        // Get file creation events from audit logs to attribute files to sellers
        var fileCreations = await _dbContext.AuditLogs
            .Where(a => a.Action == "Create" && (a.EntityName == "Reserva" || a.EntityName == "TravelFile")
                && a.Timestamp >= dateFrom && a.Timestamp <= dateTo)
            .Select(a => new { a.UserId, a.UserName, FileId = a.EntityId })
            .ToListAsync(cancellationToken);

        if (!fileCreations.Any()) return new List<SellerRankingDto>();

        var fileIds = fileCreations.Select(fc => int.TryParse(fc.FileId, out var id) ? id : 0).Where(id => id > 0).ToList();

        var files = await _dbContext.Reservas
            .Where(f => fileIds.Contains(f.Id) && f.Status != EstadoReserva.Budget && f.Status != EstadoReserva.Cancelled)
            .Select(f => new { f.Id, f.TotalSale, f.TotalCost })
            .ToListAsync(cancellationToken);

        // Get all users to map IDs to Names if AuditLog is missing them
        var users = await _dbContext.Users.ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);

        var ranking = fileCreations
            .GroupBy(fc => fc.UserId)
            .Select(g => {
                var userId = g.Key;
                var userName = g.First().UserName;
                if (string.IsNullOrWhiteSpace(userName) || userName == "System")
                {
                    if (users.TryGetValue(userId, out var fullName)) userName = fullName;
                    else userName = "Sistema / Desconocido";
                }

                var sellerFileIds = g.Select(x => int.TryParse(x.FileId, out var id) ? id : 0).Where(id => id > 0).ToHashSet();
                var sellerFiles = files.Where(f => sellerFileIds.Contains(f.Id)).ToList();
                
                var totalSales = sellerFiles.Sum(f => f.TotalSale);
                var totalCosts = sellerFiles.Sum(f => f.TotalCost);
                var margin = totalSales - totalCosts;
                var marginPercent = totalSales > 0 ? Math.Round((margin / totalSales) * 100, 1) : 0;

                return new SellerRankingDto(
                    userId,
                    userName,
                    sellerFiles.Count,
                    totalSales,
                    totalCosts,
                    margin,
                    marginPercent
                );
            })
            .Where(s => s.ReservasCreated > 0)
            .OrderByDescending(s => s.TotalSales)
            .ToList();

        return ranking;
    }

    public async Task<List<DestinationAnalyticsDto>> GetDestinationAnalyticsAsync(DateTime? from, DateTime? to, CancellationToken cancellationToken)
    {
        var dateFrom = from?.ToUniversalTime() ?? new DateTime(DateTime.UtcNow.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var dateTo = to?.ToUniversalTime() ?? DateTime.UtcNow;

        // Aggregate destinations from Hotels, Packages, and Flights
        var hotelDestinations = await _dbContext.Set<HotelBooking>()
            .Where(h => h.CreatedAt >= dateFrom && h.CreatedAt <= dateTo)
            .Select(h => new { Destination = h.City, h.SalePrice, h.NetCost, Passengers = h.Adults + h.Children })
            .ToListAsync(cancellationToken);

        // ADR-018 (§4-ter, R-D3): los servicios cargados con la ficha "producto-primero" dejan Destination
        // en null. Sin coalescer, el filtro de mas abajo (Where !IsNullOrWhiteSpace) los EXCLUIRIA del
        // ranking y su revenue desapareceria del reporte. Por eso caemos al nombre del producto
        // (PackageName / ProductName) — misma regla que ServiceDisplayName, replicada aca porque la
        // proyeccion corre en SQL y no puede invocar el helper de C#. Decision de negocio: no perder revenue.
        var packageDestinations = await _dbContext.Set<PackageBooking>()
            .Where(p => p.CreatedAt >= dateFrom && p.CreatedAt <= dateTo)
            .Select(p => new { Destination = p.Destination ?? p.PackageName, p.SalePrice, NetCost = p.NetCost, Passengers = p.Adults + p.Children })
            .ToListAsync(cancellationToken);

        var flightDestinations = await _dbContext.Set<FlightSegment>()
            .Where(f => f.CreatedAt >= dateFrom && f.CreatedAt <= dateTo)
            .Select(f => new { Destination = f.DestinationCity ?? f.Destination ?? f.ProductName, f.SalePrice, f.NetCost, Passengers = 1 })
            .ToListAsync(cancellationToken);

        var allBookings = hotelDestinations
            .Concat(packageDestinations)
            .Concat(flightDestinations)
            .Where(b => !string.IsNullOrWhiteSpace(b.Destination))
            .GroupBy(b => b.Destination.Trim().ToUpper())
            .Select(g => new DestinationAnalyticsDto(
                g.Key,
                g.Count(),
                g.Sum(b => b.SalePrice),
                g.Sum(b => b.NetCost),
                g.Sum(b => b.SalePrice) - g.Sum(b => b.NetCost),
                g.Sum(b => b.Passengers)
            ))
            .OrderByDescending(d => d.TotalRevenue)
            .Take(15)
            .ToList();

        return allBookings;
    }

    public async Task<CashFlowProjectionResponse> GetCashFlowProjectionAsync(int days, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow.Date;
        var historicalStart = now.AddDays(-30);

        // Historical cash in (customer payments). ADR-022 (fix #3): solo los que movieron caja (AffectsCash);
        // los Payment puente AffectsCash=false harian dipear el dia en negativo sin que entrara plata real.
        var cashInByDay = await _dbContext.Payments
            .Where(p => p.PaidAt >= historicalStart && p.PaidAt <= now && !p.IsDeleted && p.AffectsCash)
            .GroupBy(p => p.PaidAt.Date)
            .Select(g => new { Date = g.Key, Amount = g.Sum(p => p.Amount) })
            .ToListAsync(cancellationToken);

        // Historical cash out (supplier payments)
        var cashOutByDay = await _dbContext.SupplierPayments
            .Where(p => p.PaidAt >= historicalStart && p.PaidAt <= now)
            .GroupBy(p => p.PaidAt.Date)
            .Select(g => new { Date = g.Key, Amount = g.Sum(p => p.Amount) })
            .ToListAsync(cancellationToken);

        // Build historical daily entries
        var historical = new List<CashFlowDayDto>();
        decimal runningBalance = 0;
        for (var date = historicalStart; date <= now; date = date.AddDays(1))
        {
            var cashIn = cashInByDay.FirstOrDefault(c => c.Date == date)?.Amount ?? 0;
            var cashOut = cashOutByDay.FirstOrDefault(c => c.Date == date)?.Amount ?? 0;
            runningBalance += cashIn - cashOut;
            historical.Add(new CashFlowDayDto(DateTime.SpecifyKind(date, DateTimeKind.Utc), cashIn, cashOut, runningBalance));
        }

        // Projection: use average daily cash in/out from last 30 days
        var avgDailyCashIn = cashInByDay.Any() ? cashInByDay.Sum(c => c.Amount) / 30m : 0m;
        var avgDailyCashOut = cashOutByDay.Any() ? cashOutByDay.Sum(c => c.Amount) / 30m : 0m;

        var projected = new List<CashFlowDayDto>();
        var projectedBalance = runningBalance;
        for (int i = 1; i <= Math.Max(days, 90); i++)
        {
            var date = now.AddDays(i);
            projectedBalance += avgDailyCashIn - avgDailyCashOut;
            projected.Add(new CashFlowDayDto(DateTime.SpecifyKind(date, DateTimeKind.Utc), avgDailyCashIn, avgDailyCashOut, projectedBalance));
        }

        return new CashFlowProjectionResponse(
            Historical: historical,
            Projected: projected,
            CurrentBalance: runningBalance,
            ProjectedBalance30: projected.Count >= 30 ? projected[29].RunningBalance : projectedBalance,
            ProjectedBalance60: projected.Count >= 60 ? projected[59].RunningBalance : projectedBalance,
            ProjectedBalance90: projected.Count >= 90 ? projected[89].RunningBalance : projectedBalance
        );
    }

    public async Task<YearOverYearResponse> GetYearOverYearAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var currentYearStart = new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var previousYearStart = new DateTime(now.Year - 1, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var previousYearEnd = new DateTime(now.Year - 1, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        var currentYearData = await _dbContext.Reservas
            .Where(f => f.CreatedAt >= currentYearStart && f.Status != EstadoReserva.Budget && f.Status != EstadoReserva.Cancelled)
            .GroupBy(f => f.CreatedAt.Month)
            .Select(g => new { Month = g.Key, Sales = g.Sum(f => f.TotalSale), Costs = g.Sum(f => f.TotalCost), Count = g.Count() })
            .ToListAsync(cancellationToken);

        var previousYearData = await _dbContext.Reservas
            .Where(f => f.CreatedAt >= previousYearStart && f.CreatedAt <= previousYearEnd && f.Status != EstadoReserva.Budget && f.Status != EstadoReserva.Cancelled)
            .GroupBy(f => f.CreatedAt.Month)
            .Select(g => new { Month = g.Key, Sales = g.Sum(f => f.TotalSale), Costs = g.Sum(f => f.TotalCost), Count = g.Count() })
            .ToListAsync(cancellationToken);

        var currentYear = Enumerable.Range(1, 12).Select(m => {
            var data = currentYearData.FirstOrDefault(d => d.Month == m);
            var monthName = CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedMonthName(m);
            return new YoyMonthDto(monthName, m, data?.Sales ?? 0, data?.Costs ?? 0, (data?.Sales ?? 0) - (data?.Costs ?? 0), data?.Count ?? 0);
        }).ToList();

        var previousYear = Enumerable.Range(1, 12).Select(m => {
            var data = previousYearData.FirstOrDefault(d => d.Month == m);
            var monthName = CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedMonthName(m);
            return new YoyMonthDto(monthName, m, data?.Sales ?? 0, data?.Costs ?? 0, (data?.Sales ?? 0) - (data?.Costs ?? 0), data?.Count ?? 0);
        }).ToList();

        var currentTotal = currentYear.Sum(m => m.Sales);
        var previousTotal = previousYear.Sum(m => m.Sales);
        var growth = previousTotal > 0 ? Math.Round(((currentTotal - previousTotal) / previousTotal) * 100, 1) : 0;

        return new YearOverYearResponse(currentYear, previousYear, currentTotal, previousTotal, growth);
    }
}
