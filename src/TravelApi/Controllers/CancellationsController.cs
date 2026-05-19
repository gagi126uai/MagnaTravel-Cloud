using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using TravelApi.Application.DTOs;
using TravelApi.Application.Exceptions;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;
using TravelApi.Errors;

namespace TravelApi.Controllers;

/// <summary>
/// FC1.2.4 v3 (2026-05-18): expone el flujo de cancelacion de reservas
/// (BookingCancellation) por HTTP. El controller es thin: parsea request,
/// invoca al service, traduce excepciones a HTTP. La logica de negocio vive
/// 100% en <see cref="IBookingCancellationService"/>.
///
/// <para>
/// <b>Diseño de permisos</b> (decision de auditoria 2026-05-18):
/// reusamos los permisos existentes de B1.15 — <c>reservas.cancel</c> ya esta
/// pensado para "el usuario puede cancelar reservas". No creamos
/// <c>cancellations.create</c>/<c>cancellations.confirm</c>/etc nuevos porque:
/// <list type="bullet">
///   <item>El modulo BC reemplaza el cancel legacy: una sola politica de quien
///         puede cancelar es mas simple que dos modulos con permisos paralelos.</item>
///   <item>El gating fino (cancelar reserva con pagos -> approval, override de
///         invariantes -> approval) lo decide el service via
///         <c>ApprovalRequiredException</c>, no el controller.</item>
/// </list>
/// La unica excepcion es <c>CancellationsForceArcaConfirmation</c> que existe
/// como permiso dedicado (Admin-only, escape hatch fiscal) creado en FC1.2.1.
/// </para>
///
/// <para>
/// <b>Ownership</b>: el responsable del BC es el responsable de la reserva
/// padre. <see cref="OwnedEntity.BookingCancellation"/> ya esta soportado por
/// <c>OwnershipResolver</c> (FC1.2.0). El POST de creacion no usa el decorator
/// porque el id viene en el body, no en la ruta — la validacion se hace
/// manualmente con <see cref="IOwnershipResolver"/>.
/// </para>
/// </summary>
[ApiController]
[Route("api/cancellations")]
[Authorize]
public class CancellationsController : ControllerBase
{
    private readonly IBookingCancellationService _bcService;
    private readonly IOwnershipResolver _ownershipResolver;

    public CancellationsController(
        IBookingCancellationService bcService,
        IOwnershipResolver ownershipResolver)
    {
        _bcService = bcService;
        _ownershipResolver = ownershipResolver;
    }

    /// <summary>
    /// T-1: crea un BookingCancellation en estado <c>Drafted</c>.
    /// El usuario puede arrepentirse (abort) hasta T0 (Confirm).
    /// </summary>
    [HttpPost]
    [RequirePermission(Permissions.ReservasCancel)]
    public async Task<ActionResult<BookingCancellationDto>> Draft(
        DraftCancellationRequest request,
        CancellationToken cancellationToken)
    {
        // El body trae el ReservaPublicId — no podemos usar el decorator de
        // ownership que solo lee de route params. Validamos a mano contra la
        // reserva original, con bypass por ReservasViewAll (mismo patron que
        // PaymentService.CreatePaymentAsync que usa el service para validar).
        if (!await UserIsAllowedOverReservaAsync(request.ReservaPublicId.ToString(), cancellationToken))
        {
            return new ObjectResult(PermissionDeniedProblemFactory.OwnershipRequired(OwnedEntity.Reserva.ToString()))
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }

        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;

        try
        {
            var dto = await _bcService.DraftAsync(request, userId, userName, cancellationToken);
            return CreatedAtAction(
                actionName: nameof(GetByPublicId),
                routeValues: new { publicId = dto.PublicId },
                value: dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            // Feature flag off, reserva sin invoice activa, multiples invoices
            // con OnePerReservaInvoicePolicy on, etc. 409 expresa "estado actual
            // incompatible con la operacion" — coherente con PaymentsController.
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        // BusinessInvariantViolationException lo atrapa GlobalExceptionHandler
        // (409 con invariantCode + constraintName). No la catcheamos aca.
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    /// <summary>
    /// T0: transiciona <c>Drafted</c> → <c>AwaitingFiscalConfirmation</c>.
    /// Encola la NC en AFIP via InvoiceService.EnqueueAnnulmentAsync.
    /// Si requiere override de invariantes, el service tira ApprovalRequiredException.
    /// </summary>
    [HttpPatch("{publicId:guid}/confirm")]
    [RequirePermission(Permissions.ReservasCancel)]
    [RequireOwnership(OwnedEntity.BookingCancellation, "publicId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<BookingCancellationDto>> Confirm(
        Guid publicId,
        ConfirmCancellationRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;
        var requesterIsAdmin = User.IsInRole("Admin");

        try
        {
            var dto = await _bcService.ConfirmAsync(publicId, request, userId, userName, requesterIsAdmin, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ApprovalRequiredException ex)
        {
            // Mismo contrato 409 + body que InvoicesController.AnnulInvoice para
            // que el frontend (RequestApprovalModal) consuma el mismo shape.
            return Conflict(new
            {
                message = "Esta acción requiere autorización previa del Administrador o Colaborador.",
                requiresApproval = true,
                requestType = ex.RequestType.ToString(),
                entityType = ex.EntityType,
                entityId = ex.EntityId,
            });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    /// <summary>
    /// Aborta un BC en estado <c>Drafted</c> (el operador se arrepintio antes
    /// de tocar AFIP). Idempotente: si ya esta Aborted, retorna 200 con el DTO.
    /// </summary>
    [HttpPatch("{publicId:guid}/abort")]
    [RequirePermission(Permissions.ReservasCancel)]
    [RequireOwnership(OwnedEntity.BookingCancellation, "publicId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<BookingCancellationDto>> Abort(
        Guid publicId,
        AbortCancellationRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";

        try
        {
            var dto = await _bcService.AbortAsync(publicId, request.Reason, userId, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    /// <summary>
    /// FC1.2.1 BR-V2-01: escape hatch admin. Cuando AFIP devolvio CAE pero el
    /// callback automatico fallo, un Admin puede empatar el estado del BC con
    /// la realidad fiscal. Solo Admin (permiso dedicado, no va a Vendedor ni
    /// Colaborador). Requiere ApprovalRequest aprobado tipo InvariantOverride
    /// scoped al BC.
    /// </summary>
    [HttpPost("{publicId:guid}/force-arca-confirmation")]
    [RequirePermission(Permissions.CancellationsForceArcaConfirmation)]
    [RequireOwnership(OwnedEntity.BookingCancellation, "publicId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<BookingCancellationDto>> ForceArcaConfirmation(
        Guid publicId,
        ForceArcaConfirmationRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;

        try
        {
            var dto = await _bcService.ForceArcaConfirmationAsync(publicId, request, userId, userName, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ApprovalRequiredException ex)
        {
            return Conflict(new
            {
                message = "Esta acción requiere autorización previa.",
                requiresApproval = true,
                requestType = ex.RequestType.ToString(),
                entityType = ex.EntityType,
                entityId = ex.EntityId,
            });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    /// <summary>
    /// Lectura de un BC por PublicId. Lo usa la UI de cancelaciones y los
    /// tests de E2E (refetch despues de cada accion).
    /// </summary>
    [HttpGet("{publicId:guid}")]
    [RequirePermission(Permissions.ReservasView)]
    [RequireOwnership(OwnedEntity.BookingCancellation, "publicId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<BookingCancellationDto>> GetByPublicId(
        Guid publicId,
        CancellationToken cancellationToken)
    {
        var dto = await _bcService.GetByPublicIdAsync(publicId, cancellationToken);
        if (dto is null) return NotFound();
        return Ok(dto);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>
    /// Validacion manual de ownership para endpoints sin id en la ruta (POST).
    /// Replica la logica del <see cref="RequireOwnershipAttribute"/>: Admin
    /// bypass + bypass por permiso global + IOwnershipResolver para los demas.
    /// </summary>
    private async Task<bool> UserIsAllowedOverReservaAsync(string reservaPublicIdOrLegacyId, CancellationToken ct)
    {
        if (User.IsInRole("Admin"))
        {
            return true;
        }

        // ReservasViewAll permite operar sobre cualquier reserva (admin de
        // back-office). Lo verificamos via las claims emitidas por
        // PermissionAuthorizationHandler (en runtime se carga en el handler
        // via IUserPermissionResolver, pero el RequirePermission attribute ya
        // dejo al usuario "autorizado" para llegar hasta aca — la mejor forma
        // de saber si tiene ViewAll extra es consultar el resolver).
        var resolver = HttpContext.RequestServices.GetService<IUserPermissionResolver>();
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return false;

        if (resolver is not null)
        {
            var perms = await resolver.GetPermissionsAsync(userId, ct);
            if (perms.Contains(Permissions.ReservasViewAll))
            {
                return true;
            }
        }

        return await _ownershipResolver.IsOwnerAsync(userId, OwnedEntity.Reserva, reservaPublicIdOrLegacyId, ct);
    }
}

/// <summary>
/// FC1.2.4: payload del abort. Reason es libre del operador para auditoria
/// (queda en BC.Reason o en audit log segun donde el service lo guarde).
/// </summary>
public record AbortCancellationRequest(
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.MinLength(5)]
    [System.ComponentModel.DataAnnotations.MaxLength(500)]
    string Reason
);
