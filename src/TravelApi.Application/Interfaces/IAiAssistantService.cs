using System.Threading;
using System.Threading.Tasks;
using TravelApi.Application.Ai;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// Orquestacion de ALTO NIVEL del cerebro: es el unico servicio que consumen los modulos
/// de dominio (alertas, futuro chat, futuro "ayudame con esto"). Nunca llaman al
/// <see cref="IAiChatProvider"/> crudo.
///
/// <para><b>Que hace en F0a</b> (acotado a proposito): arma la request, llama al provider,
/// y para el helper tipado deserializa estricto con 1 reintento y degrada elegante. NADA de
/// few-shot, breaker, ni metricas persistentes todavia (eso es F0b/F3).</para>
///
/// <para>La interfaz YA preve el futuro (los modulos no la van a tener que cambiar cuando
/// lleguen few-shot/auditoria), pero la implementacion de F0a no paga por eso aun.</para>
/// </summary>
public interface IAiAssistantService
{
    /// <summary>
    /// Completa un turno de chat libre (texto). Pasa la request al provider y devuelve el
    /// resultado NEUTRO. Degrada elegante (resultado con <c>Succeeded=false</c>) ante falla;
    /// no lanza por errores de IA.
    /// </summary>
    Task<AiChatResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Completa un turno PIDIENDO salida estructurada y la deserializa estrictamente a
    /// <typeparamref name="T"/>. Si el modelo devuelve JSON invalido o que no cumple el
    /// contrato, hace UN reintento con instruccion reforzada ("responde SOLO JSON valido").
    /// Si vuelve a fallar, degrada: devuelve <c>(default, AiChatResult.Degraded)</c> y el
    /// consumidor cae al comportamiento sin IA. NUNCA inventa contenido ni lanza por
    /// salida invalida.
    ///
    /// <para>Devuelve una tupla: el valor deserializado (<c>null</c>/default si degrado) y el
    /// <see cref="AiChatResult"/> con la metadata de la llamada (tokens, exito/degradacion).
    /// El consumidor chequea <c>result.Succeeded</c> antes de usar <c>value</c>.</para>
    /// </summary>
    Task<(T? Value, AiChatResult Result)> CompleteStructuredAsync<T>(
        AiChatRequest request,
        CancellationToken cancellationToken) where T : class;
}
