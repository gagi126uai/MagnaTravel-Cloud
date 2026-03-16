using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/whatsapp/config")]
public class WhatsAppBotConfigController : ControllerBase
{
    private readonly AppDbContext _db;

    public WhatsAppBotConfigController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("env")]
    public async Task<ActionResult> GetBotEnv()
    {
        var config = await _db.WhatsAppBotConfigs.FirstOrDefaultAsync() ?? new WhatsAppBotConfig();
        var agency = await _db.AgencySettings.FirstOrDefaultAsync() ?? new AgencySettings();
        
        return Ok(new {
            config,
            agencyName = agency.AgencyName
        });
    }

    [HttpGet]
    public async Task<ActionResult<WhatsAppBotConfig>> GetConfig()
    {
        var config = await _db.WhatsAppBotConfigs.FirstOrDefaultAsync();
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
        var config = await _db.WhatsAppBotConfigs.FirstOrDefaultAsync();
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
}
