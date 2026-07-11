global using MapperConfiguration = TravelApi.Tests.Fixtures.MapperConfigurationCompat;

using AutoMapper;
using Microsoft.Extensions.Logging.Abstractions;

namespace TravelApi.Tests.Fixtures;

/// <summary>
/// Keeps the test setup concise after AutoMapper 15 made ILoggerFactory mandatory.
/// Production configuration continues to use the framework DI integration.
/// </summary>
public sealed class MapperConfigurationCompat
{
    private readonly AutoMapper.MapperConfiguration _configuration;

    public MapperConfigurationCompat(Action<IMapperConfigurationExpression> configure)
    {
        _configuration = new AutoMapper.MapperConfiguration(configure, NullLoggerFactory.Instance);
    }

    public IMapper CreateMapper() => _configuration.CreateMapper();
}
