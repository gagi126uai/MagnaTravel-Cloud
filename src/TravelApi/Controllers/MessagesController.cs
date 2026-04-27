using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Contracts.Shared;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

namespace TravelApi.Controllers;

[ApiController]
[Authorize]
[Route("api/messages")]
public class MessagesController : ControllerBase
{
    private readonly IMessageService _messageService;
    private readonly ILogger<MessagesController> _logger;

    public MessagesController(IMessageService messageService, ILogger<MessagesController> logger)
    {
        _messageService = messageService;
        _logger = logger;
    }

    [HttpGet("recipients")]
    public async Task<ActionResult<IReadOnlyList<MessageRecipientDto>>> GetRecipients(
        [FromQuery] string? search,
        CancellationToken cancellationToken)
    {
        var recipients = await _messageService.GetRecipientsAsync(search, cancellationToken);
        return Ok(recipients);
    }

    [HttpPost("simple")]
    public async Task<ActionResult<MessageDeliveryDto>> SendSimpleMessage(
        [FromBody] SendSimpleMessageRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var delivery = await _messageService.SendSimpleMessageAsync(request, BuildActor(), cancellationToken);
            return Ok(delivery);
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
            _logger.LogError(ex, "Unexpected error sending simple message");
            return Problem(statusCode: StatusCodes.Status502BadGateway, title: "No se pudo enviar el mensaje.");
        }
    }

    [HttpPost("voucher")]
    public async Task<ActionResult<IReadOnlyList<MessageDeliveryDto>>> SendVoucherMessage(
        [FromBody] SendVoucherMessageRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var deliveries = await _messageService.SendVoucherMessageAsync(request, BuildActor(), cancellationToken);
            return Ok(deliveries);
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
            _logger.LogError(ex, "Unexpected error sending voucher message");
            return Problem(statusCode: StatusCodes.Status502BadGateway, title: "No se pudo enviar el voucher.");
        }
    }

    private OperationActor BuildActor()
    {
        var roles = User.FindAll(ClaimTypes.Role).Select(role => role.Value).Where(role => !string.IsNullOrWhiteSpace(role)).ToArray();
        return new OperationActor(
            User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "System",
            User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "Sistema",
            roles);
    }
}
