using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

namespace TravelApi.Infrastructure.Services.ReservationsServiceProxy;

public class TimelineServiceHttpProxy : ReservationsServiceHttpProxyBase, ITimelineService
{
    public TimelineServiceHttpProxy(HttpClient httpClient)
        : base(httpClient)
    {
    }

    public Task<List<TimelineEventDto>> GetTimelineAsync(string reservaPublicIdOrLegacyId, CancellationToken cancellationToken)
        => GetAsync<List<TimelineEventDto>>($"api/reservas/{reservaPublicIdOrLegacyId}/timeline", cancellationToken);
}
