namespace TravelApi.Application.DTOs;

public class LeadJourneyDto
{
    public Guid LeadPublicId { get; set; }
    public Guid? ConvertedCustomerPublicId { get; set; }
    public string? ConvertedCustomerName { get; set; }
    public Guid? LatestQuotePublicId { get; set; }
    public Guid? LatestReservaPublicId { get; set; }
    public List<LeadJourneyQuoteDto> Quotes { get; set; } = new();
    public List<LeadJourneyReservaDto> Reservas { get; set; } = new();
}

public class LeadJourneyQuoteDto
{
    public Guid PublicId { get; set; }
    public string QuoteNumber { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public Guid? CustomerPublicId { get; set; }
    public Guid? ConvertedReservaPublicId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class LeadJourneyReservaDto
{
    public Guid PublicId { get; set; }
    public string NumeroReserva { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public Guid? SourceQuotePublicId { get; set; }
    public DateTime CreatedAt { get; set; }
}
