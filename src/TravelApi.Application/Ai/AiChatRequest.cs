using System.Collections.Generic;

namespace TravelApi.Application.Ai;

/// <summary>
/// Pedido de UN turno de chat al cerebro: la lista de mensajes (system/user/...) +
/// las opciones de esa llamada. Vocabulario NEUTRO; no conoce el formato del proveedor.
///
/// <para>Lo arma el consumidor de alto nivel (en F0a, directamente los tests; en F1,
/// el mapper datos -> prompt del piloto). El provider lo traduce a su payload.</para>
/// </summary>
public sealed class AiChatRequest
{
    /// <summary>
    /// Mensajes de la conversacion, en orden. Tipicamente: un <c>system</c> con las
    /// instrucciones + contrato de salida, seguido de un <c>user</c> con el pedido.
    /// </summary>
    public IReadOnlyList<AiChatMessage> Messages { get; }

    /// <summary>Opciones de esta llamada (tokens, temperatura, pedir JSON).</summary>
    public AiProviderOptions Options { get; }

    public AiChatRequest(IReadOnlyList<AiChatMessage> messages, AiProviderOptions? options = null)
    {
        Messages = messages;
        Options = options ?? new AiProviderOptions();
    }
}
