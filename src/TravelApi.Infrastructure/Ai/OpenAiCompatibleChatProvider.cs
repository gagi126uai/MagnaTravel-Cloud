using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Ai;
using TravelApi.Application.Interfaces;

namespace TravelApi.Infrastructure.Ai;

/// <summary>
/// UNICA implementacion de <see cref="IAiChatProvider"/> en F0a. Hace UN POST a
/// <c>{BaseUrl}/chat/completions</c> con el formato OpenAI-compatible (model, messages,
/// opcional response_format) y mapea la respuesta al <see cref="AiChatResult"/> NEUTRO.
///
/// <para>Sirve a cualquier proveedor OpenAI-compatible (el del piloto y otros): la unica
/// diferencia es base_url + api_key + modelo, todo por env. Este archivo NO contiene el
/// nombre de ningun proveedor: es 100% parametrizado por <see cref="AiConnectionOptions"/>.</para>
///
/// <para><b>Resiliencia</b> (mismo espiritu que <c>BnaExchangeRateService</c>): es funcionalidad
/// NO critica, asi que NUNCA tira hacia arriba. Timeout/red/5xx/JSON ilegible -> resultado
/// degradado + warning. 401/403 (config invalida) -> resultado degradado + error claro, SIN
/// reintento (no tiene sentido reintentar una key mala). Los reintentos por contenido y por
/// 429/5xx con backoff los maneja la capa de arriba / F0b; aca solo traducimos un POST.</para>
/// </summary>
public sealed class OpenAiCompatibleChatProvider : IAiChatProvider
{
    private readonly HttpClient _httpClient;
    private readonly AiConnectionOptions _options;
    private readonly ILogger<OpenAiCompatibleChatProvider> _logger;

    // System.Text.Json es case-insensitive aca porque las respuestas OpenAI-compatible
    // usan snake_case en algunos campos (finish_reason) y nosotros mapeamos con
    // [JsonPropertyName] explicito, pero dejamos la tolerancia por las dudas.
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public OpenAiCompatibleChatProvider(
        HttpClient httpClient,
        AiConnectionOptions options,
        ILogger<OpenAiCompatibleChatProvider> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public async Task<AiChatResult> ChatAsync(AiChatRequest request, CancellationToken cancellationToken)
    {
        // Config minima ausente o sin reemplazar -> degradamos sin siquiera intentar el POST.
        // Cubre el caso de un install que prendio el flag sin cargar las variables Ai__*, o
        // que dejo el placeholder CHANGE_THIS_* del .env.example. Cortar aca ahorra un
        // round-trip que volveria 401 igual.
        if (string.IsNullOrWhiteSpace(_options.BaseUrl)
            || string.IsNullOrWhiteSpace(_options.ApiKey)
            || string.IsNullOrWhiteSpace(_options.Model)
            || LooksLikePlaceholder(_options.ApiKey))
        {
            _logger.LogError(
                "Copiloto IA: configuracion incompleta o sin reemplazar (revisar Ai__BaseUrl / " +
                "Ai__ApiKey / Ai__Model). Se degrada la llamada sin contactar al proveedor.");
            return AiChatResult.Degraded("config incompleta");
        }

        try
        {
            return await PostChatAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelacion legitima del caller (p.ej. shutdown): la propagamos, NO la
            // disfrazamos de degradacion, porque no es una falla del proveedor.
            throw;
        }
        catch (TaskCanceledException ex)
        {
            // TaskCanceledException sin cancellation del caller = timeout del HttpClient.
            _logger.LogWarning("Copiloto IA: timeout al llamar al modelo. Motivo: {Message}", ex.Message);
            return AiChatResult.Degraded("timeout");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Copiloto IA: error de red al llamar al modelo. Motivo: {Message}", ex.Message);
            return AiChatResult.Degraded("error de red");
        }
        catch (JsonException ex)
        {
            // El cuerpo de la respuesta no es el JSON que esperamos (envelope OpenAI roto).
            _logger.LogWarning("Copiloto IA: respuesta del modelo ilegible (JSON invalido). Motivo: {Message}", ex.Message);
            return AiChatResult.Degraded("respuesta ilegible");
        }
        catch (FormatException ex)
        {
            // Ai__BaseUrl malformado: BuildChatCompletionsUri tira UriFormatException (deriva
            // de FormatException). Es falla de config del operador, no del proveedor; degradamos
            // para honrar el contrato "nunca tira" de IAiChatProvider.
            _logger.LogError("Copiloto IA: Ai__BaseUrl invalido. Motivo: {Message}", ex.Message);
            return AiChatResult.Degraded("config invalida (base url)");
        }
    }

    /// <summary>
    /// Detecta el placeholder del <c>.env.example</c> (CHANGE_THIS_*) sin reemplazar, para
    /// degradar antes del POST. Misma heuristica que usa el chequeo de secretos del deploy.
    /// </summary>
    private static bool LooksLikePlaceholder(string apiKey) =>
        apiKey.Contains("CHANGE_THIS", StringComparison.OrdinalIgnoreCase);

    private async Task<AiChatResult> PostChatAsync(AiChatRequest request, CancellationToken cancellationToken)
    {
        var requestUri = BuildChatCompletionsUri(_options.BaseUrl);
        var payload = BuildOpenAiPayload(request);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        // La API key viaja como Bearer. OJO: NUNCA loguear este header.
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        // Timeout POR LLAMADA: encadenamos un CTS con el del caller. No tocamos
        // HttpClient.Timeout global porque el client es compartido por la factory.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));

        using var response = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            timeoutCts.Token);

        // Config invalida: la key no sirve. Reintentar no ayuda -> degradamos con error claro.
        // NO logueamos el cuerpo de la respuesta para no arrastrar datos sensibles.
        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            _logger.LogError(
                "Copiloto IA: el proveedor rechazo la credencial (HTTP {StatusCode}). Revisar Ai__ApiKey. " +
                "Se degrada sin reintentar.",
                (int)response.StatusCode);
            return AiChatResult.Degraded($"config invalida ({(int)response.StatusCode})");
        }

        if (!response.IsSuccessStatusCode)
        {
            // 429/5xx/otros: en F0a degradamos directo (el backoff con Retry-After es F0b).
            _logger.LogWarning(
                "Copiloto IA: el proveedor respondio HTTP {StatusCode}. Se degrada la llamada.",
                (int)response.StatusCode);
            return AiChatResult.Degraded($"http {(int)response.StatusCode}");
        }

        var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
        return MapOpenAiResponse(body);
    }

    /// <summary>
    /// Construye la URI <c>{BaseUrl}/chat/completions</c> respetando que BaseUrl puede o no
    /// venir con barra final. Normaliza para no terminar con dobles barras ni faltantes.
    /// </summary>
    private static Uri BuildChatCompletionsUri(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        return new Uri($"{trimmed}/chat/completions");
    }

    /// <summary>
    /// Arma el JSON estilo OpenAI (model + messages + opcionales). Este es el UNICO lugar
    /// del codigo que conoce el formato OpenAI; hacia arriba todo es neutro.
    /// </summary>
    private string BuildOpenAiPayload(AiChatRequest request)
    {
        var messages = new object[request.Messages.Count];
        for (var i = 0; i < request.Messages.Count; i++)
        {
            var message = request.Messages[i];
            messages[i] = new { role = message.Role, content = message.Content };
        }

        // Diccionario para poder omitir campos opcionales (no mandar null hace el payload
        // mas robusto frente a proveedores quisquillosos).
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _options.Model,
            ["messages"] = messages,
            ["max_tokens"] = request.Options.MaxTokens ?? _options.MaxTokens,
        };

        if (request.Options.Temperature.HasValue)
        {
            payload["temperature"] = request.Options.Temperature.Value;
        }

        if (request.Options.RequestJsonObject)
        {
            // response_format json_object: lo soportan los proveedores OpenAI-compatible
            // modernos. Si alguno no lo soporta, igual deserializamos estricto + reintento
            // del lado nuestro (regla dura 6), asi que no dependemos de que se respete.
            payload["response_format"] = new { type = "json_object" };
        }

        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// Mapea el envelope OpenAI (<c>choices[0].message.content</c>, <c>usage</c>,
    /// <c>finish_reason</c>) al <see cref="AiChatResult"/> NEUTRO. Aca muere el formato OpenAI.
    /// </summary>
    private AiChatResult MapOpenAiResponse(string body)
    {
        // JsonException de aca la atrapa ChatAsync y la convierte en degradado "respuesta ilegible".
        var envelope = JsonSerializer.Deserialize<OpenAiChatResponse>(body, ResponseJsonOptions);

        var firstChoice = envelope?.Choices is { Count: > 0 } ? envelope.Choices[0] : null;
        var content = firstChoice?.Message?.Content;

        if (firstChoice == null || content == null)
        {
            // Envelope con forma valida pero sin contenido util (p.ej. choices vacio).
            _logger.LogWarning("Copiloto IA: la respuesta del modelo no trajo contenido en choices[0].message.content.");
            return AiChatResult.Degraded("respuesta sin contenido");
        }

        var finishReason = firstChoice.FinishReason;
        var wasTruncated = string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase);

        return AiChatResult.Success(
            text: content,
            approxTokens: envelope?.Usage?.TotalTokens,
            wasTruncated: wasTruncated,
            rawFinishReason: finishReason);
    }

    // ============================================================
    // DTOs PRIVADOS del envelope OpenAI. Son privados a proposito: el formato OpenAI
    // NO debe filtrarse fuera de este provider (precondicion P1). Si cambia el proveedor
    // a uno no-OpenAI-compatible, se reemplaza este mapeo, no el contrato neutro.
    // ============================================================

    private sealed class OpenAiChatResponse
    {
        [JsonPropertyName("choices")]
        public List<OpenAiChoice>? Choices { get; set; }

        [JsonPropertyName("usage")]
        public OpenAiUsage? Usage { get; set; }
    }

    private sealed class OpenAiChoice
    {
        [JsonPropertyName("message")]
        public OpenAiMessage? Message { get; set; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; set; }
    }

    private sealed class OpenAiMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }

    private sealed class OpenAiUsage
    {
        [JsonPropertyName("total_tokens")]
        public int? TotalTokens { get; set; }
    }
}
