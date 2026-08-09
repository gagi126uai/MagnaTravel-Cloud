using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

namespace TravelApi.Controllers;

/// <summary>
/// "Configuracion → Inteligencia artificial" (spec firmada 2026-08-07 §15).
///
/// <para><b>Solo Admin, leer y escribir.</b> Un vendedor no ve la solapa y tampoco puede llegar a
/// estos endpoints por afuera de la pantalla: la puerta esta del lado del servidor, no del navegador.
/// Es la misma puerta que usa el resto de Configuracion (<c>[Authorize(Roles = "Admin")]</c>).</para>
///
/// <para><b>La clave nunca sale por aca.</b> El GET devuelve "hay clave sí/no", de donde sale y sus
/// primeros 4 caracteres. No hay ningun endpoint que devuelva la clave entera: no existe y no se
/// agrega.</para>
/// </summary>
[ApiController]
[Route("api/settings/ai")]
[Authorize(Roles = "Admin")]
public class AiSettingsController : ControllerBase
{
    private readonly IAiSettingsService _aiSettingsService;

    public AiSettingsController(IAiSettingsService aiSettingsService)
    {
        _aiSettingsService = aiSettingsService;
    }

    /// <summary>La foto de la configuracion actual. Sin la clave (M-28, write-only).</summary>
    [HttpGet]
    public async Task<ActionResult<AiSettingsDto>> Get(CancellationToken cancellationToken)
    {
        return Ok(await _aiSettingsService.GetAsync(cancellationToken));
    }

    /// <summary>
    /// La lista de proveedores con su bajada y sus valores recomendados (M-32). Sale del motor para
    /// que sumar uno nuevo manana no obligue a tocar la pantalla.
    /// </summary>
    [HttpGet("providers")]
    public ActionResult<AiProviderPresetsResponse> GetProviders()
    {
        return Ok(_aiSettingsService.GetProviderPresets());
    }

    /// <summary>
    /// Guarda proveedor, direccion, modelo y (si vino) la clave nueva, cifrada. Queda registrado
    /// quien guardo y cuando.
    /// </summary>
    [HttpPut]
    public async Task<ActionResult<AiSettingsDto>> Update(
        [FromBody] UpdateAiSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var userName = User.FindFirstValue(ClaimTypes.Name);

        var updated = await _aiSettingsService.UpdateAsync(request, userId, userName, cancellationToken);
        return Ok(updated);
    }

    /// <summary>
    /// Prueba la conexion con lo que hay en pantalla, este guardado o no (M-31). Devuelve un codigo
    /// de resultado y cuanto tardo; nunca el mensaje del proveedor.
    ///
    /// <para><b>Tope de intentos</b>: este endpoint hace que el servidor le pegue a una direccion
    /// que escribe el usuario, asi que va limitado con la misma maquinaria de topes que ya usa el
    /// resto de la API (politica "ai-test" en Program.cs). Sumado a que solo Admin entra y a que la
    /// direccion tiene que ser publica y en https, deja de servir como sonda.</para>
    /// </summary>
    [HttpPost("test-connection")]
    [EnableRateLimiting("ai-test")]
    public async Task<ActionResult<AiConnectionTestResultDto>> TestConnection(
        [FromBody] TestAiConnectionRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _aiSettingsService.TestConnectionAsync(request, cancellationToken));
    }
}
