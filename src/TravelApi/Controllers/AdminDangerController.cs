using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

/// <summary>
/// Obra "Empezar de cero" (2026-07-27): endpoints para el borrado masivo e irreversible de TODOS los datos
/// de negocio cargados (reservas, clientes, proveedores, facturas, catálogo, etc.), con backup previo
/// obligatorio. Es la operación mas destructiva del sistema, por eso el gate es doble a propósito: rol Admin
/// (bypass de todo permiso) Y ADEMÁS el permiso explícito <see cref="Permissions.ConfiguracionDataWipe"/>
/// (defensa en profundidad — si el día de mañana el bypass de rol cambia, el permiso solo igual protege).
/// La lógica de negocio completa vive en <see cref="ISystemDataWipeService"/> (Infrastructure): este
/// controller queda deliberadamente fino.
/// </summary>
[ApiController]
[Route("api/admin/danger")]
[Authorize(Roles = "Admin")]
[RequirePermission(Permissions.ConfiguracionDataWipe)]
public class AdminDangerController : ControllerBase
{
    private readonly ISystemDataWipeService _wipeService;
    private readonly ILogger<AdminDangerController> _logger;

    public AdminDangerController(ISystemDataWipeService wipeService, ILogger<AdminDangerController> logger)
    {
        _wipeService = wipeService;
        _logger = logger;
    }

    /// <summary>
    /// SOLO LECTURA: conteos actuales por grupo + si el candado fiscal está activo (y por qué) + el nombre
    /// estimado que tendría el archivo de backup si se ejecuta el borrado ahora mismo. No cambia nada.
    /// </summary>
    [HttpGet("wipe/preview")]
    public async Task<ActionResult<SystemDataWipePreviewResponse>> GetWipePreview(CancellationToken ct)
    {
        var preview = await _wipeService.GetPreviewAsync(ct);
        return Ok(preview);
    }

    /// <summary>
    /// Ejecuta el borrado real. Devuelve 409 con un mensaje en castellano si la frase no coincide, la
    /// contraseña es incorrecta, el candado fiscal está activo, o el backup previo falló — en todos esos
    /// casos NO se borró nada.
    /// </summary>
    [HttpPost("wipe")]
    public async Task<ActionResult<SystemDataWipeResponse>> Wipe(
        [FromBody] SystemDataWipeRequest request,
        CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        try
        {
            var result = await _wipeService.ExecuteWipeAsync(
                userId,
                request.Password,
                request.Phrase,
                request.IncluirConfiguracion,
                ct);
            return Ok(result);
        }
        catch (SystemDataWipeRefusedException ex)
        {
            // El mensaje YA viene en criollo desde el service (frase, contraseña o candado fiscal). No se
            // filtra nada tecnico: es exactamente lo que el usuario tiene que leer.
            return Conflict(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            // No se filtra el detalle técnico al usuario: el mensaje es de negocio y el error real queda en
            // el log. Aviso explicito de "avisar al equipo" porque esto puede pasar A MITAD del borrado real
            // (aunque la transaccion es todo-o-nada, un fallo DESPUES de la transaccion pero antes de
            // responder dejaria al usuario sin saber si se borro o no).
            _logger.LogError(ex, "Empezar de cero: fallo inesperado ejecutando el borrado.");
            return Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "No se pudo completar el borrado.",
                detail: "Ocurrió un problema al borrar los datos. Si esto pasó DESPUÉS de ver un mensaje de éxito, avisá al equipo técnico. Si no, no se tocó ningún dato.");
        }
    }
}
