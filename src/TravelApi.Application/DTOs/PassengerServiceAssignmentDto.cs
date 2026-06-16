namespace TravelApi.Application.DTOs;

public class PassengerServiceAssignmentDto
{
    public Guid PublicId { get; set; }
    public Guid PassengerPublicId { get; set; }
    public string PassengerFullName { get; set; } = string.Empty;
    public string ServiceType { get; set; } = string.Empty;
    public int ServiceId { get; set; }
    public Guid? ServicePublicId { get; set; } // resuelto cuando es posible
    public int? RoomNumber { get; set; }
    public string? SeatNumber { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
}

public record CreatePassengerAssignmentRequest(
    string PassengerPublicIdOrLegacyId,
    string ServiceType,         // "Hotel", "Transfer", "Package", "Flight", "Generic"
    string ServicePublicIdOrLegacyId,  // publicId o legacy id del booking/segment
    int? RoomNumber,
    string? SeatNumber,
    string? Notes);

/// <summary>
/// ADR-031 v2.1: cuerpo del REEMPLAZO TOTAL ATOMICO del set de un servicio
/// (PUT .../services/{serviceType}/{servicePublicId}/assignments). Es el conjunto EXACTO de pasajeros
/// que viajan en ese servicio: el backend borra las asignaciones actuales y deja solo estas, todo en
/// una sola transaccion. El servicio se identifica por la ruta (no por el body), igual que el GET de
/// nominal-coverage. Lista vacia (o == todos los pasajeros de la reserva) => CERO asignaciones, por el
/// invariante "todos = sin asignaciones". Solo viajan publicIds de pasajero (sin documento ni datos PII).
/// </summary>
public record ReplaceServiceAssignmentsRequest(
    IReadOnlyList<string> PassengerPublicIds);
