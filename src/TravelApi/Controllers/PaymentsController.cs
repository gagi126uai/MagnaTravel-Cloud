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

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Payment>>> GetAllPayments(CancellationToken cancellationToken)
    {
        var payments = await _dbContext.Payments
            .AsNoTracking()
            .Include(p => p.Reservation)
                .ThenInclude(r => r!.Customer)
            .Include(p => p.Reservation)
                .ThenInclude(r => r!.TravelFile)
            .OrderByDescending(p => p.PaidAt)
            .ToListAsync(cancellationToken);

        return Ok(payments);
    }

    [HttpGet("reservation/{reservationId:int}")]
    public async Task<ActionResult<IEnumerable<Payment>>> GetPaymentsForReservation(
        int reservationId,
        CancellationToken cancellationToken)
    {
        var payments = await _dbContext.Payments
            .AsNoTracking()
            .Where(p => p.ReservationId == reservationId)
            .OrderByDescending(p => p.PaidAt)
            .ToListAsync(cancellationToken);

        return Ok(payments);
    }

    [HttpPost]
    public async Task<ActionResult<Payment>> CreatePayment(
        Payment payment,
        CancellationToken cancellationToken)
    {
        // Validate reservation exists
        var reservation = await _dbContext.Reservations
            .Include(r => r.TravelFile)
            .FirstOrDefaultAsync(r => r.Id == payment.ReservationId, cancellationToken);

        if (reservation is null)
        {
            return BadRequest("Reserva no encontrada.");
        }

        payment.PaidAt = DateTime.UtcNow;
        _dbContext.Payments.Add(payment);

        // Update TravelFile balance if linked
        if (reservation.TravelFile is not null)
        {
            reservation.TravelFile.Balance -= payment.Amount;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetPaymentsForReservation), new { reservationId = payment.ReservationId }, payment);
    }
}
