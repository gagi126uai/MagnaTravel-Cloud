namespace TravelApi.Application.Ai;

/// <summary>
/// Opciones de una llamada concreta al modelo. Vocabulario NEUTRO: el provider
/// las traduce a los campos que su API espere (en OpenAI-compatible:
/// <c>max_tokens</c>, <c>temperature</c>, <c>response_format</c>).
///
/// <para>Son opciones POR LLAMADA, no la config global del proveedor (esa vive en
/// <see cref="AiConnectionOptions"/>, leida de variables de entorno). Aca van los
/// parametros que cambian segun el caso de uso.</para>
/// </summary>
public sealed class AiProviderOptions
{
    /// <summary>
    /// Tope de tokens de la respuesta. <c>null</c> = usar el default del provider
    /// (que a su vez sale de <c>Ai__MaxTokens</c>). Sirve para acotar costo y evitar
    /// respuestas verborragicas.
    /// </summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// Creatividad del modelo (0 = determinista, mas alto = mas variado). Para
    /// tareas de redaccion controlada conviene bajo. <c>null</c> = default del provider.
    /// </summary>
    public double? Temperature { get; init; }

    /// <summary>
    /// Si <c>true</c>, se le pide al proveedor salida en formato JSON (cuando lo
    /// soporta, via <c>response_format</c>). NO reemplaza la validacion estricta del
    /// lado nuestro: el modelo puede ignorar el pedido, por eso igual deserializamos
    /// en modo estricto + reintento (regla dura 6 del ADR-016 §2.5).
    /// </summary>
    public bool RequestJsonObject { get; init; }
}
