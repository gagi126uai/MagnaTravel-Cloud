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
[RequirePermission(Permissions.MessagesView)]
[Route("api/whatsapp/conversations")]
public class WhatsAppConversationsController : ControllerBase
{
    private readonly IWhatsAppConversationService _conversationService;

    public WhatsAppConversationsController(IWhatsAppConversationService conversationService)
    {
        _conversationService = conversationService;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WhatsAppConversationListItemDto>>> GetConversations(CancellationToken cancellationToken)
    {
        var items = await _conversationService.GetConversationsAsync(BuildActor(), cancellationToken);
        return Ok(items);
    }

    [HttpGet("{conversationType}/{publicIdOrLegacyId}")]
    public async Task<ActionResult<WhatsAppConversationDetailDto>> GetConversationDetail(
        string conversationType,
        string publicIdOrLegacyId,
        CancellationToken cancellationToken)
    {
        try
        {
            var detail = await _conversationService.GetConversationDetailAsync(
                conversationType,
                publicIdOrLegacyId,
                BuildActor(),
                cancellationToken);

            return detail is null
                ? NotFound()
                : Ok(detail);
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "Tipo de conversacion invalido." });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    private OperationActor BuildActor()
    {
        var roles = User.FindAll(ClaimTypes.Role)
            .Select(role => role.Value)
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .ToArray();

        return new OperationActor(
            User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "System",
            User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "Sistema",
            roles);
    }
}
