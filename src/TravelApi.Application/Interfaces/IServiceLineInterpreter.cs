using System.Threading;
using System.Threading.Tasks;
using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// "La linea inteligente" (spec firmada 2026-08-07 §3, dependencias M-20 a M-23 y M-27): recibe la
/// frase que escribio el vendedor y devuelve el servicio ya interpretado para precargar la ficha.
///
/// <para><b>Contrato de errores</b>: NUNCA lanza por una falla de la inteligencia artificial. Sin
/// configuracion, con el proveedor caido, con una respuesta ilegible o con demora, devuelve
/// <see cref="ServiceLineInterpretationDto.NotInterpreted"/>. La ayuda enriquece, jamas bloquea la
/// carga de un servicio.</para>
/// </summary>
public interface IServiceLineInterpreter
{
    /// <summary>
    /// Interpreta la frase libre. <paramref name="serviceType"/> es la solapa ya elegida por el
    /// vendedor ("Hotel", "Aereo", ...): define que se busca y que variante tiene sentido.
    /// </summary>
    Task<ServiceLineInterpretationDto> InterpretAsync(
        string? freeText,
        string? serviceType,
        CancellationToken cancellationToken);
}
