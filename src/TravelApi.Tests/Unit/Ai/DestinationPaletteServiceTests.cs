using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using TravelApi.Application.Ai;
using TravelApi.Infrastructure.Ai;
using Xunit;

namespace TravelApi.Tests.Unit.Ai;

/// <summary>
/// Tests de <see cref="DestinationPaletteService"/> (spec "PDF minimalista elegante", 2026-08-14 §5):
/// una categoría conocida del set curado da el hex correcto, cualquier otra respuesta (basura, "otro",
/// degradado) cae al respaldo (<c>null</c>), y la caché evita una segunda llamada al modelo para el
/// mismo destino. Usa <see cref="FakeAiChatProvider"/> (definido en <c>ServiceLineInterpreterTests.cs</c>,
/// mismo namespace) — NO se llama a la nube.
/// </summary>
public class DestinationPaletteServiceTests
{
    private static DestinationPaletteService BuildService(FakeAiChatProvider provider, bool aiUsable = true, IMemoryCache? cache = null)
    {
        var assistant = new AiAssistantService(provider, NullLogger<AiAssistantService>.Instance);
        var connectionResolver = new FakeAiConnectionResolver(aiUsable);
        var memoryCache = cache ?? new MemoryCache(new MemoryCacheOptions());
        return new DestinationPaletteService(assistant, connectionResolver, memoryCache, NullLogger<DestinationPaletteService>.Instance);
    }

    [Fact]
    public async Task ResolveAccentColorHexAsync_KnownCategory_ReturnsItsHex()
    {
        var provider = new FakeAiChatProvider(AiChatResult.Success("caribe"));
        var service = BuildService(provider);

        var hex = await service.ResolveAccentColorHexAsync("Punta Cana", new[] { "Punta Cana" }, CancellationToken.None);

        Assert.Equal("#0e7c86", hex);
        Assert.Equal(1, provider.CallCount);
    }

    [Theory]
    [InlineData("otro")] // el set curado incluye "otro" a propósito, pero no tiene hex propio: cae al respaldo.
    [InlineData("no se")] // texto fuera del set cerrado.
    [InlineData("")] // respuesta vacía.
    [InlineData("Error: rate limit exceeded")] // un proveedor "roto" que devuelve un mensaje de error como si fuera contenido NUNCA debe llegar al PDF como si fuera una categoría válida.
    public async Task ResolveAccentColorHexAsync_OutsideCuratedSet_FallsBackToNull(string modelAnswer)
    {
        var provider = new FakeAiChatProvider(AiChatResult.Success(modelAnswer));
        var service = BuildService(provider);

        var hex = await service.ResolveAccentColorHexAsync("Bariloche", new[] { "Bariloche" }, CancellationToken.None);

        Assert.Null(hex);
    }

    [Fact]
    public async Task ResolveAccentColorHexAsync_ModelDegraded_FallsBackToNull()
    {
        var provider = new FakeAiChatProvider(AiChatResult.Degraded("timeout de prueba"));
        var service = BuildService(provider);

        var hex = await service.ResolveAccentColorHexAsync("Mendoza", new[] { "Mendoza" }, CancellationToken.None);

        Assert.Null(hex);
    }

    [Fact]
    public async Task ResolveAccentColorHexAsync_AiNotConfigured_FallsBackWithoutCallingProvider()
    {
        var provider = new FakeAiChatProvider(AiChatResult.Success("caribe"));
        var service = BuildService(provider, aiUsable: false);

        var hex = await service.ResolveAccentColorHexAsync("Punta Cana", new[] { "Punta Cana" }, CancellationToken.None);

        Assert.Null(hex);
        Assert.Equal(0, provider.CallCount); // sin IA usable, ni se arma el prompt.
    }

    [Fact]
    public async Task ResolveAccentColorHexAsync_SameDestinationTwice_SecondCallHitsCache()
    {
        var provider = new FakeAiChatProvider(AiChatResult.Success("nieve"));
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = BuildService(provider, cache: cache);

        var first = await service.ResolveAccentColorHexAsync("Bariloche", new[] { "Bariloche" }, CancellationToken.None);
        var second = await service.ResolveAccentColorHexAsync("Bariloche", new[] { "Bariloche" }, CancellationToken.None);

        Assert.Equal("#3d6b9e", first);
        Assert.Equal("#3d6b9e", second);
        Assert.Equal(1, provider.CallCount); // la segunda vino de la caché, no volvió a llamar al modelo.
    }

    [Fact]
    public async Task ResolveAccentColorHexAsync_NoDestinationNorCities_FallsBackWithoutCallingProvider()
    {
        var provider = new FakeAiChatProvider(AiChatResult.Success("caribe"));
        var service = BuildService(provider);

        var hex = await service.ResolveAccentColorHexAsync(null, System.Array.Empty<string>(), CancellationToken.None);

        Assert.Null(hex);
        Assert.Equal(0, provider.CallCount); // nada que clasificar -- ni se arma el prompt.
    }

    /// <summary>
    /// Red de contención final (mismo espíritu que el bug PROD que motivó <see cref="ThrowingChatProvider"/>
    /// en la línea inteligente): un proveedor que explota con una excepción CRUDA en vez de degradar solo
    /// (clave rechazada, algo que el provider no contempló en sus propios catch) NUNCA puede tirar abajo
    /// la generación del PDF de presupuesto. El color es puramente decorativo -- se degrada al respaldo.
    /// </summary>
    [Fact]
    public async Task ResolveAccentColorHexAsync_ProviderThrows_FallsBackToNullWithoutPropagating()
    {
        var provider = new ThrowingChatProvider();
        var assistant = new AiAssistantService(provider, NullLogger<AiAssistantService>.Instance);
        var connectionResolver = new FakeAiConnectionResolver(usable: true);
        var service = new DestinationPaletteService(
            assistant, connectionResolver, new MemoryCache(new MemoryCacheOptions()), NullLogger<DestinationPaletteService>.Instance);

        var hex = await service.ResolveAccentColorHexAsync("Ushuaia", new[] { "Ushuaia" }, CancellationToken.None);

        Assert.Null(hex);
        Assert.Equal(1, provider.CallCount);
    }
}
