using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Interfaces;

namespace TravelApi.Infrastructure.Services;

public class CatalogCacheInvalidator : ICatalogCacheInvalidator
{
    private const string CatalogTag = "catalog";

    private readonly IOutputCacheStore _outputCacheStore;
    private readonly ILogger<CatalogCacheInvalidator> _logger;

    public CatalogCacheInvalidator(
        IOutputCacheStore outputCacheStore,
        ILogger<CatalogCacheInvalidator> logger)
    {
        _outputCacheStore = outputCacheStore;
        _logger = logger;
    }

    public async Task InvalidateAsync(CancellationToken ct)
    {
        try
        {
            await _outputCacheStore.EvictByTagAsync(CatalogTag, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not invalidate catalog output cache.");
        }
    }
}
