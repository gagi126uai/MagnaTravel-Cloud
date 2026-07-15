using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Contracts.Shared;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

[ApiController]
[Authorize]
[Route("api/fiscal-documents")]
public class FiscalDocumentDeliveriesController : ControllerBase
{
    private readonly IMessageService _messageService;
    private readonly ILogger<FiscalDocumentDeliveriesController> _logger;

    public FiscalDocumentDeliveriesController(
        IMessageService messageService,
        ILogger<FiscalDocumentDeliveriesController> logger)
    {
        _messageService = messageService;
        _logger = logger;
    }

    [HttpPost("cancellations/{publicId:guid}/partial-credit-note/send")]
    [RequirePermission(Permissions.MessagesSend)]
    [RequireOwnership(OwnedEntity.BookingCancellation, "publicId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<MessageDeliveryDto>> SendPartialCreditNote(
        Guid publicId,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _messageService.SendPartialCreditNoteMessageAsync(publicId, BuildActor(), cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error sending partial credit note for cancellation {CancellationPublicId}", publicId);
            return Problem(statusCode: StatusCodes.Status502BadGateway, title: "No se pudo enviar la nota de crédito.");
        }
    }

    private OperationActor BuildActor() => new(
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "System",
        User.FindFirstValue("FullName") ?? User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "Sistema",
        User.FindAll(ClaimTypes.Role).Select(claim => claim.Value).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray());
}
