namespace TravelApi.Application.Contracts.Files;

public class CreateReservaRequest
{
    public string Name { get; set; } = string.Empty;
    public string? PayerId { get; set; }
    public DateTime? StartDate { get; set; }
    public string? Description { get; set; }
    public string? Status { get; set; }
}
