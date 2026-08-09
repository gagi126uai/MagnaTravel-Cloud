using System.Threading;
using System.Threading.Tasks;
using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// Los tres datos con los que se prueba una conexion. Pueden venir de la pantalla sin estar
/// guardados todavia: la gracia de "Probar conexion" es justamente probar ANTES de romper lo que
/// funcionaba (§15.4 de la spec firmada 2026-08-07).
/// </summary>
/// <param name="BaseUrl">Direccion a probar.</param>
/// <param name="ApiKey">Clave EN CLARO, solo en memoria y solo para esta llamada. No se loguea.</param>
/// <param name="Model">Modelo a probar.</param>
public sealed record AiConnectionProbe(string? BaseUrl, string? ApiKey, string? Model);

/// <summary>
/// Manda un saludo minimo al proveedor y traduce lo que pase a UN codigo de resultado + cuanto
/// tardo (M-31). <b>Nunca</b> devuelve el texto crudo del proveedor ni detalles tecnicos.
/// </summary>
public interface IAiConnectionTester
{
    Task<AiConnectionTestResultDto> TestAsync(AiConnectionProbe probe, CancellationToken cancellationToken);
}
