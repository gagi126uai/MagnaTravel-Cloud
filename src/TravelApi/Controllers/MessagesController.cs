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
    [RequirePermission(Permissions.MessagesView)]
    public async Task<ActionResult<IReadOnlyList<MessageRecipientDto>>> GetRecipients(
        [FromQuery] string? search,
        CancellationToken cancellationToken)
    {
        var recipients = await _messageService.GetRecipientsAsync(search, BuildActor(), cancellationToken);
        return Ok(recipients);
    }

    [HttpPost("simple")]
    [RequirePermission(Permissions.MessagesSend)]
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
    [RequirePermission(Permissions.MessagesSend, Permissions.VouchersSend)]
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

    [HttpPost("invoice")]
    [RequirePermission(Permissions.MessagesSend)]
    public async Task<ActionResult<MessageDeliveryDto>> SendInvoiceMessage(
        [FromBody] SendInvoiceMessageRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var delivery = await _messageService.SendInvoiceMessageAsync(request, BuildActor(), cancellationToken);
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
            _logger.LogError(ex, "Unexpected error sending invoice message");
            return Problem(statusCode: StatusCodes.Status502BadGateway, title: "No se pudo enviar la factura.");
        }
    }

    /// <summary>
    /// TANDA 4 (2026-08-13): envía el PDF de PRESUPUESTO al cliente de la reserva por WhatsApp. Gemelo
    /// de <see cref="SendVoucherMessage"/>/<see cref="SendInvoiceMessage"/> pero sin selector de
    /// destinatario: en etapa Presupuesto todavía no hay "a quién más" mandarle el documento, siempre es
    /// el cliente/pagador de la reserva. Doble permiso (AND, no OR como el voucher): hace falta poder
    /// enviar mensajes Y poder ver la reserva — <see cref="IMessageService.SendBudgetMessageAsync"/>
    /// además valida ownership puntual de ESA reserva (dueño o reservas.view_all).
    /// </summary>
    [HttpPost("budget")]
    [RequirePermission(Permissions.MessagesSend)]
    [RequirePermission(Permissions.ReservasView)]
    public async Task<ActionResult<MessageDeliveryDto>> SendBudgetMessage(
        [FromBody] SendBudgetMessageRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var delivery = await _messageService.SendBudgetMessageAsync(request, BuildActor(), cancellationToken);
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
            _logger.LogError(ex, "Unexpected error sending budget message");
            return Problem(statusCode: StatusCodes.Status502BadGateway, title: "No se pudo enviar el presupuesto.");
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
