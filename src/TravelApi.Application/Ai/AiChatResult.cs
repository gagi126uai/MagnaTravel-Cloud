namespace TravelApi.Application.Ai;

/// <summary>
/// Resultado de UN turno de chat, en vocabulario NEUTRO (precondicion P1 del ADR-016 §0bis).
///
/// <para><b>Por que NEUTRO y no el formato OpenAI</b>: hoy hablamos con un proveedor
/// OpenAI-compatible (la respuesta trae <c>choices[0].message.content</c>, <c>usage</c>,
/// <c>finish_reason</c>...). Pero los consumidores (modulos de dominio) NO deben ver ese
/// formato: si manana cambiamos a un proveedor que NO es OpenAI-compatible, queremos tocar
/// SOLO el provider en Infrastructure, no a cada consumidor. Por eso el mapeo del JSON
/// estilo OpenAI a este objeto vive DENTRO de <c>OpenAiCompatibleChatProvider</c>.</para>
///
/// <para><b>Degradacion elegante</b>: igual que <c>BnaExchangeRateService</c>, una falla
/// (timeout, red, config invalida, JSON ilegible) NO se propaga como excepcion hacia arriba.
/// El provider/servicio devuelve un resultado con <see cref="Succeeded"/> = false y
/// <see cref="Text"/> vacio. El consumidor decide que hacer (en el piloto: mostrar la alerta
/// sin la frase IA). La IA enriquece, nunca bloquea.</para>
/// </summary>
public sealed class AiChatResult
{
    /// <summary>
    /// Texto que devolvio el modelo. <c>string.Empty</c> cuando <see cref="Succeeded"/>
    /// es false (degradado). Nunca <c>null</c>, para que el consumidor no tenga que
    /// chequear null antes de usarlo.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Indica si la llamada produjo una respuesta utilizable. <c>false</c> = degradado
    /// (timeout, red caida, config invalida, respuesta ilegible). El consumidor debe
    /// caer al comportamiento sin IA cuando esto es false.
    /// </summary>
    public bool Succeeded { get; }

    /// <summary>
    /// Tokens aproximados consumidos por la llamada (si el proveedor los informa).
    /// <c>null</c> si no vinieron. Es aproximado a proposito: distintos proveedores
    /// cuentan distinto; sirve para medir consumo, no para facturar al centavo.
    /// </summary>
    public int? ApproxTokens { get; }

    /// <summary>
    /// <c>true</c> si el modelo corto la respuesta por limite de tokens (en OpenAI:
    /// <c>finish_reason == "length"</c>). Util para saber que el texto puede estar
    /// incompleto.
    /// </summary>
    public bool WasTruncated { get; }

    /// <summary>
    /// Motivo de finalizacion crudo y OPACO que informo el proveedor (en OpenAI:
    /// <c>finish_reason</c>, p.ej. <c>"stop"</c>, <c>"length"</c>). Se expone como
    /// string sin interpretar para diagnostico/logging; los consumidores NO deben
    /// ramificar logica de dominio sobre este valor (para eso esta <see cref="WasTruncated"/>).
    /// <c>null</c> si el proveedor no lo informo o la llamada degrado.
    /// </summary>
    public string? RawFinishReason { get; }

    /// <summary>
    /// Mensaje de diagnostico (seguro, SIN secretos) cuando la llamada degrado:
    /// p.ej. "timeout", "config invalida (401)", "respuesta ilegible". <c>null</c>
    /// en el camino feliz. NO se muestra al usuario final; es para logs/auditoria.
    /// </summary>
    public string? DegradationReason { get; }

    private AiChatResult(
        string text,
        bool succeeded,
        int? approxTokens,
        bool wasTruncated,
        string? rawFinishReason,
        string? degradationReason)
    {
        Text = text;
        Succeeded = succeeded;
        ApproxTokens = approxTokens;
        WasTruncated = wasTruncated;
        RawFinishReason = rawFinishReason;
        DegradationReason = degradationReason;
    }

    /// <summary>Construye un resultado exitoso con el texto del modelo.</summary>
    public static AiChatResult Success(
        string text,
        int? approxTokens = null,
        bool wasTruncated = false,
        string? rawFinishReason = null)
        => new(text ?? string.Empty, succeeded: true, approxTokens, wasTruncated, rawFinishReason, degradationReason: null);

    /// <summary>
    /// Construye un resultado DEGRADADO (la IA fallo de forma controlada). El consumidor
    /// debe caer al comportamiento sin IA. El <paramref name="reason"/> NUNCA debe contener
    /// secretos (API key) ni datos sensibles.
    /// </summary>
    public static AiChatResult Degraded(string reason)
        => new(string.Empty, succeeded: false, approxTokens: null, wasTruncated: false, rawFinishReason: null, degradationReason: reason);
}
