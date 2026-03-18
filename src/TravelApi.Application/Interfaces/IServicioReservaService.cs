using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

public interface IServicioReservaService
{
    Task<IEnumerable<ServicioReserva>> GetServiciosAsync(CancellationToken cancellationToken);
    Task<ServicioReserva?> GetServicioByIdAsync(int id, CancellationToken cancellationToken);
    Task<FlightSegment> CreateSegmentAsync(int servicioId, FlightSegment segment, CancellationToken cancellationToken);
}
