using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Contracts.Reservations;
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
            .Include(found => found.Segments)
            .FirstOrDefaultAsync(found => found.Id == id, cancellationToken);

        if (reservation is null)
        {
            return NotFound();
        }

        return Ok(reservation);
    }

    [HttpPost]
    public async Task<ActionResult<Reservation>> CreateReservation(
        CreateReservationRequest request,
        CancellationToken cancellationToken)
    {
        if (!HasValidDates(request.DepartureDate, request.ReturnDate))
        {
            return BadRequest("Las fechas de viaje no son válidas.");
        }

        if (!HasValidAmounts(request.BasePrice, request.Commission, request.TotalAmount))
        {
            return BadRequest("Los montos de la reserva no son válidos.");
        }

        var reservation = new Reservation
        {
            ReferenceCode = request.ReferenceCode,
            Status = ReservationStatuses.Draft,
            ProductType = request.ProductType,
            DepartureDate = NormalizeUtc(request.DepartureDate),
            ReturnDate = request.ReturnDate.HasValue ? NormalizeUtc(request.ReturnDate.Value) : null,
            BasePrice = request.BasePrice,
            Commission = request.Commission,
            TotalAmount = request.TotalAmount,
            SupplierName = request.SupplierName,
            CustomerId = request.CustomerId
        };

        _dbContext.Reservations.Add(reservation);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetReservation), new { id = reservation.Id }, reservation);
    }

    [HttpPut("{id:int}/status")]
    public async Task<ActionResult<Reservation>> UpdateReservationStatus(
        int id,
        UpdateReservationStatusRequest request,
        CancellationToken cancellationToken)
    {
        if (!ReservationStatuses.IsValid(request.Status))
        {
            return BadRequest("Estado de reserva inválido.");
        }

        var reservation = await _dbContext.Reservations.FindAsync(new object[] { id }, cancellationToken);

        if (reservation is null)
        {
            return NotFound();
        }

        if (!ReservationStatuses.CanTransition(reservation.Status, request.Status))
        {
            return BadRequest("Transición de estado no permitida.");
        }

        reservation.Status = request.Status;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(reservation);
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        return value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    private static bool HasValidDates(DateTime departureDate, DateTime? returnDate)
    {
        if (!returnDate.HasValue)
        {
            return true;
        }

        return returnDate.Value.Date >= departureDate.Date;
    }

    private static bool HasValidAmounts(decimal basePrice, decimal commission, decimal totalAmount)
    {
        if (basePrice < 0m || commission < 0m || totalAmount < 0m)
        {
            return false;
        }

        return totalAmount >= basePrice + commission;
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
        
        // Ensure dates are UTC
        segment.DepartureTime = NormalizeUtc(segment.DepartureTime);
        segment.ArrivalTime = NormalizeUtc(segment.ArrivalTime);

        _dbContext.FlightSegments.Add(segment);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetReservation), new { id = id }, segment);
    }
}
