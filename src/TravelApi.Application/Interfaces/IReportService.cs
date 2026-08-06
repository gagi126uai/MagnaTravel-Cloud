using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

public interface IReportService
{
    Task<DashboardResponse> GetDashboardAsync(CancellationToken cancellationToken);
    Task<ReportsSummaryResponse> GetSummaryAsync(CancellationToken cancellationToken);
    Task<object> GetDetailedReportAsync(DateTime? from, DateTime? to, CancellationToken cancellationToken);
    Task<IEnumerable<object>> GetDetailedReceivablesAsync(CancellationToken cancellationToken);
    Task<byte[]> ExportReportAsync(DateTime? from, DateTime? to, bool includeSales, bool includeReceivables, bool includePayables, CancellationToken cancellationToken);
    Task<AgencySettings?> GetAgencySettingsAsync(CancellationToken cancellationToken);
    Task<AgencySettings> UpdateAgencySettingsAsync(AgencySettings updated, CancellationToken cancellationToken);
    
    // BI Analytics
    Task<List<SellerRankingDto>> GetSellerRankingAsync(DateTime? from, DateTime? to, CancellationToken cancellationToken);
    Task<List<DestinationAnalyticsDto>> GetDestinationAnalyticsAsync(DateTime? from, DateTime? to, CancellationToken cancellationToken);
    Task<CashFlowProjectionResponse> GetCashFlowProjectionAsync(int days, CancellationToken cancellationToken);
    Task<YearOverYearResponse> GetYearOverYearAsync(CancellationToken cancellationToken);
}

// DTOs
public record DashboardResponse(
    int Presupuestos,
    int Reservados,
    int Operativos,
    decimal CobrosDelMes,
    decimal SaldoPendiente,
    decimal VentasDelMes,
    decimal CostosDelMes,
    decimal MargenBruto,
    decimal PagosProveedores,
    List<PendingReservaDto> ReservasPendientes,
    List<UpcomingTripDto> ProximosViajes,
    List<MonthlyMetricDto> TendenciaHistorica,
    StatusDistributionDto DistribucionEstados,
    BnaUsdSellerRateDto? BnaUsdSellerRate,
    int ActivePotentialCustomers,
    // ADR-021 Capa 6 (aditivos): los escalares de arriba quedan para compat (hoy, todo ARS, coinciden con
    // el unico item ARS de cada lista). Estos desgloses NUNCA mezclan monedas en un solo total.
    DashboardByCurrencyDto? PorMoneda = null,
    // ADR-011 (enmienda 2026-08-05, decision firmada del dueño): segunda tarjeta del dashboard, "Dólar
    // para facturar (ARCA)" — el MISMO valor que la pantalla de facturar va a sugerir ahora mismo (no
    // "solo datos reales" como BnaUsdSellerRate: en homologacion es correcto mostrar el numero de
    // practica, con el aviso EsDePrueba prendido).
    DolarParaFacturarDto? DolarParaFacturar = null);

/// <summary>
/// ADR-021 Capa 6: desgloses del dashboard SEPARADOS por moneda. Cada lista tiene a lo sumo una linea
/// por moneda presente. Cobros/pagos van por la moneda REAL del movimiento; saldo/cuentas por cobrar y
/// por pagar por la moneda del SALDO contra las tablas hijas materializadas.
/// </summary>
public record DashboardByCurrencyDto(
    List<CurrencyAmount> CobrosDelMes,
    List<CurrencyAmount> PagosProveedores,
    List<CurrencyAmount> VentasDelMes,
    List<CurrencyAmount> CostosDelMes,
    // Ventas menos costos, POR MONEDA (nunca mezclada con otra moneda). Igual criterio de enmascarado
    // que CostosDelMes: si el usuario no puede ver costos, esta lista viene vacia (no se puede mostrar
    // margen sin mostrar de donde sale, porque venta - margen revelaria el costo).
    List<CurrencyAmount> MargenBruto,
    List<CurrencyAmount> SaldoPendiente,
    List<CurrencyAmount> CuentasPorPagar);

public record CurrencyAmount(string Currency, decimal Amount);

// ADR-021 Capa 6 (B2): el top-N de deudoras se calcula POR MONEDA contra la tabla hija. El DTO gana
// Currency (contrato aditivo; default ARS para compat con el front viejo). Una instalacion 100% ARS
// solo produce items ARS = identico a hoy.
public record PendingReservaDto(Guid PublicId, string NumeroReserva, string Name, decimal Balance, string Status, string Currency = "ARS");
public record UpcomingTripDto(Guid PublicId, string NumeroReserva, string Name, DateTime StartDate, string Status);
public record MonthlyMetricDto(string Month, decimal Sales, decimal Costs, decimal Profit);
public record StatusDistributionDto(int Budgets, int Reserved, int Operational, int Closed, int Cancelled);
/// <summary>
/// Tarjeta 1 del dashboard, "Dólar Banco Nación (venta)" (decision firmada del dueño 2026-08-05):
/// SOLO datos reales — scraper BNA fresco, o su ultimo snapshot persistido, o (ADR-011, enmienda
/// 2026-08-05) el respaldo de la API publica cuando ninguno de los dos anteriores sirvio. Nunca un
/// dato de práctica de ARCA: para eso esta la tarjeta 2 (<see cref="DolarParaFacturarDto"/>).
/// </summary>
/// <param name="Value">Dolar vendedor (billetes BNA, o su equivalente real via la API publica de respaldo).</param>
/// <param name="EuroValue">Euro vendedor BNA, o su equivalente via la API publica de respaldo
/// (ampliacion 2026-08-06: la libreta ahora TAMBIEN sincroniza EUR, ver <c>ExchangeRateSyncJob</c>).
/// <c>Null</c> cuando NINGUNA de las dos fuentes tiene un dato al menos tan fresco como
/// <see cref="Value"/> (nunca se muestra un euro mas viejo que el dolar de al lado sin poder
/// avisarlo — ver <c>ReportService.AttachFreshAuxiliaryCurrenciesAsync</c>). Nunca un numero
/// inventado.</param>
/// <param name="RealValue">Real vendedor BNA. Misma regla que <paramref name="EuroValue"/> (la
/// libreta sincroniza BRL con una escalera de proveedores mas chica, ver el doc de clase de
/// <c>IOfficialDollarPublicApiService</c>).</param>
public record BnaUsdSellerRateDto(
    decimal Value,
    decimal? EuroValue,
    decimal? RealValue,
    string PublishedDate,
    string PublishedTime,
    string Source,
    bool IsStale,
    DateTime FetchedAt);

/// <summary>
/// Tarjeta 2 del dashboard, "Dólar para facturar (ARCA)" (ADR-011, enmienda 2026-08-05, decision
/// firmada del dueño): el MISMO valor que <c>GET /api/exchange-rates/suggestion</c> le sugeriria
/// ahora mismo a la pantalla de facturar — a proposito NO se filtra por "solo datos reales" (a
/// diferencia de la tarjeta 1): en homologacion, ARCA exige facturar con SU numero de practica, asi
/// que mostrar ese mismo numero aca (con el aviso <see cref="EsDePrueba"/> prendido) es lo honesto.
/// </summary>
/// <param name="Value">El tipo de cambio, tal cual lo devuelve el resolver.</param>
/// <param name="RateDate">Fecha REAL del dato (puede ser anterior a hoy si vino de un walk-back).</param>
/// <param name="EsDePrueba">
/// <c>true</c> cuando el sistema esta facturando contra homologacion y este numero es el de practica
/// de ARCA (T-5: nada de enums crudos — el front arma el cartel ambar a partir de este bool, no de un
/// <c>ExchangeRateSource</c>).
/// </param>
public record DolarParaFacturarDto(decimal Value, DateOnly RateDate, bool EsDePrueba);

public record ReportsSummaryResponse(
    int TotalCustomers,
    int TotalReservas,
    int TotalReservations,
    decimal TotalRevenue,
    decimal OutstandingBalance,
    decimal TotalCosts,
    decimal TotalSupplierPayments,
    decimal TotalSales,
    decimal GrossMargin);

// BI Analytics DTOs
public record SellerRankingDto(
    string UserId,
    string SellerName,
    int ReservasCreated,
    decimal TotalSales,
    decimal TotalCosts,
    decimal GrossMargin,
    decimal MarginPercent);

public record DestinationAnalyticsDto(
    string Destination,
    int BookingCount,
    decimal TotalRevenue,
    decimal TotalCost,
    decimal Margin,
    int PassengerCount);

public record CashFlowProjectionResponse(
    List<CashFlowDayDto> Historical,
    List<CashFlowDayDto> Projected,
    decimal CurrentBalance,
    decimal ProjectedBalance30,
    decimal ProjectedBalance60,
    decimal ProjectedBalance90);

public record CashFlowDayDto(
    DateTime Date,
    decimal CashIn,
    decimal CashOut,
    decimal RunningBalance);

public record YearOverYearResponse(
    List<YoyMonthDto> CurrentYear,
    List<YoyMonthDto> PreviousYear,
    decimal CurrentYearTotal,
    decimal PreviousYearTotal,
    decimal GrowthPercent);

public record YoyMonthDto(
    string Month,
    int MonthNumber,
    decimal Sales,
    decimal Costs,
    decimal Margin,
    int ReservaCount);
