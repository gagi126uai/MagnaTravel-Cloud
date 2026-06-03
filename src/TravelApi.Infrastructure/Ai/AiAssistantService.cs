using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Ai;
using TravelApi.Application.Interfaces;

namespace TravelApi.Infrastructure.Ai;

/// <summary>
/// Orquestacion de ALTO NIVEL del cerebro (F0a). Es el unico punto que consumen los
/// modulos de dominio. Por encima del <see cref="IAiChatProvider"/> agrega:
/// deserializacion estricta + 1 reintento con instruccion reforzada + degradacion elegante.
///
/// <para><b>Alcance F0a</b>: SIN few-shot, SIN circuit breaker, SIN metricas persistentes
/// (eso es F0b/F3). El metodo <see cref="CompleteAsync"/> es un passthrough con degradacion;
/// <see cref="CompleteStructuredAsync"/> agrega el ciclo "deserializa estricto -> si falla,
/// 1 reintento reforzado -> si vuelve a fallar, degrada".</para>
/// </summary>
public sealed class AiAssistantService : IAiAssistantService
{
    private readonly IAiChatProvider _provider;
    private readonly ILogger<AiAssistantService> _logger;

    /// <summary>
    /// Deserializacion ESTRICTA (regla dura 6 del ADR-016): rechaza propiedades desconocidas
    /// en el objeto critico. Asi, si el modelo "alucina" campos extra, el parseo falla y
    /// disparamos el reintento/degradacion en vez de aceptar basura silenciosamente.
    /// Case-insensitive para tolerar variaciones de mayusculas del modelo.
    /// </summary>
    private static readonly JsonSerializerOptions StrictJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    public AiAssistantService(
        IAiChatProvider provider,
        ILogger<AiAssistantService> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    public Task<AiChatResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken)
    {
        // F0a: passthrough directo al provider. El provider ya degrada elegante (nunca tira),
        // asi que aca no hay nada extra que envolver para el caso de texto libre.
        return _provider.ChatAsync(request, cancellationToken);
    }

    public async Task<(T? Value, AiChatResult Result)> CompleteStructuredAsync<T>(
        AiChatRequest request,
        CancellationToken cancellationToken) where T : class
    {
        // Primer intento: tal cual vino la request del consumidor.
        var firstResult = await _provider.ChatAsync(request, cancellationToken);
        if (firstResult.Succeeded && TryDeserializeStrict<T>(firstResult.Text, out var firstValue))
        {
            return (firstValue, firstResult);
        }

        // Si la llamada degrado (timeout/red/config), NO reintentamos: el problema no es el
        // contenido, es la conexion/config, y un reintento inmediato gastaria cuota/latencia
        // sin chance de mejorar. Degradamos directo. (El backoff real de 429/5xx es F0b.)
        if (!firstResult.Succeeded)
        {
            return (null, firstResult);
        }

        // Llego respuesta pero el JSON no valida: UN reintento con instruccion reforzada.
        _logger.LogWarning(
            "Copiloto IA: la salida estructurada no deserializo a {Type} en el primer intento. " +
            "Se reintenta una vez con instruccion reforzada.",
            typeof(T).Name);

        // TODO F0b: este reintento es INMEDIATO (sin backoff). Hoy es inofensivo (un solo
        // caller futuro = el job, una llamada extra por item). Antes de sumar el caller del
        // job en lote (F1), agregar backoff / respetar Retry-After para no duplicar consumo
        // de cuota ante un modelo verborragico.
        var reinforcedRequest = BuildReinforcedRequest(request);
        var secondResult = await _provider.ChatAsync(reinforcedRequest, cancellationToken);
        if (secondResult.Succeeded && TryDeserializeStrict<T>(secondResult.Text, out var secondValue))
        {
            return (secondValue, secondResult);
        }

        // Segundo intento tambien fallo (degradado o JSON invalido de nuevo): degradamos.
        // NO inventamos contenido: el consumidor cae al comportamiento sin IA.
        _logger.LogWarning(
            "Copiloto IA: la salida estructurada no deserializo a {Type} tras el reintento. Se degrada.",
            typeof(T).Name);
        return (null, AiChatResult.Degraded("salida estructurada invalida"));
    }

    /// <summary>
    /// Deserializa estricto y valida que no haya quedado null. Devuelve false (sin lanzar)
    /// ante cualquier problema, para que el caller dispare el reintento/degradacion.
    /// </summary>
    private bool TryDeserializeStrict<T>(string text, out T? value) where T : class
    {
        value = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // Algunos modelos envuelven el JSON en un bloque ```json ... ```. Lo limpiamos
        // defensivamente antes de parsear; si no hay fences, el texto queda igual.
        var candidate = StripCodeFences(text);

        try
        {
            value = JsonSerializer.Deserialize<T>(candidate, StrictJsonOptions);
            return value != null;
        }
        catch (JsonException)
        {
            // JSON malformado o con propiedades no mapeadas (modo estricto). No es un error
            // que deba propagarse: es justamente la senal para reintentar/degradar.
            return false;
        }
    }

    /// <summary>
    /// Quita un bloque markdown de codigo (```json ... ``` o ``` ... ```) si el modelo lo
    /// agrego alrededor del JSON. Heuristica defensiva, no validacion de contenido.
    /// </summary>
    private static string StripCodeFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        // Sacamos la primera linea (```json o ```) y el cierre ``` final.
        var firstNewLine = trimmed.IndexOf('\n');
        if (firstNewLine < 0)
        {
            return trimmed;
        }

        var withoutOpening = trimmed[(firstNewLine + 1)..];
        var closingIndex = withoutOpening.LastIndexOf("```", StringComparison.Ordinal);
        return closingIndex >= 0
            ? withoutOpening[..closingIndex].Trim()
            : withoutOpening.Trim();
    }

    /// <summary>
    /// Construye una request reforzada agregando un mensaje de sistema que insiste en
    /// "responde SOLO con JSON valido, sin texto extra ni markdown". Reusa los mensajes
    /// originales para no perder el contexto del pedido.
    /// </summary>
    private static AiChatRequest BuildReinforcedRequest(AiChatRequest original)
    {
        var reinforcedMessages = new List<AiChatMessage>(original.Messages.Count + 1)
        {
            AiChatMessage.System(
                "IMPORTANTE: tu respuesta anterior no era JSON valido. Responde UNICAMENTE con un " +
                "objeto JSON valido que cumpla exactamente el formato pedido. No agregues texto, " +
                "explicaciones ni bloques de codigo markdown."),
        };
        reinforcedMessages.AddRange(original.Messages);

        // Forzamos response_format json_object en el reintento (si el proveedor lo soporta,
        // mejora la chance; si no, igual validamos estricto del lado nuestro).
        var reinforcedOptions = new AiProviderOptions
        {
            MaxTokens = original.Options.MaxTokens,
            Temperature = original.Options.Temperature,
            RequestJsonObject = true,
        };

        return new AiChatRequest(reinforcedMessages, reinforcedOptions);
    }
}
