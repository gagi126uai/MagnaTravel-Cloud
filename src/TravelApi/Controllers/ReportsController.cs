using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Contracts.Accounting;
using TravelApi.Contracts.Reports;
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
        var totalReservations = await _dbContext.Reservations.CountAsync(cancellationToken);
        var totalPayments = await _dbContext.Payments.CountAsync(cancellationToken);
        var totalRevenue = await _dbContext.Payments.SumAsync(payment => (decimal?)payment.Amount, cancellationToken) ?? 0m;
        var totalReservationAmount = await _dbContext.Reservations.SumAsync(reservation => (decimal?)reservation.TotalAmount, cancellationToken) ?? 0m;
        var outstandingBalance = totalReservationAmount - totalRevenue;

        var response = new ReportsSummaryResponse(
            totalCustomers,
            totalReservations,
            totalPayments,
            totalRevenue,
            outstandingBalance);

        return Ok(response);
    }

    [HttpGet("operations")]
    public async Task<ActionResult<OperationsSummaryResponse>> GetOperationsSummary(CancellationToken cancellationToken)
    {
        var totalCustomers = await _dbContext.Customers.CountAsync(cancellationToken);
        var totalReservations = await _dbContext.Reservations.CountAsync(cancellationToken);
        var totalPayments = await _dbContext.Payments.CountAsync(cancellationToken);

        var response = new OperationsSummaryResponse(totalCustomers, totalReservations, totalPayments);

        return Ok(response);
    }

    [HttpGet("cupos")]
    public async Task<ActionResult<CupoOperationsSummaryResponse>> GetCuposSummary(CancellationToken cancellationToken)
    {
        var totalCupos = await _dbContext.Cupos.CountAsync(cancellationToken);
        var totalCapacity = await _dbContext.Cupos.SumAsync(cupo => (int?)cupo.Capacity, cancellationToken) ?? 0;
        var totalReserved = await _dbContext.Cupos.SumAsync(cupo => (int?)cupo.Reserved, cancellationToken) ?? 0;
        var totalOverbookingLimit = await _dbContext.Cupos.SumAsync(cupo => (int?)cupo.OverbookingLimit, cancellationToken) ?? 0;
        var totalAvailable = totalCapacity + totalOverbookingLimit - totalReserved;
        var totalOverbooked = Math.Max(0, totalReserved - totalCapacity);

        var response = new CupoOperationsSummaryResponse(
            totalCupos,
            totalCapacity,
            totalReserved,
            totalOverbookingLimit,
            totalAvailable,
            totalOverbooked);

        return Ok(response);
    }

    [HttpGet("bsp-dashboard")]
    public async Task<ActionResult<BspDashboardResponse>> GetBspDashboard(CancellationToken cancellationToken)
    {
        var totalBatches = await _dbContext.BspImportBatches.CountAsync(cancellationToken);
        var openBatches = await _dbContext.BspImportBatches.CountAsync(
            batch => batch.Status == "Open",
            cancellationToken);
        var closedBatches = totalBatches - openBatches;

        var totalRecords = await _dbContext.BspNormalizedRecords.CountAsync(cancellationToken);
        var totalImportedAmount = await _dbContext.BspNormalizedRecords
            .SumAsync(record => (decimal?)record.TotalAmount, cancellationToken) ?? 0m;
        var totalMatchedAmount = await _dbContext.BspReconciliationEntries
            .Where(entry => entry.Status == "Matched")
            .Select(entry => entry.BspNormalizedRecord!.TotalAmount)
            .SumAsync(amount => (decimal?)amount, cancellationToken) ?? 0m;

        var response = new BspDashboardResponse(
            totalBatches,
            openBatches,
            closedBatches,
            totalRecords,
            totalImportedAmount,
            totalMatchedAmount);

        return Ok(response);
    }

    [HttpGet("accounting-dashboard")]
    public async Task<ActionResult<AccountingDashboardResponse>> GetAccountingDashboard(CancellationToken cancellationToken)
    {
        var totalEntries = await _dbContext.AccountingEntries.CountAsync(cancellationToken);
        var totalLines = await _dbContext.AccountingLines.CountAsync(cancellationToken);
        var totalDebits = await _dbContext.AccountingLines
            .SumAsync(line => (decimal?)line.Debit, cancellationToken) ?? 0m;
        var totalCredits = await _dbContext.AccountingLines
            .SumAsync(line => (decimal?)line.Credit, cancellationToken) ?? 0m;

        var response = new AccountingDashboardResponse(
            totalEntries,
            totalLines,
            totalDebits,
            totalCredits);

        return Ok(response);
    }
}
