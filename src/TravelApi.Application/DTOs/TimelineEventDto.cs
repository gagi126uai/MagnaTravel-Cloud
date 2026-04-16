namespace TravelApi.Application.DTOs;

public class TimelineEventDto
{
    public DateTime Timestamp { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty; 
    public string Title { get; set; } = string.Empty;
    public string? Details { get; set; }
    public string? RelatedEntityType { get; set; }
    public Guid? RelatedEntityPublicId { get; set; }
}
