namespace TravelApi.Application.DTOs;

public class CustomerDto
{
    public Guid PublicId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? TaxId { get; set; }
}
