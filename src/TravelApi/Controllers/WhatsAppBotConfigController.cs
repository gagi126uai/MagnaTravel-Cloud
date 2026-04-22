using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/whatsapp/config")]
[Authorize(Roles = "Admin")]
public class WhatsAppBotConfigController : ControllerBase
{
    private readonly IWhatsAppBotConfigService _botConfigService;
    private readonly IConfiguration _configuration;

    public WhatsAppBotConfigController(IWhatsAppBotConfigService botConfigService, IConfiguration configuration)
    {
        _botConfigService = botConfigService;
        _configuration = configuration;
    }

    [HttpGet("env")]
    [AllowAnonymous]
    public async Task<ActionResult> GetBotEnv()
    {
        if (!IsInternalBotRequest())
        {
            return Unauthorized();
        }

        var environment = await _botConfigService.GetBotEnvironmentAsync(HttpContext.RequestAborted);
        return Ok(environment);
    }

    [HttpGet]
    public async Task<ActionResult<WhatsAppBotConfigDto>> GetConfig(CancellationToken cancellationToken)
    {
        var config = await _botConfigService.GetConfigAsync(cancellationToken);
        return Ok(config);
    }

    [HttpPut]
    public async Task<IActionResult> UpdateConfig(
        UpdateWhatsAppBotConfigRequest updated,
        CancellationToken cancellationToken)
    {
        await _botConfigService.UpdateConfigAsync(updated, cancellationToken);
        return NoContent();
    }

    private bool IsInternalBotRequest()
    {
        var expected = _configuration["WhatsApp:WebhookSecret"] ?? "CHANGE_THIS_SECRET";
        var provided = Request.Headers["X-Webhook-Secret"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(provided))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(provided));
    }
}
