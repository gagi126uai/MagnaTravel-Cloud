using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/reservations")]
[Authorize]
public class ReservationsController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public ReservationsController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Reservation>>> GetReservations(CancellationToken cancellationToken)
    {
        var reservations = await _dbContext.Reservations
            .AsNoTracking()
            .Include(r => r.Customer)
            .Include(r => r.Supplier)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);

        return Ok(reservations);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Reservation>> GetReservation(int id, CancellationToken cancellationToken)
    {
        var reservation = await _dbContext.Reservations
            .AsNoTracking()
            .Include(r => r.Customer)
            .Include(r => r.Supplier)
            .Include(r => r.Payments)
            .Include(r => r.Segments)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (reservation is null)
        {
            return NotFound();
        }

        return Ok(reservation);
    }

    [HttpPost("{id:int}/segments")]
    public async Task<ActionResult<FlightSegment>> CreateSegment(
        int id,
        FlightSegment segment,
        CancellationToken cancellationToken)
    {
        var reservation = await _dbContext.Reservations
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (reservation is null)
        {
            return NotFound("Reserva no encontrada.");
        }

        if (segment.ArrivalTime < segment.DepartureTime)
        {
            return BadRequest("La fecha de llegada no puede ser anterior a la de salida.");
        }

        segment.ReservationId = id;
        segment.DepartureTime = NormalizeUtc(segment.DepartureTime);
        segment.ArrivalTime = NormalizeUtc(segment.ArrivalTime);

        _dbContext.FlightSegments.Add(segment);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetReservation), new { id = id }, segment);
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        return value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}
