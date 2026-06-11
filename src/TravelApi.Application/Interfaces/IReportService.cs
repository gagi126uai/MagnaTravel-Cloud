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
    DashboardByCurrencyDto? PorMoneda = null);

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
public record BnaUsdSellerRateDto(
    decimal Value,
    decimal EuroValue,
    decimal RealValue,
    string PublishedDate,
    string PublishedTime,
    string Source,
    bool IsStale,
    DateTime FetchedAt);

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
