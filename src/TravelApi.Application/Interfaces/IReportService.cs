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
    int ActivePotentialCustomers);

public record PendingReservaDto(Guid PublicId, string NumeroReserva, string Name, decimal Balance, string Status);
public record UpcomingTripDto(Guid PublicId, string NumeroReserva, string Name, DateTime StartDate, string Status);
public record MonthlyMetricDto(string Month, decimal Sales, decimal Costs, decimal Profit);
public record StatusDistributionDto(int Budgets, int Reserved, int Operational, int Closed, int Cancelled);
public record BnaUsdSellerRateDto(
    decimal Value,
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
