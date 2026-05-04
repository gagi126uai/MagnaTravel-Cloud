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
