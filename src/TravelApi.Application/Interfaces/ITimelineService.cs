using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface ITimelineService
{
    Task<List<TimelineEventDto>> GetTimelineAsync(int reservaId, CancellationToken cancellationToken);
}
