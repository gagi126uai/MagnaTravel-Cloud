namespace TravelApi.Application.Ai;

/// <summary>
/// Configuracion GLOBAL del proveedor de IA, leida de variables de entorno (NUNCA de la DB).
///
/// <para><b>Por que en env y no en la DB</b> (ADR-016 §2.2): la <see cref="ApiKey"/> es un
/// secreto y sigue el mismo manejo que JWT_KEY / MinIO / RabbitMQ (env + validado en deploy,
/// nunca al repo ni a backups de DB en claro). <see cref="BaseUrl"/> y <see cref="Model"/>
/// van junto a la key a proposito: son inseparables (un base_url de un proveedor con la key
/// de otro no funciona). Tenerlos juntos hace que "cambiar de proveedor" sea una operacion
/// atomica de editar .env + restart, sin estados incoherentes.</para>
///
/// <para>Estos valores se leen en <c>Program.cs</c> con el patron del repo
/// (<c>["Ai:X"] ?? ["Ai__X"]</c>) y se inyectan al provider. NO se loguea la <see cref="ApiKey"/>.</para>
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
    /// API key del proveedor. SECRETO. Solo por env. NUNCA se loguea ni se persiste.
    /// Si esta vacia con el copiloto prendido, el provider degrada con "config invalida".
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
