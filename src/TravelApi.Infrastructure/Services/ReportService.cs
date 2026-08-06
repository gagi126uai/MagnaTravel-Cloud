using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
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
    // ADR-011 (enmienda 2026-08-05, "tipo de cambio real"): opcional para no romper los unit tests
    // que instancian ReportService con el ctor de 2 args (mismo patron que los demas opcionales de
    // arriba). Sin este servicio inyectado, GetDashboardBnaRateAsync simplemente no intenta el
    // fallback "oficial" y el widget se comporta EXACTO como antes de esta obra.
    private readonly IExchangeRateResolver? _exchangeRateResolver;

    public ReportService(
        AppDbContext dbContext,
        IBnaExchangeRateService bnaExchangeRateService,
        IUserPermissionResolver? permissionResolver = null,
        IHttpContextAccessor? httpContextAccessor = null,
        IFinancePositionService? financePositionService = null,
        IExchangeRateResolver? exchangeRateResolver = null)
    {
        _dbContext = dbContext;
        _bnaExchangeRateService = bnaExchangeRateService;
        _permissionResolver = permissionResolver;
        _httpContextAccessor = httpContextAccessor;
        _financePositionService = financePositionService;
        _exchangeRateResolver = exchangeRateResolver;
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

        // Firma post-verificacion Lote 2 (2026-07-27, obra 5 "Ventas personales"): scope UNICO de "mi
        // cartera" para TODO el dashboard de un vendedor sin reservas.view_all. Antes este mismo criterio
        // se recalculaba dos veces mas abajo (una para "Proximos viajes", otra para "Cobros pendientes")
        // y NO se aplicaba a ventas/costos/margen del mes ni a los desgloses por moneda — un vendedor sin
        // permiso de ver toda la agencia igual veia la facturacion TOTAL de la agencia en su dashboard.
        // Se unifica aca arriba para que ventas, costos, margen, cobros y saldo pendiente usen EXACTAMENTE
        // el mismo filtro que ya usaban ReservasPendientes/ProximosViajes. El admin (hasReservasViewAll
        // via rol Admin) sigue viendo todo: ownerFilter queda null y ningun query se acota.
        var ownerFilter = hasReservasViewAll
            ? null
            : (string.IsNullOrEmpty(currentUserId) ? "__no_user__" : currentUserId);

        // Hallazgo de review (2026-07-27, bloqueante backend+security): el criterio firmado es "el
        // vendedor no ve los numeros de toda la agencia" SIN EXCEPCIONES. filesByStatus alimenta los 3
        // contadores del semaforo (Presupuestos/Reservados/Operativos) y DistribucionEstados: con
        // ownerFilter activo, se acota a las reservas del vendedor.
        var filesByStatusQuery = _dbContext.Reservas.AsQueryable();
        if (ownerFilter != null)
        {
            filesByStatusQuery = filesByStatusQuery.Where(f => f.ResponsibleUserId == ownerFilter);
        }
        var filesByStatus = await filesByStatusQuery
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
        //
        // Hallazgo de review (2026-07-27): mismo left join Payments->Reservas que CobrosDelMes por moneda
        // (ver BuildDashboardByCurrencyAsync). Un cobro sin reserva asociada no se le puede atribuir a
        // ningun vendedor puntual: con ownerFilter activo queda AFUERA (criterio conservador, consistente
        // con el resto del dashboard).
        var paymentsThisMonthQuery =
            from p in _dbContext.Payments
            join reservaPadre in _dbContext.Reservas on p.ReservaId equals reservaPadre.Id into reservaJoin
            from reservaPadre in reservaJoin.DefaultIfEmpty()
            where p.PaidAt >= startOfMonth && !p.IsDeleted && p.AffectsCash
                && (ownerFilter == null || (reservaPadre != null && reservaPadre.ResponsibleUserId == ownerFilter))
            select p.Amount;

        var paymentsThisMonth = await paymentsThisMonthQuery
            .SumAsync(amount => (decimal?)amount, cancellationToken) ?? 0m;

        // Hallazgo de review (2026-07-27): "Saldo pendiente" escalar tambien es cartera del vendedor.
        var outstandingBalanceQuery = _dbContext.Reservas
            .Where(f => f.Status != EstadoReserva.Closed && f.Status != EstadoReserva.Cancelled && f.Status != EstadoReserva.Budget);
        if (ownerFilter != null)
        {
            outstandingBalanceQuery = outstandingBalanceQuery.Where(f => f.ResponsibleUserId == ownerFilter);
        }
        var outstandingBalance = await outstandingBalanceQuery
            .SumAsync(f => (decimal?)f.Balance, cancellationToken) ?? 0m;

        // Firma post-verificacion Lote 2 (obra 5): ventas y costos del mes acotados a ownerFilter cuando el
        // vendedor no tiene reservas.view_all (ver el comentario de ownerFilter mas arriba).
        var salesThisMonth = await _dbContext.Reservas
            .Where(f => f.CreatedAt >= startOfMonth && f.Status != EstadoReserva.Budget && f.Status != EstadoReserva.Cancelled
                && (ownerFilter == null || f.ResponsibleUserId == ownerFilter))
            .SumAsync(f => (decimal?)f.TotalSale, cancellationToken) ?? 0m;

        var costsThisMonth = await _dbContext.Reservas
            .Where(f => f.CreatedAt >= startOfMonth && f.Status != EstadoReserva.Budget && f.Status != EstadoReserva.Cancelled
                && (ownerFilter == null || f.ResponsibleUserId == ownerFilter))
            .SumAsync(f => (decimal?)f.TotalCost, cancellationToken) ?? 0m;

        // Firma post-verificacion Lote 2 (2026-07-27, cierre de hueco pedido por el orquestador): un pago
        // a proveedor tambien es parte de "mi cartera" cuando esta atado a UNA reserva del vendedor. Mismo
        // patron de left join que CobrosDelMes (ver BuildDashboardByCurrencyAsync mas abajo): un pago a
        // proveedor SIN reserva asociada (SupplierPayment.ReservaId null) no se le puede atribuir a ningun
        // vendedor puntual, asi que con ownerFilter activo queda AFUERA (mismo criterio conservador).
        var supplierPaymentsThisMonthQuery =
            from p in _dbContext.SupplierPayments
            join reservaPadre in _dbContext.Reservas on p.ReservaId equals reservaPadre.Id into reservaJoin
            from reservaPadre in reservaJoin.DefaultIfEmpty()
            where p.PaidAt >= startOfMonth
                && (ownerFilter == null || (reservaPadre != null && reservaPadre.ResponsibleUserId == ownerFilter))
            select p.Amount;

        var supplierPaymentsThisMonth = await supplierPaymentsThisMonthQuery
            .SumAsync(amount => (decimal?)amount, cancellationToken) ?? 0m;

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
            // ownerFilter ya trae el sentinel "__no_user__" resuelto arriba (una sola vez para todo el
            // metodo, ver el comentario junto a su declaracion).
            upcomingQuery = upcomingQuery.Where(f => f.ResponsibleUserId == ownerFilter);
        }

        // H15 (barrido E2E 2026-07-25): el widget "Cobros Pendientes" mostraba una reserva YA SALDADA
        // (o incluso sobre-cobrada) como si tuviera plata pendiente. Causa: este filtro solo miraba
        // "row.Balance > 0" de la tabla hija por moneda, sin consultar el eje de cobranza YA CALCULADO
        // (Reserva.DerivedCollectionStatus, ADR-048 T5) que el resto del sistema usa como fuente unica
        // para decidir si una reserva "debe" (ver el mismo criterio en el filtro "settled" de
        // GetReservasWithScopeAsync, unas lineas mas abajo en este archivo). Un residuo de centavos por
        // redondeo, o una reserva marcada "Saldado"/"SaldoAFavor" por el motor, igual aparecia en la
        // lista de deudores.
        //
        // Fix: si la reserva YA tiene el eje calculado (no null), confiamos EXCLUSIVAMENTE en el
        // ("ConDeuda" = de verdad pendiente; cualquier otro valor = no pendiente, se excluye). Si el eje
        // TODAVIA es null (reserva vieja sin backfilear), caemos al chequeo crudo de Balance > 0 de
        // antes, para no esconder deuda real de un dato legacy que el sistema no llego a clasificar.
        var pendingByCurrencyQuery =
            from row in _dbContext.ReservaMoneyByCurrency
            join reservaPadre in _dbContext.Reservas on row.ReservaId equals reservaPadre.Id
            where row.Balance > 0
                && reservaPadre.Status != EstadoReserva.Closed
                && reservaPadre.Status != EstadoReserva.Cancelled
                && (reservaPadre.DerivedCollectionStatus == null
                    || reservaPadre.DerivedCollectionStatus == ReservaCollectionStatus.WithDebt)
                && (ownerFilter == null || reservaPadre.ResponsibleUserId == ownerFilter)
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
                    // x.Status YA es string (Reserva.Status): el .ToString() de aca era un no-op que
                    // Npgsql no puede traducir a SQL (mismo landmine que el hotfix del buscador global,
                    // commit 48b15347 — encontrado al tocar esta consulta para H15).
                    x.Status,
                    currency))
                .ToListAsync(cancellationToken);
            pendingReservas.AddRange(topForCurrency);
        }

        var upcomingTrips = await upcomingQuery
            .OrderBy(f => f.StartDate)
            .Take(5)
            // f.Status YA es string: mismo fix de traduccion que arriba (H15).
            .Select(f => new UpcomingTripDto(f.PublicId, f.NumeroReserva, f.Name, f.StartDate!.Value, f.Status))
            .ToListAsync(cancellationToken);

        var sixMonthsAgo = startOfMonth.AddMonths(-5);

        // Hallazgo de review (2026-07-27): la tendencia historica de 6 meses tambien es cartera del
        // vendedor — sin esto, un vendedor sin reservas.view_all veia el grafico de VENTA de toda la
        // agencia (aunque costo/margen ya estuvieran enmascarados por canSeeCost mas abajo).
        var monthlyDataQuery = _dbContext.Reservas
            .Where(f => f.CreatedAt >= sixMonthsAgo && f.Status != EstadoReserva.Budget && f.Status != EstadoReserva.Cancelled);
        if (ownerFilter != null)
        {
            monthlyDataQuery = monthlyDataQuery.Where(f => f.ResponsibleUserId == ownerFilter);
        }
        var monthlyData = await monthlyDataQuery
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

        // Firma de Gaston (adenda 2026-07-27 tarde, docs/ux/guia-ux-gaston.md): "Posibles clientes
        // activos" muestra los de TODA LA AGENCIA, sin importar reservas.view_all. Los leads son
        // COMPARTIDOS (cualquier vendedor puede seguir a cualquier cliente potencial) y un simple
        // CONTEO no expone plata ni facturacion — no aplica el criterio "el vendedor no ve los numeros
        // de toda la agencia" que si rige para ventas/costos/cobros. Se habia acotado por
        // Lead.AssignedToUserId en un hallazgo de review anterior (mismo dia); ESTA firma lo revierte
        // a proposito. NO reabrir sin una firma nueva de Gaston.
        var activePotentialCustomers = await _dbContext.Leads
            .CountAsync(lead => lead.Status != LeadStatus.Won && lead.Status != LeadStatus.Lost, cancellationToken);

        // La cotizacion BNA es INFORMATIVA: nunca debe bloquear el dashboard. GetUsdSellerRateAsync puede
        // disparar un fetch HTTP en vivo a bna.com.ar (timeout interno de 10s) y, si BNA no responde, dejaria la
        // pantalla en skeleton todo ese tiempo. No hay refresher en background, asi que aca acotamos la espera:
        // si la cotizacion no llega en una ventana corta, nos degradamos al ultimo snapshot persistido (lectura
        // local, sin red) y, en ultima instancia, a null. El contrato DashboardResponse.bnaUsdSellerRate admite
        // null y el front ya lo tolera.
        var bnaUsdSellerRate = await GetDashboardBnaRateAsync(cancellationToken);

        // Tarjeta 2 (ADR-011, enmienda 2026-08-05, decision firmada del dueño): "Dólar para facturar
        // (ARCA)" — el MISMO valor que la pantalla de facturar sugeriria ahora mismo. A diferencia de
        // bnaUsdSellerRate, esta NO filtra datos de práctica: en homologacion es correcto mostrar el
        // numero de práctica (con el aviso EsDePrueba prendido), porque es el que ARCA va a exigir en
        // el comprobante.
        var dolarParaFacturar = await GetDolarParaFacturarAsync(cancellationToken);

        // ADR-021 Capa 6: desgloses por moneda (aditivos). Cobros/pagos por moneda REAL del movimiento;
        // ventas/costos por moneda del servicio (tabla hija filtrada por CreatedAt del mes); saldo
        // pendiente y cuentas por pagar por moneda del saldo contra las tablas hijas. CostosDelMes y
        // CuentasPorPagar se enmascaran (lista vacia) si el user no ve costos, igual que los escalares.
        var porMoneda = await BuildDashboardByCurrencyAsync(startOfMonth, canSeeCost, ownerFilter, cancellationToken);

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
            PorMoneda: porMoneda,
            DolarParaFacturar: dolarParaFacturar
        );
    }

    /// <summary>
    /// Tarjeta 2 del dashboard (ADR-011, enmienda 2026-08-05, decision firmada del dueño): pregunta al
    /// resolver EXACTAMENTE lo mismo que <c>GET /api/exchange-rates/suggestion</c> le contestaria a la
    /// pantalla de facturar ahora mismo — mismo <c>excludePracticeOfficialData</c> en <c>false</c>
    /// (default), a proposito: esta tarjeta muestra "lo que la factura va a usar", no "un dato real
    /// garantizado" (para eso esta la tarjeta 1, <see cref="GetDashboardBnaRateAsync"/>).
    ///
    /// <para>Nunca tira: la cotizacion es informativa, un fallo del resolver no puede tumbar el
    /// dashboard. Sin resolver inyectado (unit tests con el ctor corto) o sin sugerencia disponible,
    /// devuelve <c>null</c> y el front muestra el estado vacio de la tarjeta.</para>
    /// </summary>
    private async Task<DolarParaFacturarDto?> GetDolarParaFacturarAsync(CancellationToken cancellationToken)
    {
        if (_exchangeRateResolver is null)
        {
            return null;
        }

        try
        {
            var hoyArgentina = DateOnly.FromDateTime(ArgentinaTime.GetArgentinaToday());
            var suggestion = await _exchangeRateResolver.GetSuggestionAsync("USD", hoyArgentina, cancellationToken);
            if (suggestion is null)
            {
                return null;
            }

            // Mismo criterio que la leyenda de la pantalla de facturar (hallazgo normativo 2026-08-05,
            // validacion ARCA 10240): un AfipOficial que no vino del entorno productivo de ARCA es un
            // numero de práctica, no el dolar real.
            bool esDePrueba = suggestion.Source == ExchangeRateSource.AfipOficial && !suggestion.IsProductionSource;

            return new DolarParaFacturarDto(Value: suggestion.Rate, RateDate: suggestion.RateDate, EsDePrueba: esDePrueba);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Obtiene la cotizacion BNA para el dashboard SIN que la pantalla quede esperando a Banco Nacion.
    ///
    /// <para>Corre el fetch en vivo (que puede ir a la red, timeout interno de 10s) contra una ventana corta
    /// (<see cref="DashboardBnaTimeout"/>). Si gana la ventana, o si el fetch falla, nos degradamos al ultimo
    /// snapshot persistido en DB (lectura local, sin red). NUNCA propaga excepcion: la cotizacion es
    /// informativa y no puede tumbar el dashboard.</para>
    ///
    /// <para>ADR-011 (enmienda 2026-08-05, "tipo de cambio real"): si ni el fetch en vivo ni el snapshot
    /// persistido tienen dato (el scraper del BNA viene fallando hace rato), esta ultima parada NO estaba
    /// antes — el widget quedaba mudo. Ahora, DESPUES de agotar la cadena BNA de siempre, probamos la
    /// libreta de <c>ExchangeRateQuotes</c> (fuente ARCA) para HOY. Si tiene dato, el widget lo muestra
    /// etiquetado con honestidad como "oficial" en vez de mentir que es "BNA billetes".</para>
    ///
    /// <para>El CancellationTokenSource se linkea al token del request para que, si el usuario abandona la
    /// pantalla, tambien se corte el intento en vivo.</para>
    /// </summary>
    private async Task<BnaUsdSellerRateDto?> GetDashboardBnaRateAsync(CancellationToken cancellationToken)
    {
        // Ventana propia para el intento en vivo: si BNA no contesta a tiempo, cancelamos y caemos al snapshot.
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(DashboardBnaTimeout);

        BnaUsdSellerRateDto? bnaRate;
        try
        {
            bnaRate = await _bnaExchangeRateService.GetUsdSellerRateAsync(timeoutSource.Token);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout de la ventana corta (OperationCanceledException por el linked token) o cualquier falla del
            // fetch en vivo. Degradamos al snapshot persistido leyendo con el token ORIGINAL del request (no el
            // ya cancelado). Si el usuario abandono el request, cancellationToken.IsCancellationRequested es true
            // y dejamos que la excepcion de cancelacion del request se propague (no es nuestro caso a degradar).
            bnaRate = await TryLoadPersistedBnaRateAsync(cancellationToken);
        }

        if (bnaRate is not null)
        {
            return bnaRate;
        }

        // Cadena BNA agotada sin dato util: unica parada nueva de esta obra.
        return await TryLoadOfficialFallbackRateAsync(cancellationToken);
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
    /// ADR-011 (enmienda 2026-08-05, decision firmada del dueño): fallback al resolver de la libreta
    /// de cotizaciones cuando el BNA no trajo nada (ni en vivo ni el ultimo snapshot). SOLO lectura
    /// local (el resolver nunca le pega a ARCA en el camino interactivo, ver
    /// <see cref="IExchangeRateResolver"/>), asi que no hace falta otra ventana de timeout — ya
    /// estamos en el camino "degradado" del dashboard.
    ///
    /// <para><b>Pide <c>excludePracticeOfficialData: true</c> a proposito</b>: esta es la tarjeta
    /// "solo datos reales" (tarjeta 1). Un <c>AfipOficial</c> de homologacion (numero de práctica) NO
    /// es una referencia valida para cotizarle al cliente — para eso esta la tarjeta 2
    /// (<see cref="GetDolarParaFacturarAsync"/>), que si lo muestra con su aviso correspondiente.</para>
    ///
    /// <para>Si no hay resolver inyectado (unit tests con el ctor corto) o la libreta no tiene dato
    /// REAL para HOY, devuelve null: el widget queda igual que antes de esta obra (nunca inventa un
    /// numero).</para>
    /// </summary>
    private async Task<BnaUsdSellerRateDto?> TryLoadOfficialFallbackRateAsync(CancellationToken cancellationToken)
    {
        if (_exchangeRateResolver is null)
        {
            return null;
        }

        try
        {
            var hoyArgentina = DateOnly.FromDateTime(ArgentinaTime.GetArgentinaToday());
            var suggestion = await _exchangeRateResolver.GetSuggestionAsync(
                "USD", hoyArgentina, cancellationToken, excludePracticeOfficialData: true);
            if (suggestion is null)
            {
                return null;
            }

            return new BnaUsdSellerRateDto(
                Value: suggestion.Rate,
                // La API publica de respaldo solo trae USD: nunca inventamos euro/real (T-5, "el
                // sistema no inventa datos"). El front oculta esos tiles cuando vienen null.
                EuroValue: null,
                RealValue: null,
                PublishedDate: suggestion.RateDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                // Esta fuente es diaria (no trae "hora de publicacion" como el scrape del BNA): vacio
                // en vez de inventar una hora que no existe. El front ya muestra "-" cuando falta.
                PublishedTime: string.Empty,
                Source: "oficial",
                IsStale: suggestion.IsStale,
                FetchedAt: suggestion.FetchedAt);
        }
        catch (Exception)
        {
            // Misma regla que el resto de la cadena BNA: la cotizacion es informativa, jamas tumba el dashboard.
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
    /// <param name="ownerFilter">
    /// Firma post-verificacion Lote 2 (2026-07-27, obra 5 "Ventas personales"): <c>null</c> = agencia
    /// entera (admin o vendedor con reservas.view_all). Con valor, acota Ventas/Costos/Margen/Cobros/Saldo
    /// pendiente/Pagos a proveedores a las reservas de ESE vendedor (<c>Reserva.ResponsibleUserId</c>).
    ///
    /// Cierre de hueco (2026-07-27, decision del orquestador): Pagos a proveedores TAMBIEN se acota — el
    /// criterio firmado por Gaston es "el vendedor no ve los numeros de toda la agencia", sin excepciones.
    /// Antes de este cierre quedaba deliberadamente afuera (dato de costo, ya enmascarado por
    /// <paramref name="canSeeCost"/>); ahora, ademas del enmascarado por costo, se acota por cartera.
    /// </param>
    private async Task<DashboardByCurrencyDto> BuildDashboardByCurrencyAsync(
        DateTime startOfMonth, bool canSeeCost, string? ownerFilter, CancellationToken cancellationToken)
    {
        // Cobros del mes por moneda REAL del cobro.
        // ADR-022 / FC4 (fix I2, 2026-06-14): solo pagos que MOVIERON caja (AffectsCash). Sin este filtro
        // los Payment "puente" (AffectsCash=false) se cuelan en "Cobros por moneda": el de sobrepago
        // ("SaldoAFavor", negativo) ensucia el total, y el de saldo a favor APLICADO (FC4, positivo) infla
        // el panel con un ingreso de caja que nunca entro. Mismo criterio que CobrosDelMes/TotalRevenue.
        //
        // Firma post-verificacion Lote 2 (obra 5): un cobro sin reserva asociada (Payment.ReservaId null,
        // ej. un ajuste manual de caja) no se le puede atribuir a ningun vendedor puntual. Con ownerFilter
        // activo, esos cobros quedan AFUERA del "Cobros del mes" del vendedor (es dinero que no es de su
        // cartera); el admin (ownerFilter null) los sigue viendo todos, left join preservado con `into`.
        var cobrosQuery =
            from p in _dbContext.Payments
            join reservaPadre in _dbContext.Reservas on p.ReservaId equals reservaPadre.Id into reservaJoin
            from reservaPadre in reservaJoin.DefaultIfEmpty()
            where p.PaidAt >= startOfMonth && !p.IsDeleted && p.AffectsCash
                && (ownerFilter == null || (reservaPadre != null && reservaPadre.ResponsibleUserId == ownerFilter))
            select new { p.Currency, p.Amount };

        var cobros = await SumByCurrencyAsync(
            cobrosQuery
                .GroupBy(x => x.Currency)
                .Select(g => new CurrencyAmount(g.Key, g.Sum(x => x.Amount))),
            cancellationToken);

        // Pagos a proveedor del mes por moneda REAL del egreso.
        //
        // Cierre de hueco (2026-07-27, decision del orquestador): mismo patron de left join que cobros
        // (arriba). Un pago a proveedor SIN reserva asociada (SupplierPayment.ReservaId null) no se le
        // puede atribuir a ningun vendedor puntual: con ownerFilter activo queda AFUERA (mismo criterio
        // conservador que ya se aplico a CobrosDelMes). El admin (ownerFilter null) sigue viendo todo.
        var pagosProveedoresQuery =
            from p in _dbContext.SupplierPayments
            join reservaPadre in _dbContext.Reservas on p.ReservaId equals reservaPadre.Id into reservaJoin
            from reservaPadre in reservaJoin.DefaultIfEmpty()
            where p.PaidAt >= startOfMonth
                && (ownerFilter == null || (reservaPadre != null && reservaPadre.ResponsibleUserId == ownerFilter))
            select new { p.Currency, p.Amount };

        var pagosProveedores = await SumByCurrencyAsync(
            pagosProveedoresQuery
                .GroupBy(x => x.Currency)
                .Select(g => new CurrencyAmount(g.Key, g.Sum(x => x.Amount))),
            cancellationToken);

        // Ventas/costos del mes por moneda del servicio (tabla hija), filtrando reservas creadas en el mes
        // y excluyendo Budget/Cancelled (mismo filtro que el escalar VentasDelMes/CostosDelMes). Join
        // explicito contra Reservas (no nav implicita) para correr igual en Postgres e InMemory.
        // Firma post-verificacion Lote 2 (obra 5): ownerFilter acota a la cartera del vendedor.
        var monthQuery =
            from row in _dbContext.ReservaMoneyByCurrency
            join reservaPadre in _dbContext.Reservas on row.ReservaId equals reservaPadre.Id
            where reservaPadre.CreatedAt >= startOfMonth
                && reservaPadre.Status != EstadoReserva.Budget
                && reservaPadre.Status != EstadoReserva.Cancelled
                && (ownerFilter == null || reservaPadre.ResponsibleUserId == ownerFilter)
            select new { row.Currency, row.TotalSale, row.TotalCost };

        var ventas = await SumByCurrencyAsync(
            monthQuery.GroupBy(x => x.Currency).Select(g => new CurrencyAmount(g.Key, g.Sum(x => x.TotalSale))),
            cancellationToken);

        var costos = canSeeCost
            ? await SumByCurrencyAsync(
                monthQuery.GroupBy(x => x.Currency).Select(g => new CurrencyAmount(g.Key, g.Sum(x => x.TotalCost))),
                cancellationToken)
            : new List<CurrencyAmount>();

        // Margen bruto del mes por moneda: ventas menos costos, moneda por moneda. Si no ve costos, no
        // ve margen tampoco (mismo enmascarado que CostosDelMes de arriba).
        var margenBruto = canSeeCost
            ? ComputeMargenBrutoByCurrency(ventas, costos)
            : new List<CurrencyAmount>();

        // ADR-022 §4.7 (T4): cuentas por cobrar (AR) y por pagar (AP) por moneda salen ahora de la FUENTE
        // UNICA compartida con tesoreria, para que dashboard y tesoreria den EXACTAMENTE el mismo numero.
        // Si no se inyecto el servicio (unit tests con ctor corto), se construye sobre el mismo DbContext.
        var financePosition = _financePositionService ?? new FinancePositionService(_dbContext);

        // AR (cuentas por cobrar): plata de venta -> NO se enmascara. Firma post-verificacion Lote 2:
        // ownerFilter viaja al servicio compartido para que el "Saldo pendiente" del vendedor sea SU
        // cartera (Tesoreria sigue llamando sin este parametro, ve la agencia entera).
        var saldoPendiente = (await financePosition.GetAccountsReceivableByCurrencyAsync(cancellationToken, ownerFilter))
            .Select(x => new CurrencyAmount(x.Currency, x.Amount))
            .ToList();

        // AP (cuentas por pagar): dato de costo -> se enmascara si no ve costos.
        //
        // Hallazgo de review (2026-07-27): ademas del enmascarado por costo, con ownerFilter activo esta
        // lista queda VACIA sin importar canSeeCost. La deuda con proveedores (SupplierBalanceByCurrency)
        // es un pasivo de la AGENCIA con el operador, no de una reserva puntual: a diferencia de
        // CuentasPorCobrar (que sale de ReservaMoneyByCurrency, atado 1:1 a una reserva y por lo tanto a
        // un vendedor), la deuda a proveedor no tiene forma de atribuirse a la cartera de un vendedor
        // especifico. Mostrarsela igual (aunque tenga cobranzas.see_cost) violaria el criterio firmado
        // "el vendedor no ve los numeros de toda la agencia".
        var cuentasPorPagar = (canSeeCost && ownerFilter == null)
            ? (await financePosition.GetAccountsPayableByCurrencyAsync(cancellationToken))
                .Select(x => new CurrencyAmount(x.Currency, x.Amount))
                .ToList()
            : new List<CurrencyAmount>();

        return new DashboardByCurrencyDto(
            CobrosDelMes: cobros,
            PagosProveedores: canSeeCost ? pagosProveedores : new List<CurrencyAmount>(),
            VentasDelMes: ventas,
            CostosDelMes: costos,
            MargenBruto: margenBruto,
            SaldoPendiente: saldoPendiente,
            CuentasPorPagar: cuentasPorPagar);
    }

    /// <summary>
    /// Calcula el margen bruto (venta menos costo) POR MONEDA, uniendo las monedas presentes en
    /// cualquiera de las dos listas. Si una moneda solo aparece en costos (por ejemplo un servicio
    /// cotizado en USD que todavia no se vendio en esa moneda), el margen da negativo A PROPOSITO: mejor
    /// mostrar la perdida en el dashboard que esconderla asumiendo venta cero.
    /// </summary>
    private static List<CurrencyAmount> ComputeMargenBrutoByCurrency(
        List<CurrencyAmount> ventasPorMoneda, List<CurrencyAmount> costosPorMoneda)
    {
        var ventaPorMoneda = ventasPorMoneda.ToDictionary(x => x.Currency, x => x.Amount, StringComparer.Ordinal);
        var costoPorMoneda = costosPorMoneda.ToDictionary(x => x.Currency, x => x.Amount, StringComparer.Ordinal);

        var monedasPresentes = ventaPorMoneda.Keys
            .Union(costoPorMoneda.Keys, StringComparer.Ordinal)
            .OrderBy(currency => currency, StringComparer.Ordinal);

        var margenPorMoneda = new List<CurrencyAmount>();
        foreach (var currency in monedasPresentes)
        {
            var venta = ventaPorMoneda.TryGetValue(currency, out var ventaAmount) ? ventaAmount : 0m;
            var costo = costoPorMoneda.TryGetValue(currency, out var costoAmount) ? costoAmount : 0m;
            margenPorMoneda.Add(new CurrencyAmount(currency, EconomicRulesHelper.RoundCurrency(venta - costo)));
        }

        return margenPorMoneda;
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
        // ADR-022 / FC4 (fix I2, 2026-06-14): igual que el dashboard, solo pagos que movieron caja
        // (AffectsCash). Excluye los puentes de sobrepago (negativo) y de saldo a favor aplicado (positivo),
        // que existen para imputar deuda, no para mover plata. Coherente con el resto de ReportService.
        var cobros = await SumByCurrencyAsync(
            _dbContext.Payments
                .Where(p => p.PaidAt >= dateFrom && p.PaidAt <= dateTo && !p.IsDeleted && p.AffectsCash)
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

        // Margen bruto del periodo por moneda: mismo criterio que el dashboard (ver ComputeMargenBrutoByCurrency).
        var margenBruto = canSeeCost
            ? ComputeMargenBrutoByCurrency(ventas, costos)
            : new List<CurrencyAmount>();

        // Saldo pendiente (cuentas por cobrar) por moneda: no es un dato del periodo sino el saldo vigente.
        // ADR-023 T1.3: usa la MISMA lista canonica de estados en firme que el AR de tesoreria y la cuenta del
        // cliente (antes excluia Closed/Cancelled/Budget pero contaba Quotation/Lost/PendingOperatorRefund, que
        // no son deuda exigible). Asi el saldo pendiente del dashboard cierra con el resto de las pantallas.
        var saldoPendienteQuery =
            from row in _dbContext.ReservaMoneyByCurrency
            join reservaPadre in _dbContext.Reservas on row.ReservaId equals reservaPadre.Id
            where FinancePositionService.ReceivableDebtStatuses.Contains(reservaPadre.Status)
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
            MargenBruto: margenBruto,
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
        // ADR-023 T1.3: la deuda exigible son SOLO las reservas en firme (InManagement/Confirmed/Closed;
        // ADR-036 quito Traveling y ToSettle), misma lista canonica (FinancePositionService.ReceivableDebtStatuses)
        // que el AR de tesoreria y la cuenta del cliente. Antes esta query
        // excluia Closed/Cancelled/Budget pero seguia contando Quotation/Lost/PendingOperatorRefund (que no son
        // deuda). Por eso el total por cliente puede no coincidir con el escalar legacy Customer.CurrentBalance,
        // que sumaba todo sin filtrar estado.
        var receivablesByCurrency =
            from row in _dbContext.ReservaMoneyByCurrency
            join reservaPadre in _dbContext.Reservas on row.ReservaId equals reservaPadre.Id
            join customer in _dbContext.Customers on reservaPadre.PayerId equals customer.Id
            where row.Balance > 0
                && customer.IsActive
                && FinancePositionService.ReceivableDebtStatuses.Contains(reservaPadre.Status)
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

    /// <summary>
    /// ADR-023 T1.3: fila tipada de cuenta por cobrar por cliente + MONEDA, para el Excel. El dashboard
    /// (GetDetailedReceivablesAsync) usa objetos anonimos por contrato historico; el Excel necesita un tipo con
    /// nombre para iterar, asi que comparte la MISMA fuente canonica via este metodo.
    /// </summary>
    private sealed record ReceivableRow(string FullName, string? DocumentNumber, string Currency, decimal Balance);

    private async Task<List<ReceivableRow>> GetReceivablesByCustomerCurrencyAsync(CancellationToken cancellationToken)
    {
        // Misma fuente canonica que GetDetailedReceivablesAsync: ReservaMoneyByCurrency de reservas en firme,
        // por cliente + moneda. Reemplaza la lectura del zombie Customer.CurrentBalance.
        var query =
            from row in _dbContext.ReservaMoneyByCurrency
            join reservaPadre in _dbContext.Reservas on row.ReservaId equals reservaPadre.Id
            join customer in _dbContext.Customers on reservaPadre.PayerId equals customer.Id
            where row.Balance > 0
                && customer.IsActive
                && FinancePositionService.ReceivableDebtStatuses.Contains(reservaPadre.Status)
            group row.Balance by new { customer.FullName, customer.DocumentNumber, row.Currency }
            into grouped
            select new
            {
                grouped.Key.FullName,
                grouped.Key.DocumentNumber,
                grouped.Key.Currency,
                Balance = grouped.Sum()
            };

        var raw = await query
            .OrderByDescending(x => x.Balance)
            .ToListAsync(cancellationToken);

        return raw
            .Select(x => new ReceivableRow(
                x.FullName,
                x.DocumentNumber,
                Monedas.Normalizar(x.Currency),
                EconomicRulesHelper.RoundCurrency(x.Balance)))
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
            // ADR-023 T1.3: columna Moneda nueva. El saldo deja de ser un escalar mezclado (ARS+USD) y se
            // reporta una fila por cliente + moneda (no se puede sumar ARS con USD; consistente con ADR-021).
            debtSheet.Cell(1, 3).Value = "Moneda";
            debtSheet.Cell(1, 4).Value = "Saldo Deudor";

            // ADR-023 T1.3: el Excel deja de leer el zombie Customer.CurrentBalance (que daba el reporte vacio).
            // Sale de la MISMA fuente canonica que el dashboard: ReservaMoneyByCurrency en firme, por moneda.
            var debtors = await GetReceivablesByCustomerCurrencyAsync(cancellationToken);

            int row = 2;
            foreach (var debtor in debtors)
            {
                debtSheet.Cell(row, 1).Value = debtor.FullName;
                debtSheet.Cell(row, 2).Value = debtor.DocumentNumber;
                debtSheet.Cell(row, 3).Value = debtor.Currency;
                debtSheet.Cell(row, 4).Value = debtor.Balance;
                row++;
            }

            debtSheet.Range(2, 4, row - 1, 4).Style.NumberFormat.Format = "$ #,##0.00";
            debtSheet.Columns().AdjustToContents();
        }

        if (includePayables)
        {
            var payableSheet = workbook.Worksheets.Add("Cuentas por Pagar");
            payableSheet.Cell(1, 1).Value = "Proveedor";
            payableSheet.Cell(1, 2).Value = "Moneda";
            payableSheet.Cell(1, 3).Value = "Saldo pendiente";

            var creditors = await (
                from balance in _dbContext.SupplierBalanceByCurrency
                join supplier in _dbContext.Suppliers on balance.SupplierId equals supplier.Id
                where balance.Balance > 0
                orderby balance.Balance descending
                select new { supplier.Name, balance.Currency, balance.Balance })
                .ToListAsync(cancellationToken);

            int row = 2;
            foreach (var creditor in creditors)
            {
                payableSheet.Cell(row, 1).Value = creditor.Name;
                payableSheet.Cell(row, 2).Value = Monedas.Normalizar(creditor.Currency);
                payableSheet.Cell(row, 3).Value = creditor.Balance;
                row++;
            }

            payableSheet.Range(2, 3, row - 1, 3).Style.NumberFormat.Format = "#,##0.00";
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

        // Hallazgo H2 (barrido E2E 2026-07-25), extension a los datos de la agencia: este CUIT es el que se
        // imprime en los papeles que ve el cliente (recibos, vouchers, seccion de datos bancarios). Mal
        // tipeado, se entrega documentacion con un CUIT que no existe. Mismo validador y mismo mensaje que
        // el alta de cliente y de operador; vacio sigue pasando (la agencia puede no haberlo cargado aun).
        //
        // Solo se valida si el numero CAMBIA (mismo criterio que la edicion de cliente/operador): editar el
        // telefono de la agencia no tiene por que quedar trabado por un CUIT cargado mal antes de este fix.
        var storedTaxId = settings?.TaxId;
        bool taxIdChanged = !string.Equals(storedTaxId, updated.TaxId, StringComparison.Ordinal);
        if (taxIdChanged && !CuitValidator.IsValidOrEmpty(updated.TaxId))
        {
            // ValidationException (DataAnnotations) a proposito: el endpoint de configuracion de la agencia
            // no tiene try/catch propio, y el GlobalExceptionHandler es el que traduce ESTE tipo a un 400 con
            // el mensaje real. Con cualquier otro tipo el usuario veria el 500 generico.
            throw new ValidationException(CuitValidator.InvalidCuitMessage);
        }

        // Obra "cada campo acepta solo lo que va en ese campo" (2026-07-31, TANDA 1). Estos datos se
        // imprimen en los papeles que ve el cliente (recibos, vouchers) y la condicion fiscal de la
        // agencia decide la letra de los comprobantes. Mismo tipo de excepcion y mismo criterio "solo si
        // cambia" que el CUIT de arriba: una configuracion vieja con un mail mal escrito no traba al admin
        // que solo viene a cambiar la direccion.
        bool emailChanged = !string.Equals(settings?.Email, updated.Email, StringComparison.Ordinal);
        if (emailChanged && !EmailValidator.IsValidOrEmpty(updated.Email))
        {
            throw new ValidationException(EmailValidator.InvalidEmailMessage);
        }

        bool phoneChanged = !string.Equals(settings?.Phone, updated.Phone, StringComparison.Ordinal);
        if (phoneChanged && !PhoneValidator.IsValidOrEmpty(updated.Phone))
        {
            throw new ValidationException(PhoneValidator.InvalidPhoneMessage);
        }

        bool taxConditionChanged = !string.Equals(settings?.TaxCondition, updated.TaxCondition, StringComparison.Ordinal);
        if (taxConditionChanged && !TaxConditionValidator.IsKnownTextOrEmpty(updated.TaxCondition))
        {
            throw new ValidationException(TaxConditionValidator.InvalidTaxConditionMessage);
        }

        // El % de comision por defecto SIEMPRE se valida (no "solo si cambia"): es plata y el campo llega
        // como numero, asi que no existe el caso "dato legacy raro que no puedo corregir" — cualquier
        // valor guardado hoy ya esta dentro del rango o es un error que conviene frenar ahora.
        if (!CommissionPercentValidator.IsValid(updated.DefaultCommissionPercent))
        {
            throw new ValidationException(CommissionPercentValidator.InvalidPercentMessage);
        }

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

    // Bucket para las reservas que no tienen vendedor responsable asignado. NO inventamos dueño:
    // se agrupan aparte para que el ranking sea honesto (auditoria de negocio 2026-06-12, item 7).
    private const string UnassignedSellerUserId = "";
    private const string UnassignedSellerName = "Sin asignar";

    /// <summary>
    /// Ranking de vendedores HONESTO (auditoria de negocio 2026-06-12, item 7). Cambios respecto del
    /// comportamiento anterior:
    /// <list type="bullet">
    /// <item>Atribuye la venta al <b>vendedor responsable</b> de la reserva (<c>ResponsibleUserId</c>),
    ///   no a quien la creo segun los AuditLogs (que podia ser otra persona o "System").</item>
    /// <item>Mide <b>venta CONFIRMADA</b> (<c>ConfirmedSale</c> = servicios resueltos), no el presupuesto
    ///   (<c>TotalSale</c>, que incluye servicios todavia sin confirmar). Una reserva sin servicios
    ///   confirmados aporta 0.</item>
    /// <item>Excluye <c>PendingOperatorRefund</c> ademas de <c>Budget</c> y <c>Cancelled</c>: una reserva
    ///   en pleno circuito de devolucion del operador no es una venta a acreditarle al vendedor.</item>
    /// <item>Las reservas sin responsable caen en el bucket "Sin asignar" en vez de inventar un dueño.</item>
    /// </list>
    /// La forma del DTO (<see cref="SellerRankingDto"/>) NO cambia: es contenido del reporte, no layout.
    /// </summary>
    public async Task<List<SellerRankingDto>> GetSellerRankingAsync(DateTime? from, DateTime? to, CancellationToken cancellationToken)
    {
        var dateFrom = from?.ToUniversalTime() ?? new DateTime(DateTime.UtcNow.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var dateTo = to?.ToUniversalTime() ?? DateTime.UtcNow;

        // Universo: reservas creadas en el periodo que representan una venta real. Se descartan los
        // estados que NO son venta atribuible (presupuesto, cancelada, y la que esta esperando
        // devolucion del operador). El costo asociado a la venta confirmada lo tomamos de TotalCost
        // (el costo del file); ConfirmedSale es la venta exigible.
        var files = await _dbContext.Reservas
            .Where(f => f.CreatedAt >= dateFrom && f.CreatedAt <= dateTo
                        && f.Status != EstadoReserva.Budget
                        && f.Status != EstadoReserva.Cancelled
                        && f.Status != EstadoReserva.PendingOperatorRefund)
            .Select(f => new
            {
                f.ResponsibleUserId,
                f.ResponsibleUserName,
                f.ConfirmedSale,
                f.TotalCost
            })
            .ToListAsync(cancellationToken);

        if (files.Count == 0) return new List<SellerRankingDto>();

        // Para completar el nombre cuando la reserva no lo tiene cacheado (ResponsibleUserName null).
        var users = await _dbContext.Users.ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);

        var ranking = files
            // Las reservas sin responsable se agrupan todas juntas bajo la misma clave vacia.
            .GroupBy(f => f.ResponsibleUserId ?? UnassignedSellerUserId)
            .Select(group =>
            {
                var userId = group.Key;
                var sellerName = ResolveSellerName(userId, group.Select(g => g.ResponsibleUserName), users);

                var confirmedSales = group.Sum(f => f.ConfirmedSale);
                var totalCosts = group.Sum(f => f.TotalCost);
                var margin = confirmedSales - totalCosts;
                var marginPercent = confirmedSales > 0 ? Math.Round((margin / confirmedSales) * 100, 1) : 0;

                return new SellerRankingDto(
                    userId,
                    sellerName,
                    group.Count(),
                    confirmedSales,
                    totalCosts,
                    margin,
                    marginPercent);
            })
            .OrderByDescending(s => s.TotalSales)
            .ToList();

        return ranking;
    }

    /// <summary>
    /// Resuelve el nombre a mostrar de un vendedor. Prioridad: nombre cacheado en la reserva -> nombre
    /// del usuario en Identity -> "Sin asignar" (bucket sin responsable) -> fallback generico.
    /// </summary>
    private static string ResolveSellerName(string userId, IEnumerable<string?> cachedNames, IDictionary<string, string> users)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return UnassignedSellerName;
        }

        var cached = cachedNames.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return cached!;
        }

        if (users.TryGetValue(userId, out var fullName) && !string.IsNullOrWhiteSpace(fullName))
        {
            return fullName;
        }

        return "Vendedor desconocido";
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
