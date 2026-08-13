using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Ai;
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
    // Opcional, mismo criterio que los demas de arriba (no romper los unit tests que arman este
    // servicio con el ctor corto). Se usa para dejar rastro de datos que el reporte decide IGNORAR
    // — sin log, un cobro descartado seria invisible para quien tenga que explicar un total.
    private readonly ILogger<ReportService>? _logger;
    // Obra "PDF de presupuesto" (2026-08-11/12): almacenamiento del logo de la agencia (MinIO). Opcional,
    // mismo criterio que los demas de arriba (no romper los unit tests que arman este servicio con el
    // ctor corto) — sin este servicio inyectado, UpdateAgencyLogoAsync rechaza con un mensaje claro en
    // vez de explotar con NullReferenceException.
    private readonly IFileStoragePort? _fileStoragePort;
    // Mini-tanda PDF-2a (2026-08-12): el cerebro que redacta el BORRADOR de condiciones. Mismo criterio
    // "opcional" que el resto: sin estos dos servicios inyectados, GenerateBudgetConditionDraftAsync
    // simplemente contesta "IA no disponible" en vez de romper los unit tests viejos que arman
    // ReportService con el ctor corto.
    private readonly IAiAssistantService? _aiAssistantService;
    private readonly IAiConnectionResolver? _aiConnectionResolver;

    public ReportService(
        AppDbContext dbContext,
        IBnaExchangeRateService bnaExchangeRateService,
        IUserPermissionResolver? permissionResolver = null,
        IHttpContextAccessor? httpContextAccessor = null,
        IFinancePositionService? financePositionService = null,
        IExchangeRateResolver? exchangeRateResolver = null,
        ILogger<ReportService>? logger = null,
        IFileStoragePort? fileStoragePort = null,
        IAiAssistantService? aiAssistantService = null,
        IAiConnectionResolver? aiConnectionResolver = null)
    {
        _dbContext = dbContext;
        _bnaExchangeRateService = bnaExchangeRateService;
        _permissionResolver = permissionResolver;
        _httpContextAccessor = httpContextAccessor;
        _financePositionService = financePositionService;
        _exchangeRateResolver = exchangeRateResolver;
        _logger = logger;
        _fileStoragePort = fileStoragePort;
        _aiAssistantService = aiAssistantService;
        _aiConnectionResolver = aiConnectionResolver;
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

            // "Ayuda invisible del tipo de cambio" (spec firmada 2026-08-06, decision P6=A del dueño):
            // mientras el numero que la factura va a usar NO es plata de verdad, esta tarjeta NO SE
            // MUESTRA. Antes se mostraba con un cartel al lado avisando que era de ensayo; el dueño
            // decidio que un numero falso al lado de uno real es peor que no mostrar nada, y que la
            // palabra del aviso es justo una de las que no quiere ver mas en pantalla.
            if (suggestion.LoCompletaElSistema)
            {
                return null;
            }

            return new DolarParaFacturarDto(Value: suggestion.Rate, RateDate: suggestion.RateDate, EsDePrueba: false);
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
    /// <para><b>FIX (bug real reportado en vivo por el dueño, 2026-08-05, "el dato mas nuevo gana")</b>:
    /// el scraper del BNA viene roto desde el 8/7. Antes, en cuanto el fetch en vivo fallaba, esta funcion
    /// caia al snapshot persistido (que puede ser de HACE UN MES) y devolvia ese dato sin mas — la libreta
    /// de <c>ExchangeRateQuotes</c> (que el job de sincronizacion llena todas las horas, via ARCA o alguna
    /// de las cinco APIs de respaldo) nunca se llegaba a mostrar aunque tuviera una fila de HOY, porque el
    /// snapshot viejo "ganaba" solo por no ser null. Ahora, cuando el fetch en vivo NO trajo un dato
    /// realmente fresco (<c>IsStale=true</c>, sea porque vino del catch o del snapshot persistido), se
    /// compara la FECHA del snapshot contra la FECHA de la fila mas reciente de la libreta y gana la mas
    /// nueva de las dos (ver <see cref="PickNewestDollarSource"/>). El dato del dia que trajo el scraper EN
    /// VIVO (<c>IsStale=false</c>) sigue ganando siempre, sin comparar nada: es el dato de hoy, no hay nada
    /// mas nuevo que eso.</para>
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

        // El scraper trajo un dato FRESCO en esta misma corrida (fetch en vivo exitoso, IsStale=false):
        // es el dato de hoy, gana siempre, sin comparar nada contra la libreta.
        if (bnaRate is not null && !bnaRate.IsStale)
        {
            return bnaRate;
        }

        // A partir de aca "bnaRate" (si existe) es VIEJO: un snapshot persistido, que puede tener
        // semanas si el scraper esta roto. Comparamos su fecha contra la fila mas nueva de la libreta
        // y mostramos la que sea mas reciente de las dos (ver el FIX documentado arriba).
        var libretaSuggestion = await TryLoadOfficialFallbackSuggestionAsync("USD", cancellationToken);
        var dollarWinner = PickNewestDollarSource(bnaRate, libretaSuggestion);
        if (dollarWinner is null)
        {
            // Sin ningun dato de dolar (ni snapshot ni libreta): no tiene sentido resolver euro/real
            // solos, la tira ni se dibuja en este caso (P4=C, "otras monedas" cuelga del dolar).
            return null;
        }

        return await AttachFreshAuxiliaryCurrenciesAsync(dollarWinner, bnaRate, cancellationToken);
    }

    /// <summary>
    /// Ampliacion 2026-08-06 ("el euro y el real tampoco tienen que faltar", extiende ADR-011):
    /// completa <c>EuroValue</c>/<c>RealValue</c> del DTO ganador de dolar aplicando, INDEPENDIENTE
    /// para cada moneda, la MISMA regla "el dato mas nuevo gana" que ya usa <see cref="PickNewestDollarSource"/>
    /// para el dolar — comparando el snapshot scrapeado del BNA (que ya trae euro/real de la MISMA
    /// pagina que trae el dolar, ver <see cref="BnaExchangeRateService"/>) contra las filas EUR/BRL
    /// que <c>ExchangeRateSyncJob</c> deja en la libreta.
    ///
    /// <para><b>Regla de frescura elegida (fijada, no solo documentada)</b>: la tira del dashboard
    /// muestra UNA sola fecha para toda la fila (la del dolar, "al DD/MM" — guia UX P6=A). Euro y
    /// real NO tienen su propio campo de fecha en el DTO ni en la pantalla, asi que la UNICA forma
    /// honesta de mostrarlos es exigir que el dato mas fresco de esa moneda sea AL MENOS tan nuevo
    /// como la fecha del dolar que se esta mostrando al lado. Si es MAS VIEJO, se prefiere
    /// OCULTARLO (queda <c>null</c>) antes que mostrar un numero desactualizado sin forma de
    /// avisarlo — el front ya sabe hacer esto SIN cambios: el desplegable "otras monedas" no se
    /// dibuja si no hay valor (P4=C, ya firmado, ver <c>hayOtrasMonedasParaMostrar</c> en
    /// <c>dolarTiraDashboardLogic.js</c>). Es la unica manera de cumplir "jamas un euro de hace un
    /// mes al lado de un dolar de hoy sin que se note": la AUSENCIA es el aviso.</para>
    /// </summary>
    private async Task<BnaUsdSellerRateDto> AttachFreshAuxiliaryCurrenciesAsync(
        BnaUsdSellerRateDto dollarWinner, BnaUsdSellerRateDto? bnaSnapshot, CancellationToken cancellationToken)
    {
        var dollarShownDate = TryParseSnapshotPublishedDate(dollarWinner.PublishedDate);
        var bnaSnapshotDate = TryParseSnapshotPublishedDate(bnaSnapshot?.PublishedDate);

        var euroValue = await ResolveFreshAuxiliaryCurrencyValueAsync(
            "EUR", bnaSnapshot?.EuroValue, bnaSnapshotDate, dollarShownDate, cancellationToken);
        var realValue = await ResolveFreshAuxiliaryCurrencyValueAsync(
            "BRL", bnaSnapshot?.RealValue, bnaSnapshotDate, dollarShownDate, cancellationToken);

        return dollarWinner with { EuroValue = euroValue, RealValue = realValue };
    }

    /// <summary>
    /// Resuelve el valor mas fresco de UNA moneda auxiliar (euro o real), comparando el snapshot del
    /// BNA contra la libreta, y aplicando el gate de frescura contra el dolar mostrado (ver
    /// <see cref="AttachFreshAuxiliaryCurrenciesAsync"/> para el porque de esa regla).
    /// </summary>
    private async Task<decimal?> ResolveFreshAuxiliaryCurrencyValueAsync(
        string currency,
        decimal? bnaSnapshotValue,
        DateOnly? bnaSnapshotDate,
        DateOnly? dollarShownDate,
        CancellationToken cancellationToken)
    {
        // Candidato 1: el mismo snapshot HTML del BNA que ya trajo el dolar (misma pagina, misma
        // corrida de scraping) — valor invalido (0/negativo, ej. columnas que la tabla del BNA nunca
        // completo) o sin fecha parseable no cuentan como candidato.
        (decimal Rate, DateOnly Date)? snapshotCandidate =
            bnaSnapshotValue is > 0m && bnaSnapshotDate is not null
                ? (bnaSnapshotValue.Value, bnaSnapshotDate.Value)
                : null;

        // Candidato 2: la libreta (ExchangeRateQuotes), misma fuente y mismo modo "solo datos reales"
        // (excludePracticeOfficialData: true, dentro de TryLoadOfficialFallbackSuggestionAsync) que ya
        // usa el dolar de esta tarjeta.
        var libretaSuggestion = await TryLoadOfficialFallbackSuggestionAsync(currency, cancellationToken);
        (decimal Rate, DateOnly Date)? libretaCandidate =
            libretaSuggestion is not null ? (libretaSuggestion.Rate, libretaSuggestion.RateDate) : null;

        var winner = PickNewestAuxiliaryCandidate(snapshotCandidate, libretaCandidate);
        if (winner is null)
        {
            return null;
        }

        // Gate de frescura (regla fijada arriba): si el dato mas nuevo de esta moneda es MAS VIEJO
        // que la fecha del dolar mostrado, se oculta en vez de mostrarse desactualizado sin aviso.
        if (dollarShownDate is null || winner.Value.Date < dollarShownDate.Value)
        {
            return null;
        }

        return winner.Value.Rate;
    }

    /// <summary>
    /// Mismo criterio de desempate que <see cref="PickNewestDollarSource"/> (a igualdad de fecha,
    /// gana el snapshot — el orden de siempre), pero operando sobre un par (valor, fecha) generico en
    /// vez de armar un <see cref="BnaUsdSellerRateDto"/> completo: euro y real no necesitan el resto
    /// de los campos del DTO (fuente, hora publicada, etc.), solo el numero y su fecha.
    /// </summary>
    private static (decimal Rate, DateOnly Date)? PickNewestAuxiliaryCandidate(
        (decimal Rate, DateOnly Date)? snapshotCandidate, (decimal Rate, DateOnly Date)? libretaCandidate)
    {
        if (snapshotCandidate is null)
        {
            return libretaCandidate;
        }
        if (libretaCandidate is null)
        {
            return snapshotCandidate;
        }
        return libretaCandidate.Value.Date > snapshotCandidate.Value.Date ? libretaCandidate : snapshotCandidate;
    }

    /// <summary>
    /// TRABAJO 1 (bug real 2026-08-05, "el dato mas nuevo gana"): decide cual de las dos fuentes
    /// mostrar cuando el fetch en vivo del BNA no trajo nada fresco. Compara la FECHA REAL de cada
    /// fuente (nunca el texto tal cual) y gana la mas reciente; a igualdad de fecha, gana
    /// <paramref name="snapshot"/> — el mismo orden que ya tenia la pantalla, para no cambiar nada en
    /// el caso mas comun (las dos fuentes son de hoy).
    /// </summary>
    private static BnaUsdSellerRateDto? PickNewestDollarSource(
        BnaUsdSellerRateDto? snapshot, ExchangeRateSuggestion? libretaSuggestion)
    {
        if (libretaSuggestion is null)
        {
            // Sin dato en la libreta: se muestra el snapshot tal cual (aunque sea viejo, con su
            // "(sin actualizar)" de siempre), o null si tampoco hay snapshot.
            return snapshot;
        }

        if (snapshot is null)
        {
            return BuildDtoFromLibretaSuggestion(libretaSuggestion);
        }

        // El snapshot guarda la fecha como TEXTO scrapeado del sitio del BNA ("D/M/YYYY", sin ceros a
        // la izquierda). Si no se puede interpretar como fecha, no hay forma honesta de decir que es
        // "mas nuevo" que la libreta (que SI tiene una columna DATE confiable): preferimos la libreta.
        var snapshotDate = TryParseSnapshotPublishedDate(snapshot.PublishedDate);
        if (snapshotDate is null)
        {
            return BuildDtoFromLibretaSuggestion(libretaSuggestion);
        }

        return libretaSuggestion.RateDate > snapshotDate.Value
            ? BuildDtoFromLibretaSuggestion(libretaSuggestion)
            : snapshot;
    }

    /// <summary>Mismos formatos que usa <c>BnaExchangeRateService</c> para parsear su propio
    /// <c>PublishedDate</c> (el scraper del sitio del BNA no siempre trae el dia/mes con cero a la
    /// izquierda). Se duplica el arreglo a proposito: es un detalle interno de esa clase (privado), y
    /// cuatro literales no ameritan romper el encapsulamiento por una dependencia compartida.</summary>
    private static readonly string[] SnapshotPublishedDateFormats =
        { "d/M/yyyy", "dd/MM/yyyy", "d/MM/yyyy", "dd/M/yyyy" };

    private static DateOnly? TryParseSnapshotPublishedDate(string? publishedDate)
    {
        if (string.IsNullOrWhiteSpace(publishedDate))
        {
            return null;
        }

        return DateOnly.TryParseExact(
            publishedDate, SnapshotPublishedDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
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
    /// ADR-011 (enmienda 2026-08-05, decision firmada del dueño; generalizada a <paramref name="currency"/>
    /// EUR/BRL en la ampliacion 2026-08-06): fallback al resolver de la libreta de cotizaciones cuando
    /// el BNA no trajo un dato fresco (ni en vivo ni el ultimo snapshot) — para dolar. Para euro/real
    /// (<see cref="ResolveFreshAuxiliaryCurrencyValueAsync"/>) es UNA de las dos fuentes que se
    /// comparan siempre, no solo cuando el scraper fallo (el scraper no tiene forma de avisar "esta
    /// corrida no me llego el euro" por separado del dolar). SOLO lectura local (el resolver nunca le
    /// pega a ARCA en el camino interactivo, ver <see cref="IExchangeRateResolver"/>), asi que no hace
    /// falta otra ventana de timeout.
    ///
    /// <para><b>Pide <c>excludePracticeOfficialData: true</c> a proposito</b>: esta es la tarjeta
    /// "solo datos reales" (tarjeta 1). Un <c>AfipOficial</c> de homologacion (numero de práctica) NO
    /// es una referencia valida para cotizarle al cliente — para eso esta la tarjeta 2
    /// (<see cref="GetDolarParaFacturarAsync"/>), que si lo muestra con su aviso correspondiente. Para
    /// euro/real este flag no cambia nada en la practica (ARCA no cotiza esas monedas, nunca hay una
    /// fila <c>AfipOficial</c> que filtrar), pero se pasa igual para no bifurcar el metodo por moneda.</para>
    ///
    /// <para>Devuelve la sugerencia CRUDA (con <c>RateDate</c> como <see cref="DateOnly"/>) en vez del
    /// DTO ya armado: <see cref="PickNewestDollarSource"/> necesita comparar esa fecha contra la del
    /// snapshot ANTES de decidir cual de las dos mostrar (TRABAJO 1, "el dato mas nuevo gana") — armar
    /// el DTO (que solo tiene la fecha como texto) antes de esa comparacion habria obligado a
    /// re-parsear un texto que nosotros mismos generamos, en vez de comparar la fecha real que ya
    /// teniamos. Si no hay resolver inyectado (unit tests con el ctor corto) o la libreta no tiene
    /// dato REAL para HOY, devuelve null.</para>
    /// </summary>
    private async Task<ExchangeRateSuggestion?> TryLoadOfficialFallbackSuggestionAsync(string currency, CancellationToken cancellationToken)
    {
        if (_exchangeRateResolver is null)
        {
            return null;
        }

        try
        {
            var hoyArgentina = DateOnly.FromDateTime(ArgentinaTime.GetArgentinaToday());
            return await _exchangeRateResolver.GetSuggestionAsync(
                currency, hoyArgentina, cancellationToken, excludePracticeOfficialData: true);
        }
        catch (Exception)
        {
            // Misma regla que el resto de la cadena BNA: la cotizacion es informativa, jamas tumba el dashboard.
            return null;
        }
    }

    /// <summary>
    /// Arma el DTO del widget a partir de una sugerencia de la libreta ya elegida como ganadora por
    /// <see cref="PickNewestDollarSource"/>. Separado de <see cref="TryLoadOfficialFallbackSuggestionAsync"/>
    /// para no armar el DTO (con la fecha ya convertida a texto) hasta DESPUES de decidir si la
    /// libreta gana o pierde contra el snapshot.
    /// </summary>
    private static BnaUsdSellerRateDto BuildDtoFromLibretaSuggestion(ExchangeRateSuggestion suggestion) => new(
        Value: suggestion.Rate,
        // Este "esqueleto" arranca con Euro/Real en null: la sugerencia de dolar que arma esta
        // funcion solo trae USD. Quien llama a esto (GetDashboardBnaRateAsync, via
        // AttachFreshAuxiliaryCurrenciesAsync, ampliacion 2026-08-06) los completa DESPUES con su
        // propia regla de frescura independiente por moneda — no se inventan valores ACA.
        EuroValue: null,
        RealValue: null,
        PublishedDate: suggestion.RateDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
        // Esta fuente es diaria (no trae "hora de publicacion" como el scrape del BNA): vacio
        // en vez de inventar una hora que no existe. El front ya muestra "-" cuando falta.
        PublishedTime: string.Empty,
        Source: "oficial",
        IsStale: suggestion.IsStale,
        FetchedAt: suggestion.FetchedAt);

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

    // ============================================================================================
    // "Facturas en dolares" (spec firmada 2026-08-06, Parte B)
    //
    // Que resuelve, en criollo: cuando vendes en dolares casi siempre COBRAS a un dolar y FACTURAS a
    // otro (el maximo que el comprobante admite ese dia). La diferencia es real y normal, y el contador
    // la necesita ordenada mes por mes. El vendedor no la tiene que ver nunca: por eso esto vive detras
    // del permiso de Reportes y no en el estado de cuenta de la reserva (regla P-16).
    // ============================================================================================

    public async Task<UsdInvoicesReportResponse> GetUsdInvoicesReportAsync(
        DateTime? from, DateTime? to, CancellationToken cancellationToken)
    {
        var rows = await BuildUsdInvoiceRowsAsync(from, to, cancellationToken);
        return new UsdInvoicesReportResponse(rows, BuildUsdInvoicesTotals(rows));
    }

    public async Task<byte[]> ExportUsdInvoicesReportAsync(
        DateTime? from, DateTime? to, CancellationToken cancellationToken)
    {
        var rows = await BuildUsdInvoiceRowsAsync(from, to, cancellationToken);
        var totals = BuildUsdInvoicesTotals(rows);

        // Mismo mecanismo de export que ya usa el resto de Reportes (ClosedXML + un worksheet por
        // reporte): el contador recibe un archivo con la pinta a la que esta acostumbrado.
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Facturas en dólares");

        sheet.Cell(1, 1).Value = "Fecha";
        sheet.Cell(1, 2).Value = "Comprobante";
        sheet.Cell(1, 3).Value = "Reserva";
        sheet.Cell(1, 4).Value = "Cliente";
        sheet.Cell(1, 5).Value = "Moneda";
        sheet.Cell(1, 6).Value = "Monto";
        sheet.Cell(1, 7).Value = "Tipo de cambio";
        sheet.Cell(1, 8).Value = "Pesos de la factura";
        sheet.Cell(1, 9).Value = "Pesos cobrados";
        sheet.Cell(1, 10).Value = "Diferencia";

        int row = 2;
        foreach (var line in rows)
        {
            sheet.Cell(row, 1).Value = line.Fecha;
            sheet.Cell(row, 2).Value = line.Comprobante;
            sheet.Cell(row, 3).Value = line.NumeroReserva ?? string.Empty;
            sheet.Cell(row, 4).Value = line.Cliente;
            sheet.Cell(row, 5).Value = line.Moneda;
            sheet.Cell(row, 6).Value = line.MontoEnMonedaExtranjera;
            sheet.Cell(row, 7).Value = line.TipoCambioFactura;
            sheet.Cell(row, 8).Value = line.PesosDeLaFactura;
            // Celda VACIA cuando no hay dato, no un cero: un cero diria "cobre cero pesos", que es
            // una afirmacion distinta de "todavia no cobre nada" (mismo criterio que el guion de la
            // pantalla).
            if (line.PesosCobrados.HasValue) sheet.Cell(row, 9).Value = line.PesosCobrados.Value;
            if (line.Diferencia.HasValue) sheet.Cell(row, 10).Value = line.Diferencia.Value;
            row++;
        }

        sheet.Cell(row, 8).Value = totals.PesosDeLaFactura;
        if (totals.PesosCobrados.HasValue) sheet.Cell(row, 9).Value = totals.PesosCobrados.Value;
        if (totals.Diferencia.HasValue) sheet.Cell(row, 10).Value = totals.Diferencia.Value;
        sheet.Cell(row, 7).Value = "Total del período";
        sheet.Row(row).Style.Font.Bold = true;

        sheet.Range(2, 8, row, 10).Style.NumberFormat.Format = "$ #,##0.00";
        sheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// El corazon del reporte: trae las facturas de venta en moneda extranjera del periodo y les pega
    /// lo cobrado.
    ///
    /// <para><b>Que entra y que no</b>: solo FACTURAS de venta (las notas de credito y de debito que la
    /// corrigen no van: mezclarlas en la misma tabla haria que los totales dejen de significar "lo que
    /// facture en dolares este mes"), aprobadas por el organismo, y que no hayan sido anuladas — una
    /// factura anulada por nota de credito ya no vale, sumarla infla el total.</para>
    /// </summary>
    private async Task<List<UsdInvoiceRowDto>> BuildUsdInvoiceRowsAsync(
        DateTime? from, DateTime? to, CancellationToken cancellationToken)
    {
        // Mismos defaults de periodo que ExportReportAsync (mes en curso): el front manda siempre las
        // dos fechas, esto es la red de seguridad para una llamada sin parametros.
        var dateFrom = from?.ToUniversalTime() ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var dateTo = to?.ToUniversalTime() ?? DateTime.UtcNow;

        var candidates = await _dbContext.Invoices
            .AsNoTracking()
            .Where(invoice =>
                invoice.MonId != "PES"
                && invoice.Resultado == "A"
                && invoice.AnnulmentStatus != AnnulmentStatus.Succeeded
                // Dia de emision: el que el organismo tiene registrado. Las facturas viejas (anteriores
                // a que se persistiera ese dia) caen a cuando se emitio y, en ultima instancia, a
                // cuando se creo el comprobante.
                && (invoice.CbteFchArgentina ?? invoice.IssuedAt ?? invoice.CreatedAt) >= dateFrom
                && (invoice.CbteFchArgentina ?? invoice.IssuedAt ?? invoice.CreatedAt) <= dateTo)
            .Select(invoice => new UsdInvoiceRawRow(
                invoice.Id,
                invoice.PublicId,
                invoice.TipoComprobante,
                invoice.PuntoDeVenta,
                invoice.NumeroComprobante,
                invoice.MonId,
                invoice.MonCotiz,
                invoice.ImporteTotal,
                invoice.CbteFchArgentina ?? invoice.IssuedAt ?? invoice.CreatedAt,
                invoice.Reserva != null ? invoice.Reserva.NumeroReserva : null,
                invoice.Reserva != null ? invoice.Reserva.PublicId : (Guid?)null,
                invoice.Reserva != null && invoice.Reserva.Payer != null ? invoice.Reserva.Payer.FullName : null))
            .ToListAsync(cancellationToken);

        // El filtro "es una factura de venta" se aplica EN MEMORIA a proposito: la lista ya viene
        // acotada (facturas en moneda extranjera de un periodo) y llamar al helper del Dominio dentro
        // del Where no se puede — el traductor a SQL no sabe convertir una llamada a metodo arbitraria
        // (misma trampa documentada en ExchangeRateResolver). Preferimos reusar la fuente unica del
        // criterio antes que volver a escribir la lista de codigos aca.
        var saleInvoices = candidates
            .Where(row => ComprobanteLabel.IsSaleInvoice(row.TipoComprobante))
            .OrderByDescending(row => row.Fecha)
            .ThenByDescending(row => row.Id)
            .ToList();

        var collectedByInvoice = await BuildCollectedPesosByInvoiceAsync(saleInvoices, cancellationToken);

        return saleInvoices.Select(row => BuildUsdInvoiceRow(row, collectedByInvoice)).ToList();
    }

    /// <summary>
    /// Cuanta plata EN PESOS entro imputada a cada una de estas facturas.
    ///
    /// <para><b>Que cuenta como "imputado a esta factura"</b>: el cobro que el usuario vinculo a ella al
    /// registrarlo (es el unico vinculo cobro-factura que existe en el sistema). Un cobro suelto de la
    /// reserva, sin vincular, NO se reparte por adivinanza entre las facturas: preferimos mostrar un
    /// guion honesto antes que un numero inventado.</para>
    ///
    /// <para><b>Como se valua cada cobro</b>: si entro en pesos, valen los pesos que entraron, tal cual
    /// (es el caso tipico — cobras en pesos a tu dolar y la factura salio a otro; esa es JUSTAMENTE la
    /// diferencia que este reporte muestra). Si entro en la misma moneda de la factura, se valua al tipo
    /// de cambio de la propia factura: recibiste exactamente los dolares que facturaste, asi que no hay
    /// diferencia de cambio que declarar.</para>
    ///
    /// <para><b>Un cobro en una TERCERA moneda no se cuenta</b> (hallazgo 2026-08-06): si la factura es
    /// en dolares y el cliente pago en euros, no tenemos con que valuar esos euros en pesos — el tipo de
    /// cambio de la factura es dolar/peso, no euro/peso. Usarlo igual produciria un numero inventado en
    /// una planilla contable. Se ignora y se loguea; el contador ve un guion o un cobrado parcial, que
    /// es honesto, en vez de un total falso.</para>
    /// </summary>
    private async Task<Dictionary<int, decimal>> BuildCollectedPesosByInvoiceAsync(
        List<UsdInvoiceRawRow> invoices, CancellationToken cancellationToken)
    {
        if (invoices.Count == 0)
        {
            return new Dictionary<int, decimal>();
        }

        var invoiceIds = invoices.Select(invoice => invoice.Id).ToList();

        var payments = await _dbContext.Payments
            .AsNoTracking()
            .Where(payment =>
                payment.LinkedInvoiceId != null
                && invoiceIds.Contains(payment.LinkedInvoiceId.Value)
                && payment.Status == "Paid"
                // Solo plata que efectivamente movio caja. Los movimientos "puente" (por ejemplo, un
                // saldo a favor que se aplica) no son plata que entro por esta factura.
                && payment.AffectsCash)
            .Select(payment => new
            {
                InvoiceId = payment.LinkedInvoiceId!.Value,
                payment.Amount,
                payment.Currency
            })
            .ToListAsync(cancellationToken);

        var invoiceById = invoices.ToDictionary(invoice => invoice.Id);
        var collected = new Dictionary<int, decimal>();

        foreach (var payment in payments)
        {
            var invoice = invoiceById[payment.InvoiceId];

            bool entroEnPesos = string.Equals(payment.Currency, Monedas.ARS, StringComparison.OrdinalIgnoreCase);
            bool entroEnLaMonedaDeLaFactura = string.Equals(
                payment.Currency, ToDisplayCurrency(invoice.MonId), StringComparison.OrdinalIgnoreCase);

            if (!entroEnPesos && !entroEnLaMonedaDeLaFactura)
            {
                _logger?.LogWarning(
                    "Reporte de facturas en moneda extranjera: se ignoro un cobro en {PaymentCurrency} " +
                    "imputado a un comprobante en {InvoiceCurrency}. No hay forma honesta de valuarlo en pesos.",
                    payment.Currency, ToDisplayCurrency(invoice.MonId));
                continue;
            }

            decimal pesosDeEsteCobro = entroEnPesos
                ? payment.Amount
                : payment.Amount * invoice.MonCotiz;

            collected.TryGetValue(payment.InvoiceId, out var acumulado);
            collected[payment.InvoiceId] = acumulado + pesosDeEsteCobro;
        }

        return collected;
    }

    /// <summary>
    /// Codigo de moneda para mostrar ("USD"), a partir del codigo interno del comprobante ("DOL").
    ///
    /// <para><b>Fallback seguro</b> (hallazgo de exposicion 2026-08-06): si el codigo interno no esta en
    /// el catalogo, se muestra "Otra". Antes se mostraba el codigo crudo, y un codigo como "060" en una
    /// planilla que abre el contador no significa nada para nadie fuera del sistema (regla T-5).</para>
    /// </summary>
    private static string ToDisplayCurrency(string arcaCurrencyCode)
        => ArcaCurrencyMapper.ToIso(arcaCurrencyCode) ?? "Otra";

    private static UsdInvoiceRowDto BuildUsdInvoiceRow(
        UsdInvoiceRawRow row, IReadOnlyDictionary<int, decimal> collectedByInvoice)
    {
        var pesosDeLaFactura = EconomicRulesHelper.RoundCurrency(row.ImporteTotal * row.MonCotiz);

        decimal? pesosCobrados = collectedByInvoice.TryGetValue(row.Id, out var cobrado)
            ? EconomicRulesHelper.RoundCurrency(cobrado)
            : null;

        // "Cero o sin cobros = guion" (spec Parte B): una diferencia de cero no es informacion, es
        // ruido. Se muestra solo cuando hay algo que contar.
        decimal? diferencia = null;
        if (pesosCobrados.HasValue)
        {
            var delta = EconomicRulesHelper.RoundCurrency(pesosCobrados.Value - pesosDeLaFactura);
            diferencia = delta == 0m ? null : delta;
        }

        return new UsdInvoiceRowDto(
            Fecha: row.Fecha,
            Comprobante: ComprobanteLabel.Format(row.TipoComprobante, row.PuntoDeVenta, row.NumeroComprobante),
            ComprobanteId: row.PublicId,
            NumeroReserva: row.NumeroReserva,
            ReservaId: row.ReservaPublicId,
            Cliente: string.IsNullOrWhiteSpace(row.ClienteNombre) ? "Cliente ocasional" : row.ClienteNombre!,
            // El codigo interno del comprobante ("DOL") nunca sale a pantalla: se traduce al codigo
            // corto que la gente usa ("USD"), regla T-5. Ver ToDisplayCurrency para el fallback.
            Moneda: ToDisplayCurrency(row.MonId),
            MontoEnMonedaExtranjera: row.ImporteTotal,
            TipoCambioFactura: row.MonCotiz,
            PesosDeLaFactura: pesosDeLaFactura,
            PesosCobrados: pesosCobrados,
            Diferencia: diferencia);
    }

    /// <summary>
    /// Pie de la tabla. La diferencia total es la SUMA de las diferencias de cada fila, no la resta de
    /// los dos totales: las facturas que todavia no cobraron nada no aportan una diferencia (no se
    /// "deben" esos pesos, simplemente no entraron todavia), asi que restar los totales daria un numero
    /// enorme y falso.
    /// </summary>
    private static UsdInvoicesReportTotalsDto BuildUsdInvoicesTotals(List<UsdInvoiceRowDto> rows)
    {
        var totalFacturado = EconomicRulesHelper.RoundCurrency(rows.Sum(row => row.PesosDeLaFactura));

        var filasConCobros = rows.Where(row => row.PesosCobrados.HasValue).ToList();
        decimal? totalCobrado = filasConCobros.Count == 0
            ? null
            : EconomicRulesHelper.RoundCurrency(filasConCobros.Sum(row => row.PesosCobrados!.Value));

        var sumaDiferencias = EconomicRulesHelper.RoundCurrency(
            rows.Where(row => row.Diferencia.HasValue).Sum(row => row.Diferencia!.Value));
        decimal? totalDiferencia = sumaDiferencias == 0m ? null : sumaDiferencias;

        return new UsdInvoicesReportTotalsDto(totalFacturado, totalCobrado, totalDiferencia);
    }

    /// <summary>
    /// Fila cruda tal cual sale de la base, antes de convertirla en la fila que ve el usuario. Existe
    /// para que la proyeccion a SQL sea explicita y para no arrastrar la entidad <c>Invoice</c> entera
    /// (con sus snapshots JSON) por una tabla que solo necesita diez campos.
    /// </summary>
    private sealed record UsdInvoiceRawRow(
        int Id,
        Guid PublicId,
        int TipoComprobante,
        int PuntoDeVenta,
        long NumeroComprobante,
        string MonId,
        decimal MonCotiz,
        decimal ImporteTotal,
        DateTime Fecha,
        string? NumeroReserva,
        Guid? ReservaPublicId,
        string? ClienteNombre);

    public async Task<AgencySettings?> GetAgencySettingsAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.AgencySettings.OrderBy(s => s.Id).FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Fix bloqueante (2026-08-13): trae SOLO el texto de la plantilla, con una proyección
    /// (<c>Select</c> antes del <c>FirstOrDefaultAsync</c>) para no traer del todo la fila de
    /// <see cref="AgencySettings"/> — ni por error se filtra un campo interno que no corresponde acá.
    /// Si la agencia todavía no configuró nada (o no existe fila de settings), devuelve texto null: la
    /// ficha de reserva ya sabe mostrar el textarea vacío en ese caso, no es un error.
    /// </summary>
    public async Task<BudgetPaymentTermsTemplateDto> GetBudgetPaymentTermsTemplateAsync(CancellationToken cancellationToken)
    {
        var text = await _dbContext.AgencySettings
            .OrderBy(s => s.Id)
            .Select(s => s.BudgetPaymentTermsTemplate)
            .FirstOrDefaultAsync(cancellationToken);

        return new BudgetPaymentTermsTemplateDto(text);
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

        // Obra "PDF de presupuesto" (2026-08-11/12): el color de la banda del PDF, mismo criterio
        // "solo si cambia" que el resto de esta pantalla.
        bool pdfBandColorChanged = !string.Equals(settings?.PdfBandColorHex, updated.PdfBandColorHex, StringComparison.Ordinal);
        if (pdfBandColorChanged && !HexColorValidator.IsValidOrEmpty(updated.PdfBandColorHex))
        {
            throw new ValidationException(HexColorValidator.InvalidHexColorMessage);
        }

        // Mejora #3 (review de seguridad, 2026-08-12): la columna es varchar(50) — sin este chequeo, un
        // legajo EVT largo revienta con un error crudo de Npgsql ("value too long for type character
        // varying(50)") en vez de un mensaje criollo. Se valida SIEMPRE (no "solo si cambia"): es una
        // columna nueva, no existe el caso "dato legacy que ya estaba mal" (mismo criterio que
        // CommissionPercentValidator, arriba).
        const int maxAgencyLicenseNumberLength = 50;
        if (updated.AgencyLicenseNumber?.Trim().Length > maxAgencyLicenseNumberLength)
        {
            throw new ValidationException($"El legajo no puede superar los {maxAgencyLicenseNumberLength} caracteres.");
        }

        // Fix post-review (2026-08-12): la plantilla de "Formas de pago" tenia la MISMA columna
        // varchar(4000) que BudgetConditionBlock.Text (mismo limite del textarea de Configuracion), pero
        // esta pantalla no la estaba guardando -> quedaba inerte (el PDF la leia, nadie podia escribirla).
        // Mismo criterio "SIEMPRE, no solo si cambia" que el legajo de arriba: columna nueva, sin datos
        // legacy que puedan estar mal.
        const int maxBudgetPaymentTermsTemplateLength = 4000;
        if (updated.BudgetPaymentTermsTemplate?.Trim().Length > maxBudgetPaymentTermsTemplateLength)
        {
            throw new ValidationException($"El texto de formas de pago no puede superar los {maxBudgetPaymentTermsTemplateLength} caracteres.");
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
            // Obra "PDF de presupuesto" (2026-08-11/12): legajo EVT y color de banda del PDF. El logo
            // NO se toca acá — tiene su propio endpoint (UpdateAgencyLogoAsync) porque es un archivo.
            settings.AgencyLicenseNumber = string.IsNullOrWhiteSpace(updated.AgencyLicenseNumber)
                ? null
                : updated.AgencyLicenseNumber.Trim();
            settings.PdfBandColorHex = string.IsNullOrWhiteSpace(updated.PdfBandColorHex)
                ? null
                : updated.PdfBandColorHex.Trim();
            // Fix post-review (2026-08-12): faltaba esta linea — la columna existia y el PDF la leia, pero
            // el PUT nunca la copiaba al objeto trackeado, asi que quedaba siempre en null sin importar lo
            // que mandara el front.
            settings.BudgetPaymentTermsTemplate = string.IsNullOrWhiteSpace(updated.BudgetPaymentTermsTemplate)
                ? null
                : updated.BudgetPaymentTermsTemplate.Trim();
            settings.UpdatedAt = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }

    // ============================================================================================
    // Obra "PDF de presupuesto" (2026-08-11/12), TANDA 1: logo de la agencia + bloques de condiciones.
    // ============================================================================================

    /// <summary>Extensiones y Content-Type aceptados para el logo (solo el PRIMER filtro, por nombre de archivo declarado).</summary>
    private static readonly Dictionary<string, string[]> AllowedLogoContentTypesByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = new[] { "image/png" },
        [".jpg"] = new[] { "image/jpeg" },
        [".jpeg"] = new[] { "image/jpeg" },
    };

    private const long MaxLogoSizeBytes = 2 * 1024 * 1024; // 2 MB: es un logo, no una foto de alta resolucion.

    /// <summary>
    /// Sube o reemplaza el logo de la agencia. Tres candados independientes, en orden: (1) extensión del
    /// nombre de archivo + Content-Type declarado (<see cref="AllowedLogoContentTypesByExtension"/>) — un
    /// filtro barato pero que el cliente puede mentir; (2) tamaño máximo (<see cref="MaxLogoSizeBytes"/>);
    /// (3) firma REAL del archivo (magic bytes) vía <see cref="ImageFileSignatureValidator.IsPngOrJpg"/> —
    /// mismo criterio de seguridad que <c>AttachmentService.MatchesFileSignature</c> (no comparten código,
    /// AttachmentService cubre más formatos), agregado en la review de seguridad del 2026-08-12 porque el
    /// Content-Type del punto (1) por sí solo NO alcanza (el navegador lo manda, y el cliente puede
    /// mentirlo). Si ya había un logo cargado, el archivo viejo se borra del almacenamiento DESPUÉS de
    /// confirmar que el nuevo se guardó bien (no se pierde el logo anterior si algo falla a mitad de camino).
    /// </summary>
    public async Task<AgencySettings> UpdateAgencyLogoAsync(Stream fileStream, string fileName, string contentType, CancellationToken cancellationToken)
    {
        if (_fileStoragePort is null)
        {
            // No debería pasar en producción (Program.cs siempre registra MinioFileStoragePort); es una
            // guarda defensiva para no explotar con NullReferenceException si algún día cambia el wiring.
            throw new InvalidOperationException("El almacenamiento de archivos no está disponible en este momento.");
        }

        var safeFileName = Path.GetFileName(fileName ?? string.Empty).Trim();
        var extension = Path.GetExtension(safeFileName);
        if (!AllowedLogoContentTypesByExtension.TryGetValue(extension, out var allowedContentTypes))
        {
            throw new ValidationException("El logo tiene que ser una imagen PNG o JPG.");
        }

        await using var buffer = new MemoryStream();
        await fileStream.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length == 0)
        {
            throw new ValidationException("El archivo está vacío.");
        }
        if (buffer.Length > MaxLogoSizeBytes)
        {
            throw new ValidationException("El logo supera el tamaño máximo permitido de 2 MB.");
        }

        var normalizedContentType = (contentType ?? string.Empty).Trim();
        if (!allowedContentTypes.Contains(normalizedContentType, StringComparer.OrdinalIgnoreCase))
        {
            throw new ValidationException("El contenido del archivo no coincide con el tipo declarado.");
        }

        // Mejora #1 (review de seguridad, 2026-08-12): el Content-Type que manda el navegador es un dato
        // que el cliente puede mentir (ej. subir un .exe renombrado a "logo.png" con Content-Type
        // forzado a "image/png"). Miramos los primeros bytes REALES del archivo — mismo criterio que
        // AttachmentService.MatchesFileSignature, acotado a los 2 formatos que acepta el logo.
        if (!ImageFileSignatureValidator.IsPngOrJpg(buffer.ToArray()))
        {
            throw new ValidationException("La firma del archivo no coincide con el tipo permitido.");
        }

        var settings = await _dbContext.AgencySettings.OrderBy(s => s.Id).FirstOrDefaultAsync(cancellationToken)
            ?? new AgencySettings();
        var previousStoredFileName = settings.LogoStoredFileName;

        buffer.Position = 0;
        var objectName = $"agency/logo/{Guid.NewGuid():N}{extension}";
        var stored = await _fileStoragePort.SaveAsync(buffer, objectName, safeFileName, normalizedContentType, cancellationToken);

        settings.LogoStoredFileName = stored.StoredFileName;
        settings.LogoFileName = stored.FileName;
        settings.LogoContentType = stored.ContentType;
        settings.LogoFileSize = stored.FileSize;
        settings.UpdatedAt = DateTime.UtcNow;

        if (settings.Id == 0)
        {
            _dbContext.AgencySettings.Add(settings);
        }
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Borra el archivo VIEJO recien despues de confirmar el nuevo (si algo fallo arriba, el logo
        // anterior sigue intacto en MinIO en vez de perderse a mitad de camino).
        if (!string.IsNullOrWhiteSpace(previousStoredFileName) && previousStoredFileName != settings.LogoStoredFileName)
        {
            await _fileStoragePort.DeleteAsync(previousStoredFileName, cancellationToken);
        }

        return settings;
    }

    public async Task RemoveAgencyLogoAsync(CancellationToken cancellationToken)
    {
        var settings = await _dbContext.AgencySettings.OrderBy(s => s.Id).FirstOrDefaultAsync(cancellationToken);
        if (settings is null || string.IsNullOrWhiteSpace(settings.LogoStoredFileName))
        {
            return; // Idempotente: borrar "nada" no es un error.
        }

        var storedFileName = settings.LogoStoredFileName;
        settings.LogoStoredFileName = null;
        settings.LogoFileName = null;
        settings.LogoContentType = null;
        settings.LogoFileSize = null;
        settings.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (_fileStoragePort is not null)
        {
            await _fileStoragePort.DeleteAsync(storedFileName, cancellationToken);
        }
    }

    public async Task<(byte[] Bytes, string ContentType, string FileName)> GetAgencyLogoAsync(CancellationToken cancellationToken)
    {
        var settings = await _dbContext.AgencySettings.OrderBy(s => s.Id).FirstOrDefaultAsync(cancellationToken);
        if (settings is null || string.IsNullOrWhiteSpace(settings.LogoStoredFileName) || _fileStoragePort is null)
        {
            throw new KeyNotFoundException("La agencia todavía no tiene un logo cargado.");
        }

        return await _fileStoragePort.GetAsync(
            settings.LogoStoredFileName, settings.LogoFileName ?? "logo", settings.LogoContentType ?? "application/octet-stream", cancellationToken);
    }

    public async Task<IReadOnlyList<BudgetConditionBlockDto>> GetBudgetConditionBlocksAsync(CancellationToken cancellationToken)
    {
        var storedBlocks = await _dbContext.Set<BudgetConditionBlock>()
            .AsNoTracking()
            .ToDictionaryAsync(b => b.Kind, cancellationToken);

        // Las 6 categorías SIEMPRE, en el orden fijo de las pestañas — con texto vacío para las que
        // todavía no tienen fila (no se pre-siembran en la migración, ver BudgetConditionBlock).
        return BudgetConditionBlockKindText.All
            .Select(kindText =>
            {
                var kind = BudgetConditionBlockKindText.ParseOrNull(kindText)!.Value;
                storedBlocks.TryGetValue(kind, out var stored);
                return new BudgetConditionBlockDto(kindText, stored?.Text);
            })
            .ToList();
    }

    public async Task<BudgetConditionBlockDto> UpdateBudgetConditionBlockAsync(string kindText, string? text, CancellationToken cancellationToken)
    {
        var kind = BudgetConditionBlockKindText.ParseOrNull(kindText)
            ?? throw new ValidationException("Esa categoría de condiciones no existe.");

        if (text != null && text.Length > 4000)
        {
            throw new ValidationException("El texto de las condiciones es demasiado largo (máximo 4000 caracteres).");
        }

        var block = await _dbContext.Set<BudgetConditionBlock>().FirstOrDefaultAsync(b => b.Kind == kind, cancellationToken);
        var normalizedText = string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        if (block is null)
        {
            block = new BudgetConditionBlock { Kind = kind, Text = normalizedText, UpdatedAt = DateTime.UtcNow };
            _dbContext.Set<BudgetConditionBlock>().Add(block);
        }
        else
        {
            block.Text = normalizedText;
            block.UpdatedAt = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return new BudgetConditionBlockDto(kindText, block.Text);
    }

    /// <summary>Mensaje único que ve el vendedor cuando la IA no puede redactar el borrador — sin jerga técnica (data-exposure).</summary>
    private const string AiDraftUnavailableMessage =
        "La inteligencia artificial no está disponible ahora. Escribí el texto a mano o probá más tarde.";

    /// <summary>
    /// Mini-tanda PDF-2a (2026-08-12): genera el BORRADOR de condiciones con IA para el link "✨
    /// Ayudame a redactarlo" de la spec de UI. Nunca persiste nada — devuelve el texto para que el
    /// dueño lo revise en el textarea y lo confirme (o no) con el PUT de siempre (regla P-21).
    /// </summary>
    public async Task<BudgetConditionDraftDto> GenerateBudgetConditionDraftAsync(
        string kindText, string? currentText, CancellationToken cancellationToken)
    {
        var kind = BudgetConditionBlockKindText.ParseOrNull(kindText)
            ?? throw new ValidationException("Esa categoría de condiciones no existe.");

        // Sin los dos servicios de IA inyectados, o sin conexión utilizable, no tiene sentido armar el
        // prompt: se avisa de una directo. Mismo criterio que ServiceLineInterpreter (IsUsableAsync
        // ANTES de gastar una consulta a la base o una llamada de red).
        if (_aiAssistantService is null || _aiConnectionResolver is null
            || !await _aiConnectionResolver.IsUsableAsync(cancellationToken))
        {
            throw new InvalidOperationException(AiDraftUnavailableMessage);
        }

        var request = BuildBudgetConditionDraftRequest(kind, currentText);
        var result = await _aiAssistantService.CompleteAsync(request, cancellationToken);

        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.Text))
        {
            // El motivo técnico (timeout, config inválida, etc) queda SOLO en el log del servidor — a la
            // pantalla le llega el mismo mensaje en criollo de siempre (P-17 / data-exposure).
            _logger?.LogWarning(
                "Borrador IA de condiciones del presupuesto: no se pudo redactar. Motivo interno: {Reason}",
                result.DegradationReason ?? "sin detalle");
            throw new InvalidOperationException(AiDraftUnavailableMessage);
        }

        return new BudgetConditionDraftDto(result.Text.Trim());
    }

    /// <summary>
    /// Arma el pedido al modelo: redactar (o mejorar) las condiciones de venta de UNA categoría del
    /// presupuesto, en español rioplatense, texto plano y acotado — listo para pegar tal cual en el PDF.
    /// </summary>
    private static AiChatRequest BuildBudgetConditionDraftRequest(BudgetConditionBlockKind kind, string? currentText)
    {
        var categoryLabel = BudgetConditionCategoryLabelForPrompt(kind);

        var systemMessage = AiChatMessage.System(
            "Sos un asistente que redacta, en español rioplatense y en tono profesional simple, las " +
            "condiciones de venta que una agencia de viajes minorista argentina imprime al pie de un " +
            "presupuesto en PDF. Escribís SOLO texto plano: sin títulos, sin markdown, sin asteriscos ni " +
            "numeración con símbolos. El texto tiene que quedar listo para pegar tal cual en el PDF, con " +
            "un largo máximo de 180 palabras.");

        var trimmedCurrentText = string.IsNullOrWhiteSpace(currentText) ? null : currentText.Trim();
        var userMessage = AiChatMessage.User(
            trimmedCurrentText is null
                ? $"Redactá desde cero las condiciones de venta estándar para la categoría \"{categoryLabel}\" de un presupuesto de viajes."
                : $"Mejorá y completá este borrador de condiciones de venta para la categoría \"{categoryLabel}\" de un " +
                  "presupuesto de viajes, conservando lo que la agencia ya decidió (no lo contradigas):\n\n" +
                  trimmedCurrentText);

        // Tope generoso de tokens para 180 palabras en español (el limite REAL de largo lo pide el
        // prompt); temperatura baja porque esto es redaccion de un texto legal-comercial repetible, no
        // contenido creativo.
        var options = new AiProviderOptions { MaxTokens = 400, Temperature = 0.4 };

        return new AiChatRequest(new[] { systemMessage, userMessage }, options);
    }

    /// <summary>Nombre de la categoría en palabras humanas, SOLO para el prompt (no viaja a ninguna pantalla).</summary>
    private static string BudgetConditionCategoryLabelForPrompt(BudgetConditionBlockKind kind) => kind switch
    {
        BudgetConditionBlockKind.Flights => "aéreos",
        BudgetConditionBlockKind.Hotels => "hoteles",
        BudgetConditionBlockKind.Transfers => "traslados",
        BudgetConditionBlockKind.Packages => "paquetes",
        BudgetConditionBlockKind.Assistances => "asistencias al viajero",
        BudgetConditionBlockKind.General => "condiciones generales del presupuesto",
        _ => "condiciones generales del presupuesto",
    };

    /// <summary>
    /// TANDA 4 (2026-08-13): genera el BORRADOR de la plantilla de "Formas de pago" con IA para el link
    /// "✨ Ayudame a redactarlo" de Configuración (Card 3). Mismo mecanismo que
    /// <see cref="GenerateBudgetConditionDraftAsync"/> — nunca persiste nada, el dueño confirma con el
    /// PUT de <see cref="UpdateAgencySettingsAsync"/> de siempre (regla P-21).
    /// </summary>
    public async Task<BudgetConditionDraftDto> GenerateBudgetPaymentTermsTemplateDraftAsync(
        string? currentText, CancellationToken cancellationToken)
    {
        if (_aiAssistantService is null || _aiConnectionResolver is null
            || !await _aiConnectionResolver.IsUsableAsync(cancellationToken))
        {
            throw new InvalidOperationException(AiDraftUnavailableMessage);
        }

        var request = BuildBudgetPaymentTermsTemplateDraftRequest(currentText);
        var result = await _aiAssistantService.CompleteAsync(request, cancellationToken);

        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.Text))
        {
            // El motivo técnico (timeout, config inválida, etc) queda SOLO en el log del servidor — a la
            // pantalla le llega el mismo mensaje en criollo de siempre (P-17 / data-exposure).
            _logger?.LogWarning(
                "Borrador IA de formas de pago del presupuesto: no se pudo redactar. Motivo interno: {Reason}",
                result.DegradationReason ?? "sin detalle");
            throw new InvalidOperationException(AiDraftUnavailableMessage);
        }

        return new BudgetConditionDraftDto(result.Text.Trim());
    }

    /// <summary>
    /// Arma el pedido al modelo: redactar (o mejorar) el texto de "Formas de pago" de una agencia de
    /// viajes minorista (seña, saldo, cuotas, medios de pago aceptados), en español rioplatense, texto
    /// plano y acotado — listo para pegar tal cual en Configuración.
    /// </summary>
    private static AiChatRequest BuildBudgetPaymentTermsTemplateDraftRequest(string? currentText)
    {
        var systemMessage = AiChatMessage.System(
            "Sos un asistente que redacta, en español rioplatense y en tono profesional simple, el texto " +
            "de \"Formas de pago\" que una agencia de viajes minorista argentina carga en su configuración " +
            "y que después se imprime en cada presupuesto en PDF (seña, saldo, cuotas, medios de pago " +
            "aceptados). Escribís SOLO texto plano: sin títulos, sin markdown, sin asteriscos ni " +
            "numeración con símbolos. El texto tiene que quedar listo para pegar tal cual en el PDF, con " +
            "un largo máximo de 120 palabras.");

        var trimmedCurrentText = string.IsNullOrWhiteSpace(currentText) ? null : currentText.Trim();
        var userMessage = AiChatMessage.User(
            trimmedCurrentText is null
                ? "Redactá desde cero un texto estándar de formas de pago para los presupuestos de una " +
                  "agencia de viajes: seña, saldo y medios de pago aceptados."
                : "Mejorá y completá este borrador de formas de pago para los presupuestos de una agencia " +
                  "de viajes, conservando lo que la agencia ya decidió (no lo contradigas):\n\n" +
                  trimmedCurrentText);

        // Tope de tokens acorde a 120 palabras en español; temperatura baja porque esto es redaccion de
        // un texto comercial repetible, no contenido creativo (mismo criterio que el de condiciones).
        var options = new AiProviderOptions { MaxTokens = 300, Temperature = 0.4 };

        return new AiChatRequest(new[] { systemMessage, userMessage }, options);
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
