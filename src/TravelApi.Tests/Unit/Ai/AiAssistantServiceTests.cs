using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TravelApi.Application.Ai;
using TravelApi.Infrastructure.Ai;
using Xunit;

namespace TravelApi.Tests.Unit.Ai;

/// <summary>
/// Tests de <see cref="AiAssistantService"/> (orquestacion de alto nivel del cerebro, F0a).
/// Usan <see cref="FakeAiChatProvider"/>: NO se llama a la nube. Cubren la deserializacion
/// estricta, el reintento unico y la degradacion elegante (regla dura 6 del ADR-016 §2.5).
/// </summary>
public class AiAssistantServiceTests
{
    /// <summary>DTO de prueba que representaria una salida estructurada del modelo.</summary>
    private sealed class SampleOutput
    {
        public string Frase { get; set; } = string.Empty;
        public int Prioridad { get; set; }
    }

    private static AiAssistantService BuildService(FakeAiChatProvider provider)
        => new(provider, NullLogger<AiAssistantService>.Instance);

    private static AiChatRequest SampleRequest()
        => new(new[] { AiChatMessage.System("contrato"), AiChatMessage.User("pedido") });

    // ============================================================
    // CompleteAsync (texto libre): passthrough + degradacion
    // ============================================================

    [Fact]
    public async Task CompleteAsync_ProviderSucceeds_ReturnsText()
    {
        var provider = new FakeAiChatProvider(AiChatResult.Success("hola equipo"));
        var service = BuildService(provider);

        var result = await service.CompleteAsync(SampleRequest(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("hola equipo", result.Text);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task CompleteAsync_ProviderDegrades_ReturnsDegradedWithoutThrowing()
    {
        var provider = new FakeAiChatProvider(AiChatResult.Degraded("timeout"));
        var service = BuildService(provider);

        var result = await service.CompleteAsync(SampleRequest(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(string.Empty, result.Text);
    }

    // ============================================================
    // CompleteStructuredAsync: JSON valido al primer intento
    // ============================================================

    [Fact]
    public async Task CompleteStructuredAsync_ValidJsonFirstTry_DeserializesWithoutRetry()
    {
        var provider = new FakeAiChatProvider(
            AiChatResult.Success("{\"frase\":\"Contactar hoy\",\"prioridad\":2}"));
        var service = BuildService(provider);

        var (value, result) = await service.CompleteStructuredAsync<SampleOutput>(
            SampleRequest(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(value);
        Assert.Equal("Contactar hoy", value!.Frase);
        Assert.Equal(2, value.Prioridad);
        // No hubo reintento: el primer intento ya valido.
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task CompleteStructuredAsync_StripsMarkdownCodeFences()
    {
        // Algunos modelos envuelven el JSON en ```json ... ```. Debe deserializar igual.
        var fenced = "```json\n{\"frase\":\"ok\",\"prioridad\":1}\n```";
        var provider = new FakeAiChatProvider(AiChatResult.Success(fenced));
        var service = BuildService(provider);

        var (value, result) = await service.CompleteStructuredAsync<SampleOutput>(
            SampleRequest(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(value);
        Assert.Equal("ok", value!.Frase);
    }

    // ============================================================
    // CompleteStructuredAsync: JSON invalido -> 1 reintento -> exito
    // ============================================================

    [Fact]
    public async Task CompleteStructuredAsync_InvalidThenValid_RetriesOnceAndSucceeds()
    {
        var provider = new FakeAiChatProvider(
            AiChatResult.Success("esto no es json"),
            AiChatResult.Success("{\"frase\":\"recuperado\",\"prioridad\":3}"));
        var service = BuildService(provider);

        var (value, result) = await service.CompleteStructuredAsync<SampleOutput>(
            SampleRequest(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(value);
        Assert.Equal("recuperado", value!.Frase);
        // Exactamente DOS llamadas: el intento original + un solo reintento.
        Assert.Equal(2, provider.CallCount);
    }

    // ============================================================
    // CompleteStructuredAsync: JSON invalido dos veces -> degrada
    // ============================================================

    [Fact]
    public async Task CompleteStructuredAsync_InvalidTwice_DegradesAfterSingleRetry()
    {
        var provider = new FakeAiChatProvider(
            AiChatResult.Success("basura 1"),
            AiChatResult.Success("basura 2"));
        var service = BuildService(provider);

        var (value, result) = await service.CompleteStructuredAsync<SampleOutput>(
            SampleRequest(), CancellationToken.None);

        // No se inventa contenido: value es null y el resultado esta degradado.
        Assert.Null(value);
        Assert.False(result.Succeeded);
        // Un solo reintento (no loop infinito): 2 llamadas y para.
        Assert.Equal(2, provider.CallCount);
    }

    // ============================================================
    // CompleteStructuredAsync: propiedad desconocida -> modo estricto la rechaza
    // ============================================================

    [Fact]
    public async Task CompleteStructuredAsync_UnknownProperty_StrictModeRejectsThenDegrades()
    {
        // El modelo "alucina" un campo extra (campoFantasma) que el DTO no tiene.
        // El modo estricto (UnmappedMemberHandling.Disallow) debe rechazarlo, reintentar
        // y, como el reintento devuelve lo mismo, degradar. Esto prueba que NO aceptamos
        // salida con forma inesperada silenciosamente.
        var hallucinated = "{\"frase\":\"x\",\"prioridad\":1,\"campoFantasma\":true}";
        var provider = new FakeAiChatProvider(
            AiChatResult.Success(hallucinated),
            AiChatResult.Success(hallucinated));
        var service = BuildService(provider);

        var (value, result) = await service.CompleteStructuredAsync<SampleOutput>(
            SampleRequest(), CancellationToken.None);

        Assert.Null(value);
        Assert.False(result.Succeeded);
        Assert.Equal(2, provider.CallCount);
    }

    // ============================================================
    // CompleteStructuredAsync: si la PRIMERA llamada degrada (timeout/red),
    // NO reintenta (el problema no es el contenido)
    // ============================================================

    [Fact]
    public async Task CompleteStructuredAsync_ProviderDegradesFirst_DoesNotRetry()
    {
        var provider = new FakeAiChatProvider(AiChatResult.Degraded("timeout"));
        var service = BuildService(provider);

        var (value, result) = await service.CompleteStructuredAsync<SampleOutput>(
            SampleRequest(), CancellationToken.None);

        Assert.Null(value);
        Assert.False(result.Succeeded);
        // Una sola llamada: ante degradacion de conexion no gastamos un reintento.
        Assert.Equal(1, provider.CallCount);
    }
}
