using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

/// <summary>
/// Obra "Empezar de cero" (2026-07-27, Parte A "borrado selectivo por grupos") + "Restaurar desde la app"
/// (Parte B): endpoints para las dos operaciones mas destructivas/delicadas del sistema — borrado masivo por
/// grupos con backup previo obligatorio, y restauracion de un backup. El gate es doble a proposito en TODOS
/// los endpoints: rol Admin (bypass de todo permiso) Y ADEMAS un permiso explicito por operacion (defensa en
/// profundidad — si el dia de mañana el bypass de rol cambia, el permiso solo igual protege). La logica de
/// negocio completa vive en <see cref="ISystemDataWipeService"/>/<see cref="ISystemDataRestoreService"/>
/// (Infrastructure): este controller queda deliberadamente fino.
/// </summary>
[ApiController]
[Route("api/admin/danger")]
[Authorize(Roles = "Admin")]
public class AdminDangerController : ControllerBase
{
    private readonly ISystemDataWipeService _wipeService;
    private readonly ISystemDataRestoreService _restoreService;
    private readonly ILogger<AdminDangerController> _logger;

    public AdminDangerController(
        ISystemDataWipeService wipeService,
        ISystemDataRestoreService restoreService,
        ILogger<AdminDangerController> logger)
    {
        _wipeService = wipeService;
        _restoreService = restoreService;
        _logger = logger;
    }

    /// <summary>
    /// SOLO LECTURA: conteos actuales por grupo + si el candado fiscal está activo (y por qué) + el mapa de
    /// dependencias entre grupos. No cambia nada.
    /// </summary>
    [HttpGet("wipe/preview")]
    [RequirePermission(Permissions.ConfiguracionDataWipe)]
    public async Task<ActionResult<SystemDataWipePreviewResponse>> GetWipePreview(CancellationToken ct)
    {
        var preview = await _wipeService.GetPreviewAsync(ct);
        return Ok(preview);
    }

    /// <summary>
    /// Ejecuta el borrado real de los grupos pedidos. Devuelve 409 con un mensaje en castellano si la frase no
    /// coincide, la contraseña es incorrecta, los grupos no son coherentes con sus dependencias, el candado
    /// fiscal está activo, o el backup previo falló — en todos esos casos NO se borró nada.
    /// </summary>
    [HttpPost("wipe")]
    [RequirePermission(Permissions.ConfiguracionDataWipe)]
    public async Task<ActionResult<SystemDataWipeResponse>> Wipe(
        [FromBody] SystemDataWipeRequest request,
        CancellationToken ct)
    {
        var userId = GetRequesterUserIdOrNull();
        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            var result = await _wipeService.ExecuteWipeAsync(userId, request.Password, request.Phrase, request.Grupos, ct);
            return Ok(result);
        }
        catch (SystemDataWipeRefusedException ex)
        {
            // El mensaje YA viene en criollo desde el service. No se filtra nada tecnico.
            return Conflict(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Empezar de cero: fallo inesperado ejecutando el borrado.");
            return Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "No se pudo completar el borrado.",
                detail: "Ocurrió un problema al borrar los datos. Si esto pasó DESPUÉS de ver un mensaje de éxito, avisá al equipo técnico. Si no, no se tocó ningún dato.");
        }
    }

    /// <summary>SOLO LECTURA: lista los backups disponibles para restaurar (más nuevo primero).</summary>
    [HttpGet("backups")]
    [RequirePermission(Permissions.ConfiguracionDataRestore)]
    public async Task<ActionResult<SystemDataBackupsResponse>> GetBackups(CancellationToken ct)
    {
        var response = await _restoreService.ListBackupsAsync(ct);
        return Ok(response);
    }

    /// <summary>SOLO LECTURA: valida un backup (existencia + índice legible). No restaura nada.</summary>
    [HttpPost("restore/verify")]
    [RequirePermission(Permissions.ConfiguracionDataRestore)]
    public async Task<ActionResult<SystemDataRestoreVerifyResponse>> VerifyRestore(
        [FromBody] SystemDataRestoreVerifyRequest request,
        CancellationToken ct)
    {
        var userId = GetRequesterUserIdOrNull();
        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            var result = await _restoreService.VerifyBackupAsync(userId, request.Archivo, ct);
            return Ok(result);
        }
        catch (SystemDataRestoreRefusedException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Ejecuta la restauración real (modo <c>prueba</c>: base sombra separada; modo <c>real</c>: solo tablas
    /// de configuración, data-only, sobre tablas vacías de la base viva). Devuelve 409 en castellano si algo
    /// no es válido — en ese caso NO se restauró nada.
    /// </summary>
    [HttpPost("restore")]
    [RequirePermission(Permissions.ConfiguracionDataRestore)]
    public async Task<ActionResult<SystemDataRestoreResponse>> Restore(
        [FromBody] SystemDataRestoreRequest request,
        CancellationToken ct)
    {
        var userId = GetRequesterUserIdOrNull();
        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            var result = await _restoreService.ExecuteRestoreAsync(
                userId, request.Password, request.Phrase, request.Archivo, request.Modo, request.Tablas, request.Motivo, ct);
            return Ok(result);
        }
        catch (SystemDataRestoreRefusedException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restaurar: fallo inesperado ejecutando la restauración.");
            return Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "No se pudo completar la restauración.",
                detail: "Ocurrió un problema al restaurar el backup. Si esto pasó DESPUÉS de ver un mensaje de éxito, avisá al equipo técnico.");
        }
    }

    private string? GetRequesterUserIdOrNull()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return string.IsNullOrWhiteSpace(userId) ? null : userId;
    }
}
