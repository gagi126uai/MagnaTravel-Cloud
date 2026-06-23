using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

/// <summary>
/// Endpoints transversales de pasajeros (no atados a una reserva puntual). Hoy solo la busqueda de
/// pasajeros historicos para la "ficha de pasajero reutilizable". La gestion de pasajeros DENTRO de una
/// reserva (alta/edicion/baja, asignaciones) sigue en ReservasController bajo ownership por reserva.
/// </summary>
[ApiController]
[Route("api/passengers")]
[Authorize]
public class PassengersController : ControllerBase
{
    private readonly IPassengerSearchService _passengerSearchService;

    public PassengersController(IPassengerSearchService passengerSearchService)
    {
        _passengerSearchService = passengerSearchService;
    }

    /// <summary>
    /// Busca pasajeros ya cargados (de cualquier reserva) por documento exacto y/o nombre parcial para
    /// autocompletar el formulario al cargar un pasajero. Devuelve UNA fila por persona (deduplicada) y
    /// solo sus datos de identidad — NO expone numeros de reserva ni nada de los viajes ajenos.
    /// </summary>
    // Permiso: reservas.view, el mismo que ya gatea leer pasajeros (GET .../passengers en
    // ReservasController). Esta busqueda es un lookup GLOBAL de identidad (no de una reserva concreta),
    // asi que NO lleva ownership por reserva; la proteccion es no exponer dato alguno de las reservas.
    [HttpGet("search-similar")]
    [RequirePermission(Permissions.ReservasView)]
    public async Task<ActionResult<IReadOnlyList<PassengerSimilarMatchDto>>> SearchSimilar(
        [FromQuery] string? fullName,
        [FromQuery] string? documentType,
        [FromQuery] string? documentNumber,
        [FromQuery] int take,
        CancellationToken cancellationToken)
    {
        var matches = await _passengerSearchService.SearchSimilarAsync(
            fullName, documentType, documentNumber, take, cancellationToken);
        return Ok(matches);
    }
}
