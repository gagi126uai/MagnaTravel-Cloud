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
}
