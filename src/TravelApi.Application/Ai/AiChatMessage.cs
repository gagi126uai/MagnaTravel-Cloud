namespace TravelApi.Application.Ai;

/// <summary>
/// Un turno de conversacion con el modelo. Vocabulario NEUTRO (no atado a ningun
/// proveedor): un rol ("system" / "user" / "assistant") + el texto del mensaje.
///
/// <para>Existe para que los consumidores armen la conversacion sin conocer el
/// formato del proveedor. La traduccion a JSON estilo OpenAI vive DENTRO de
/// <c>OpenAiCompatibleChatProvider</c> (Infrastructure), no aca. Si manana el
/// proveedor deja de ser OpenAI-compatible, este contrato no cambia.</para>
/// </summary>
public sealed class AiChatMessage
{
    /// <summary>
    /// Rol del emisor del mensaje. Valores tipicos: <c>"system"</c> (instrucciones),
    /// <c>"user"</c> (pedido), <c>"assistant"</c> (respuesta previa del modelo).
    /// Es un string libre a proposito: el provider lo mapea a lo que su API espere.
    /// </summary>
    public string Role { get; }

    /// <summary>Contenido textual del mensaje.</summary>
    public string Content { get; }

    public AiChatMessage(string role, string content)
    {
        Role = role;
        Content = content;
    }

    /// <summary>Atajo para el mensaje de sistema (instrucciones / contrato de salida).</summary>
    public static AiChatMessage System(string content) => new("system", content);

    /// <summary>Atajo para el mensaje del usuario (el pedido concreto).</summary>
    public static AiChatMessage User(string content) => new("user", content);

    /// <summary>Atajo para una respuesta previa del modelo (util para few-shot a futuro).</summary>
    public static AiChatMessage Assistant(string content) => new("assistant", content);
}
