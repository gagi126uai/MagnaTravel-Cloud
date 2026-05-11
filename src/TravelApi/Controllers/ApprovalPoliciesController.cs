using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

/// <summary>
/// B1.15 Fase B'' (2026-05-11): configuracion de policies por RequestType.
///
/// Acceso: solo Admin via <see cref="Permissions.ApprovalsPolicies"/>. Lectura
/// y escritura comparten el mismo permiso porque la UI esta en Configuracion
/// y no tiene sentido "ver pero no editar" en este contexto.
/// </summary>
[ApiController]
[Route("api/approval-policies")]
[Authorize]
[RequirePermission(Permissions.ApprovalsPolicies)]
public class ApprovalPoliciesController : ControllerBase
{
    private readonly IApprovalPolicyService _service;

    public ApprovalPoliciesController(IApprovalPolicyService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ApprovalPolicyDto>>> GetAll(CancellationToken ct)
        => Ok(await _service.GetAllAsync(ct));

    [HttpPut("{requestType}")]
    public async Task<ActionResult<ApprovalPolicyDto>> Update(
        string requestType,
        [FromBody] UpdateApprovalPolicyPayload payload,
        CancellationToken ct)
    {
        if (!Enum.TryParse<ApprovalRequestType>(requestType, ignoreCase: false, out var parsed))
            return BadRequest(new { message = $"RequestType invalido: {requestType}" });

        try
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? throw new UnauthorizedAccessException("Usuario no identificado.");
            var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;
            var result = await _service.UpdateAsync(parsed, payload, userId, userName, ct);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
