using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

public interface IReservationService
{
    Task<IEnumerable<Reservation>> GetReservationsAsync(CancellationToken cancellationToken);
    Task<Reservation?> GetReservationAsync(int id, CancellationToken cancellationToken);
    Task<FlightSegment> CreateSegmentAsync(int reservationId, FlightSegment segment, CancellationToken cancellationToken);
}
