using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/payments")]
[Authorize]
public class PaymentsController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public PaymentsController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("reservation/{reservationId:int}")]
    public async Task<ActionResult<IEnumerable<Payment>>> GetPaymentsForReservation(
        int reservationId,
        CancellationToken cancellationToken)
    {
        var payments = await _dbContext.Payments
            .AsNoTracking()
            .Where(payment => payment.ReservationId == reservationId)
            .OrderByDescending(payment => payment.PaidAt)
            .ToListAsync(cancellationToken);

        return Ok(payments);
    }

    [HttpPost]
    public async Task<ActionResult<Payment>> CreatePayment(Payment payment, CancellationToken cancellationToken)
    {
        var reservationExists = await _dbContext.Reservations
            .AnyAsync(reservation => reservation.Id == payment.ReservationId, cancellationToken);

        if (!reservationExists)
        {
            return BadRequest("Reservation does not exist.");
        }

        _dbContext.Payments.Add(payment);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetPaymentsForReservation), new { reservationId = payment.ReservationId }, payment);
    }
}
