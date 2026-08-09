namespace TravelApi.Application.Ai;

/// <summary>
/// Los datos con los que se habla con la IA: direccion, clave y modelo (mas tiempos y topes).
///
/// <para><b>De donde salen</b> (adenda firmada a ADR-016, 2026-08-07 §11): los arma
/// <c>IAiConnectionResolver</c> en CADA llamada. Primero mira lo que el dueño cargo en
/// "Configuracion → Inteligencia artificial" (base, con la clave cifrada); si ahi no hay una
/// configuracion completa, usa como RESPALDO las variables de entorno <c>Ai__*</c> que se leen en
/// <c>Program.cs</c> con el patron del repo (<c>["Ai:X"] ?? ["Ai__X"]</c>).</para>
///
/// <para><b>Los tres van juntos a proposito</b>: son inseparables (la direccion de un proveedor con
/// la clave de otro no funciona). Por eso se toman los tres del mismo lado, nunca mezclados.</para>
///
/// <para>La <see cref="ApiKey"/> NUNCA se loguea ni sale por ninguna API.</para>
/// </summary>
public sealed class AiConnectionOptions
{
    /// <summary>
    /// URL base del endpoint OpenAI-compatible (SIN el sufijo <c>/chat/completions</c>,
    /// que lo agrega el provider). Default en <c>.env.example</c> apunta al endpoint
    /// OpenAI-compatible del proveedor del piloto. Cambia por proveedor.
    /// </summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// La clave del proveedor, EN CLARO y solo en memoria. SECRETO: nunca se loguea, nunca sale por
    /// la API. Cuando viene de la pantalla, en la base esta guardada CIFRADA. Si esta vacia, no hay
    /// IA y el sistema funciona igual, sin las ayudas.
    /// </summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// Nombre del modelo. VOLATIL: los modelos del free tier cambian de nombre seguido,
    /// por eso es config y no constante. Se confirma al configurar el install.
    /// </summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>Timeout por llamada HTTP al modelo, en segundos. Default 15 (ADR-016 §2.4).</summary>
    public int TimeoutSeconds { get; init; } = 15;

    /// <summary>
    /// Tope de tokens de la respuesta por defecto (cuando la llamada no especifica uno).
    /// Acota costo y largo de salida.
    /// </summary>
    public int MaxTokens { get; init; } = 512;

    /// <summary>
    /// Maximo de reintentos. Default 2 a nivel config, pero en F0a el
    /// <c>AiAssistantService</c> usa SOLO 1 reintento (timeout / JSON invalido). Los
    /// reintentos completos (429 con Retry-After, 5xx con backoff) son F0b.
    /// </summary>
    public int MaxRetries { get; init; } = 2;
}
