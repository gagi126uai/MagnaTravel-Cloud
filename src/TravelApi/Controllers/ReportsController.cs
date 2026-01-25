using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/reports")]
[Authorize]
public class ReportsController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public ReportsController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("summary")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ReportsSummaryResponse>> GetSummary(CancellationToken cancellationToken)
    {
        var totalCustomers = await _dbContext.Customers.CountAsync(cancellationToken);
        var totalFiles = await _dbContext.TravelFiles.CountAsync(cancellationToken);
        var totalReservations = await _dbContext.Reservations.CountAsync(cancellationToken);
        var totalRevenue = await _dbContext.Payments.SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;
        var totalSales = await _dbContext.TravelFiles.SumAsync(f => (decimal?)f.TotalSale, cancellationToken) ?? 0m;
        var outstandingBalance = await _dbContext.TravelFiles.SumAsync(f => (decimal?)f.Balance, cancellationToken) ?? 0m;

        return Ok(new ReportsSummaryResponse(
            totalCustomers,
            totalFiles,
            totalReservations,
            totalRevenue,
            outstandingBalance));
    }

    [HttpGet("operations")]
    public async Task<ActionResult<OperationsSummaryResponse>> GetOperationsSummary(CancellationToken cancellationToken)
    {
        var totalCustomers = await _dbContext.Customers.CountAsync(cancellationToken);
        var totalFiles = await _dbContext.TravelFiles.CountAsync(cancellationToken);
        var totalPayments = await _dbContext.Payments.CountAsync(cancellationToken);

        return Ok(new OperationsSummaryResponse(totalCustomers, totalFiles, totalPayments));
    }
}

// Simple DTOs inline
public record ReportsSummaryResponse(
    int TotalCustomers,
    int TotalFiles,
    int TotalReservations,
    decimal TotalRevenue,
    decimal OutstandingBalance);

public record OperationsSummaryResponse(
    int TotalCustomers,
    int TotalFiles,
    int TotalPayments);
