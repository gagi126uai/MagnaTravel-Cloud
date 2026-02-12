namespace TravelApi.DTOs;

public class PassengerDto
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? DocumentType { get; set; }
    public string? DocumentNumber { get; set; }
    public DateTime? BirthDate { get; set; }
    public string? Nationality { get; set; }
}
