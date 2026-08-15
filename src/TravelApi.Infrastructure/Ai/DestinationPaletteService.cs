using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Ai;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Helpers;

namespace TravelApi.Infrastructure.Ai;

/// <summary>
/// Implementación de <see cref="IDestinationPaletteService"/> (spec "PDF minimalista elegante",
/// 2026-08-14 §5). Mismo patrón que <see cref="ServiceLineInterpreter"/>: conexión barata vía
/// <see cref="IAiConnectionResolver"/>, UN turno de chat, y degradación elegante ante cualquier falla —
/// el color de acento es puramente decorativo, así que ninguna falla de acá puede frenar la emisión de
/// un PDF de presupuesto.
///
/// <para><b>Por qué UN turno de texto libre y no salida estructurada (JSON)</b>: el pedido es "devolvé
/// UNA palabra de esta lista". Pedirle al modelo un JSON con una sola clave sería más ceremonia (y más
/// tokens) sin ganar nada — el parseo acá es tan simple como comparar el texto contra el set cerrado.</para>
/// </summary>
public sealed class DestinationPaletteService : IDestinationPaletteService
{
    /// <summary>
    /// El set CURADO de categorías y su color de acento (spec §5). Es una lista CERRADA a propósito: la
    /// IA nunca elige un hex libre, solo una de estas siete palabras — así el PDF nunca puede terminar
    /// pintado con un color que la agencia no aprobó. "otro" no tiene entrada acá: cae directo al
    /// fallback del caller (el color de <c>AgencySettings.PdfBandColorHex</c>).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> CategoryAccentHex = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["caribe"] = "#0e7c86",
        ["playa"] = "#b3873e",
        ["nieve"] = "#3d6b9e",
        ["ciudad"] = "#b05c3b",
        ["naturaleza"] = "#3e7d4f",
        ["vino"] = "#7d3c4e",
    };

    /// <summary>
    /// Tiempo que se guarda una elección de categoría por destino (spec §5). 30 días, chato, para
    /// ÉXITO Y PARA FALLBACK por igual — a diferencia de la caché de "la línea inteligente"
    /// (<see cref="ServiceLineInterpretationCache"/>, que reintenta rápido tras una falla), acá una
    /// falla transitoria del proveedor cachea "usá el color de respaldo" el mismo tiempo que un destino
    /// real clasificado como "otro": el color es decorativo, no un dato de negocio, así que no vale la
    /// pena la complejidad de dos TTL distintos por esto.
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);

    /// <summary>Reloj propio, corto: el vendedor está esperando la descarga del PDF, no vale la pena hacerlo esperar por un color.</summary>
    private static readonly TimeSpan ModelTimeout = TimeSpan.FromSeconds(6);

    private readonly IAiAssistantService _assistant;
    private readonly IAiConnectionResolver _connectionResolver;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DestinationPaletteService> _logger;

    public DestinationPaletteService(
        IAiAssistantService assistant,
        IAiConnectionResolver connectionResolver,
        IMemoryCache cache,
        ILogger<DestinationPaletteService> logger)
    {
        _assistant = assistant;
        _connectionResolver = connectionResolver;
        _cache = cache;
        _logger = logger;
    }

    public async Task<string?> ResolveAccentColorHexAsync(
        string? destinationTitle,
        IReadOnlyList<string> cityHints,
        CancellationToken cancellationToken)
    {
        cityHints ??= Array.Empty<string>();

        var cacheKey = BuildCacheKey(destinationTitle, cityHints);
        if (cacheKey is null)
        {
            // Sin destino ni ciudades: no hay nada que clasificar. Fallback directo, sin cachear (no
            // tiene sentido "recordar" una entrada vacía).
            return null;
        }

        if (_cache.TryGetValue(cacheKey, out string? cachedHex))
        {
            return cachedHex;
        }

        // Sin IA usable, ni se arma el prompt (mismo criterio que ServiceLineInterpreter): esta
        // instalación trabaja sin la ayuda, y eso NO se cachea — si el dueño recién cargó la
        // configuración de IA, el próximo PDF tiene que verla andar, no esperar 30 días.
        if (!await _connectionResolver.IsUsableAsync(cancellationToken))
        {
            return null;
        }

        string? resolvedHex;
        try
        {
            var category = await AskModelForCategoryAsync(destinationTitle, cityHints, cancellationToken);
            resolvedHex = category is not null && CategoryAccentHex.TryGetValue(category, out var hex) ? hex : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // El que pidió el PDF cortó la conexión. No es una falla nuestra: se propaga tal cual.
            throw;
        }
        catch (Exception ex)
        {
            // Red de contención final: nada de lo que pase acá puede frenar la emisión del PDF. El color
            // es decorativo — ante cualquier problema, se cae al color de respaldo de la agencia.
            _logger.LogWarning(ex, "Paleta por destino: fallo el proveedor/modelo eligiendo la categoría. Se usa el color de respaldo.");
            resolvedHex = null;
        }

        _cache.Set(cacheKey, resolvedHex, CacheTtl);
        return resolvedHex;
    }

    /// <summary>
    /// Le pide al modelo UNA palabra del set cerrado, con un reloj corto propio. Devuelve la categoría en
    /// minúsculas si es una de las conocidas, o <c>null</c> ante cualquier otra cosa (degradado, "otro",
    /// texto libre que el modelo agregó de más).
    /// </summary>
    private async Task<string?> AskModelForCategoryAsync(
        string? destinationTitle, IReadOnlyList<string> cityHints, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(ModelTimeout);

        var request = BuildRequest(destinationTitle, cityHints);

        AiChatResult result;
        try
        {
            result = await _assistant.CompleteAsync(request, timeoutSource.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // el que pidió el PDF cortó la conexión, no nuestro reloj.
        }
        catch (OperationCanceledException)
        {
            // Salto NUESTRO reloj: el modelo tardó más de lo razonable para elegir UNA palabra.
            _logger.LogInformation("Paleta por destino: el modelo no contestó dentro de {Seconds}s. Se usa el color de respaldo.", ModelTimeout.TotalSeconds);
            return null;
        }

        if (!result.Succeeded)
        {
            _logger.LogInformation("Paleta por destino: la IA no devolvió una respuesta usable. Motivo interno: {Reason}", result.DegradationReason ?? "sin detalle");
            return null;
        }

        return ParseCategory(result.Text);
    }

    /// <summary>
    /// El prompt lleva SOLO el destino y las ciudades de los servicios (gate data-exposure, §5 de la
    /// spec): nada de pasajeros, montos, números de reserva ni ningún otro dato interno.
    /// </summary>
    private static AiChatRequest BuildRequest(string? destinationTitle, IReadOnlyList<string> cityHints)
    {
        var categories = string.Join(", ", CategoryAccentHex.Keys.Append("otro"));

        var systemMessage = AiChatMessage.System(
            "Sos un clasificador simple de destinos de viaje. Vas a recibir el nombre de un destino y, " +
            $"a veces, ciudades relacionadas. Elegí UNA sola palabra de esta lista que mejor lo describa: {categories}. " +
            "Respondé ÚNICAMENTE esa palabra, en minúsculas, sin puntos ni explicaciones. " +
            "Si ninguna categoría encaja bien, respondé \"otro\".");

        var destinationText = string.IsNullOrWhiteSpace(destinationTitle) ? "(sin nombre)" : destinationTitle.Trim();
        var citiesText = cityHints.Count == 0 ? "(sin ciudades adicionales)" : string.Join(", ", cityHints);
        var userMessage = AiChatMessage.User($"Destino: {destinationText}. Ciudades: {citiesText}.");

        var options = new AiProviderOptions
        {
            MaxTokens = 8, // alcanza y sobra para una sola palabra corta.
            Temperature = 0, // clasificación determinista, no redacción creativa.
        };

        return new AiChatRequest(new[] { systemMessage, userMessage }, options);
    }

    /// <summary>Puntuación sobrante que un modelo "prolijo" a veces agrega ("Caribe.", "\"nieve\"") aunque se le pida que no lo haga.</summary>
    private static readonly char[] TrailingPunctuation = { '.', ',', ';', ':', '!', '?', '"', '\'' };

    /// <summary>Deja pasar la respuesta del modelo SOLO si, lavada, es exactamente una de las categorías conocidas.</summary>
    private static string? ParseCategory(string rawText)
    {
        var normalized = TextNormalizer.NormalizeForMatch(rawText).Trim(TrailingPunctuation);
        return normalized.Length > 0 && CategoryAccentHex.ContainsKey(normalized) ? normalized : null;
    }

    /// <summary>
    /// Clave de caché por destino normalizado (spec §5: "trim/lower"). Si no hay NI destino NI ciudades,
    /// no hay nada que clasificar y se devuelve <c>null</c> (el caller no cachea ese caso).
    /// </summary>
    private static string? BuildCacheKey(string? destinationTitle, IReadOnlyList<string> cityHints)
    {
        var normalizedDestination = TextNormalizer.NormalizeForMatch(destinationTitle ?? string.Empty);
        var normalizedCities = string.Join(
            "|", cityHints.Select(city => TextNormalizer.NormalizeForMatch(city)).Where(city => !string.IsNullOrEmpty(city)));

        if (string.IsNullOrEmpty(normalizedDestination) && normalizedCities.Length == 0)
        {
            return null;
        }

        return $"paleta-destino:{normalizedDestination}:{normalizedCities}";
    }
}
