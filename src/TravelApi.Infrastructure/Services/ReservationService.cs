using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class ReservationService : IReservationService
{
    private readonly AppDbContext _dbContext;

    public ReservationService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IEnumerable<Reservation>> GetReservationsAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.Reservations
            .AsNoTracking()
            .Include(r => r.Customer)
            .Include(r => r.Supplier)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<Reservation?> GetReservationAsync(int id, CancellationToken cancellationToken)
    {
        return await _dbContext.Reservations
            .AsNoTracking()
            .Include(r => r.Customer)
            .Include(r => r.Supplier)
            .Include(r => r.Payments)
            .Include(r => r.Segments)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    }

    public async Task<FlightSegment> CreateSegmentAsync(int reservationId, FlightSegment segment, CancellationToken cancellationToken)
    {
        var reservation = await _dbContext.Reservations
            .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken);

        if (reservation is null)
            throw new ArgumentException("Reserva no encontrada.");

        if (segment.ArrivalTime < segment.DepartureTime)
            throw new ArgumentException("La fecha de llegada no puede ser anterior a la de salida.");

        segment.ReservationId = reservationId;
        segment.DepartureTime = NormalizeUtc(segment.DepartureTime);
        segment.ArrivalTime = NormalizeUtc(segment.ArrivalTime);

        _dbContext.FlightSegments.Add(segment);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return segment;
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        return value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}
