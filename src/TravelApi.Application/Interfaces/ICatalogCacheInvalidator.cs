namespace TravelApi.Application.Interfaces;

public interface ICatalogCacheInvalidator
{
    Task InvalidateAsync(CancellationToken ct);
}
