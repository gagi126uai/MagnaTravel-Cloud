namespace TravelApi.Application.DTOs;

public class LeadJourneyDto
{
    public int LeadId { get; set; }
    public int? ConvertedCustomerId { get; set; }
    public string? ConvertedCustomerName { get; set; }
    public int? LatestQuoteId { get; set; }
    public int? LatestReservaId { get; set; }
    public List<LeadJourneyQuoteDto> Quotes { get; set; } = new();
    public List<LeadJourneyReservaDto> Reservas { get; set; } = new();
}

public class LeadJourneyQuoteDto
{
    public int Id { get; set; }
    public string QuoteNumber { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int? CustomerId { get; set; }
    public int? ConvertedReservaId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class LeadJourneyReservaDto
{
    public int Id { get; set; }
    public string NumeroReserva { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int? SourceQuoteId { get; set; }
    public DateTime CreatedAt { get; set; }
}
