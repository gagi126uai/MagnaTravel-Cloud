using System.Threading;
using System.Threading.Tasks;
using TravelApi.Application.Ai;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// Abstraccion de BAJO NIVEL del modelo: hace UN turno de chat y devuelve el resultado crudo.
/// Deliberadamente "tonto" — no sabe de dominio, no arma prompts, no valida JSON de negocio,
/// no reintenta por contenido. Solo: recibe mensajes + opciones, hace UN POST al modelo,
/// mapea la respuesta al <see cref="AiChatResult"/> NEUTRO.
///
/// <para>Es el "unico punto de integracion con el modelo" que pide la vision (ADR-016 §2.1).
/// Su unica implementacion en F0a es <c>OpenAiCompatibleChatProvider</c> (Infrastructure).
/// Si manana aparece un proveedor que NO es OpenAI-compatible, se agrega una 2da
/// implementacion DETRAS de esta misma interfaz, sin tocar a los consumidores.</para>
///
/// <para><b>Contrato de errores</b>: NUNCA tira excepcion hacia arriba por una falla de IA
/// (timeout, red, 401/403, 5xx, JSON ilegible). Devuelve <see cref="AiChatResult.Degraded"/>.
/// La IA enriquece, nunca bloquea (mismo espiritu que <c>BnaExchangeRateService</c>).</para>
/// </summary>
public interface IAiChatProvider
{
    /// <summary>
    /// Ejecuta un turno de chat. Devuelve un resultado exitoso o degradado; no lanza
    /// excepciones por fallas del proveedor.
    /// </summary>
    Task<AiChatResult> ChatAsync(AiChatRequest request, CancellationToken cancellationToken);
}
