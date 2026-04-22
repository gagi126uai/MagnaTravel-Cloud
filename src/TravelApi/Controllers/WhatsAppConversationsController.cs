using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

namespace TravelApi.Controllers;

[ApiController]
[Authorize]
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
        var items = await _conversationService.GetConversationsAsync(cancellationToken);
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
                cancellationToken);

            return detail is null
                ? NotFound()
                : Ok(detail);
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "Tipo de conversacion invalido." });
        }
    }
}
