namespace TravelApi.Application.DTOs;

public class PassengerDto
{
    public Guid PublicId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? DocumentType { get; set; }
    public string? DocumentNumber { get; set; }
    public DateTime? BirthDate { get; set; }
    public string? Nationality { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Gender { get; set; }
    public string? Notes { get; set; }
}
