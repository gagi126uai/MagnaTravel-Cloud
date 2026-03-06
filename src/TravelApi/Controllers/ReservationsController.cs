using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/reservations")]
[Authorize]
public class ReservationsController : ControllerBase
{
    private readonly IReservationService _reservationService;

    public ReservationsController(IReservationService reservationService)
    {
        _reservationService = reservationService;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Reservation>>> GetReservations(CancellationToken cancellationToken)
    {
        var reservations = await _reservationService.GetReservationsAsync(cancellationToken);
        return Ok(reservations);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Reservation>> GetReservation(int id, CancellationToken cancellationToken)
    {
        var reservation = await _reservationService.GetReservationAsync(id, cancellationToken);
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
        try
        {
            var createdSegment = await _reservationService.CreateSegmentAsync(id, segment, cancellationToken);
            return CreatedAtAction(nameof(GetReservation), new { id = id }, createdSegment);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }
}
