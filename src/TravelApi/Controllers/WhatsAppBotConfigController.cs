using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/whatsapp/config")]
[Authorize(Roles = "Admin")]
public class WhatsAppBotConfigController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;

    public WhatsAppBotConfigController(AppDbContext db, IConfiguration configuration)
    {
        _db = db;
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

        var config = await _db.WhatsAppBotConfigs.OrderBy(item => item.Id).FirstOrDefaultAsync() ?? new WhatsAppBotConfig();
        var agency = await _db.AgencySettings.OrderBy(item => item.Id).FirstOrDefaultAsync() ?? new AgencySettings();
        
        return Ok(new {
            config = new {
                config.WelcomeMessage,
                config.AskInterestMessage,
                config.AskDatesMessage,
                config.AskTravelersMessage,
                config.ThanksMessage,
                config.AgentRequestMessage,
                config.DuplicateMessage
            },
            agencyName = agency.AgencyName
        });
    }

    [HttpGet]
    public async Task<ActionResult<WhatsAppBotConfig>> GetConfig()
    {
        var config = await _db.WhatsAppBotConfigs.OrderBy(item => item.Id).FirstOrDefaultAsync();
        if (config == null)
        {
            config = new WhatsAppBotConfig();
            _db.WhatsAppBotConfigs.Add(config);
            await _db.SaveChangesAsync();
        }
        return Ok(config);
    }

    [HttpPut]
    public async Task<IActionResult> UpdateConfig(WhatsAppBotConfig updated)
    {
        var config = await _db.WhatsAppBotConfigs.OrderBy(item => item.Id).FirstOrDefaultAsync();
        if (config == null)
        {
            config = new WhatsAppBotConfig();
            _db.WhatsAppBotConfigs.Add(config);
        }

        config.WelcomeMessage = updated.WelcomeMessage;
        config.AskInterestMessage = updated.AskInterestMessage;
        config.AskDatesMessage = updated.AskDatesMessage;
        config.AskTravelersMessage = updated.AskTravelersMessage;
        config.ThanksMessage = updated.ThanksMessage;
        config.AgentRequestMessage = updated.AgentRequestMessage;
        config.DuplicateMessage = updated.DuplicateMessage;
        config.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
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
