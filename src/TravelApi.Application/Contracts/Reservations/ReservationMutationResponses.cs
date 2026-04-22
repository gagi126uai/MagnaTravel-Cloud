using TravelApi.Application.DTOs;

namespace TravelApi.Application.Contracts.Reservations;

public class ReservationServiceMutationResult
{
    public required ServicioReservaDto Servicio { get; set; }
    public string? Warning { get; set; }
}
