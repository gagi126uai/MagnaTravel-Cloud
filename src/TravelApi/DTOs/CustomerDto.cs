namespace TravelApi.DTOs;

public class CustomerDto
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? TaxId { get; set; }
}
