namespace TravelApi.Contracts.Cupos;

public record CupoAssignmentDto(
    int Id,
    int CupoId,
    int? ReservationId,
    int Quantity,
    DateTime AssignedAt
);
