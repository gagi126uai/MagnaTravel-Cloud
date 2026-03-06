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
    List<PendingFileDto> ExpedientesPendientes,
    List<UpcomingTripDto> ProximosViajes,
    List<MonthlyMetricDto> TendenciaHistorica,
    StatusDistributionDto DistribucionEstados);

public record PendingFileDto(int Id, string FileNumber, string Name, decimal Balance, string Status);
public record UpcomingTripDto(int Id, string FileNumber, string Name, DateTime StartDate, string Status);
public record MonthlyMetricDto(string Month, decimal Sales, decimal Costs, decimal Profit);
public record StatusDistributionDto(int Budgets, int Reserved, int Operational, int Closed, int Cancelled);

public record ReportsSummaryResponse(
    int TotalCustomers,
    int TotalFiles,
    int TotalReservations,
    decimal TotalRevenue,
    decimal OutstandingBalance,
    decimal TotalCosts,
    decimal TotalSupplierPayments,
    decimal TotalSales,
    decimal GrossMargin);
