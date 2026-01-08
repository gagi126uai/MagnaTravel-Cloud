using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
}
