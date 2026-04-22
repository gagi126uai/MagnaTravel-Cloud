namespace TravelApi.Application.Contracts.Events;

public abstract record IntegrationEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public string EventType => GetType().Name;
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
    public Guid? CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public int PayloadVersion { get; init; } = 1;
}
