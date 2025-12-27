using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/reservations")]
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
            .Include(reservation => reservation.Customer)
            .OrderByDescending(reservation => reservation.CreatedAt)
            .ToListAsync(cancellationToken);

        return Ok(reservations);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Reservation>> GetReservation(int id, CancellationToken cancellationToken)
    {
        var reservation = await _dbContext.Reservations
            .AsNoTracking()
            .Include(found => found.Customer)
            .Include(found => found.Payments)
            .FirstOrDefaultAsync(found => found.Id == id, cancellationToken);

        if (reservation is null)
        {
            return NotFound();
        }

        return Ok(reservation);
    }

    [HttpPost]
    public async Task<ActionResult<Reservation>> CreateReservation(Reservation reservation, CancellationToken cancellationToken)
    {
        _dbContext.Reservations.Add(reservation);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetReservation), new { id = reservation.Id }, reservation);
    }
}
