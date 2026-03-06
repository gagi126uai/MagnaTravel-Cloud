namespace TravelApi.Application.Interfaces;

public interface IAlertService
{
    Task<object> GetAlertsAsync(CancellationToken cancellationToken);
}
