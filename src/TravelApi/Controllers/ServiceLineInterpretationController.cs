using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

/// <summary>
/// "La linea inteligente" de la ficha de carga de servicio (spec firmada 2026-08-07 §3, M-20).
///
/// <para><b>Que hace</b>: recibe la frase que escribio el vendedor ("sheraton iguazu doble desayuno
/// ola 48 usd del 12 al 15/9") y devuelve el servicio interpretado para precargar la ficha en
/// amarillo. Todo lo que el sistema no entendio viaja vacio y la ficha lo deja en blanco.</para>
///
/// <para><b>Quien puede llamarlo</b>: exactamente el mismo permiso que hace falta para guardar un
/// servicio en esa reserva — <c>reservas.edit</c> mas ser el responsable de la reserva (o tener
/// <c>reservas.view_all</c>). No es un endpoint de administrador: cualquier vendedor carga servicios.
/// La reserva va en la direccion justamente porque es la que define ese permiso.</para>
///
/// <para><b>Nunca devuelve un error de inteligencia artificial</b>: sin configuracion, con el
/// proveedor caido o con demora, contesta 200 con <c>interpreted=false</c> y la pantalla sigue con el
/// buscador de siempre (M-23 / §3.5). Ni un cartel, ni un codigo, ni la palabra "IA".</para>
/// </summary>
[ApiController]
[Route("api/reservas/{reservaId}/linea-inteligente")]
[Authorize]
public class ServiceLineInterpretationController : ControllerBase
{
    private readonly IServiceLineInterpreter _interpreter;

    public ServiceLineInterpretationController(IServiceLineInterpreter interpreter)
    {
        _interpreter = interpreter;
    }

    /// <summary>
    /// Interpreta la frase libre y devuelve el servicio armado para precargar.
    ///
    /// <para><b>Tope de llamadas</b>: cada pedido gasta cuota del proveedor de inteligencia
    /// artificial, asi que va limitado por usuario con la misma maquinaria de topes que el resto de la
    /// API (politica "ai-line" en Program.cs). El tope es holgado para escribir normal y frena que la
    /// caja se convierta en una canilla abierta.</para>
    /// </summary>
    [HttpPost]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, "reservaId", bypassPermission: Permissions.ReservasViewAll)]
    [EnableRateLimiting("ai-line")]
    public async Task<ActionResult<ServiceLineInterpretationDto>> Interpret(
        string reservaId,
        [FromBody] InterpretServiceLineRequest request,
        CancellationToken ct)
    {
        // El controller queda fino a proposito: toda la decision (que se entiende, que se descarta,
        // que se pregunta) vive en el motor, que es donde se puede testear (T-13).
        var interpretation = await _interpreter.InterpretAsync(request.Text, request.ServiceType, ct);
        return Ok(interpretation);
    }
}
