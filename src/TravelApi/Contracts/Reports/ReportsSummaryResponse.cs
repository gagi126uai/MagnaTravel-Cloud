namespace TravelApi.Contracts.Reports;

public record ReportsSummaryResponse(
    int TotalCustomers,
    int TotalReservations,
    int TotalPayments,
    decimal TotalRevenue,
    decimal OutstandingBalance);
