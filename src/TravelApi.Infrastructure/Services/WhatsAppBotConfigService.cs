using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class WhatsAppBotConfigService : IWhatsAppBotConfigService
{
    private readonly AppDbContext _db;

    public WhatsAppBotConfigService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<WhatsAppBotEnvironmentDto> GetBotEnvironmentAsync(CancellationToken cancellationToken)
    {
        var config = await _db.WhatsAppBotConfigs
            .AsNoTracking()
            .OrderBy(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken) ?? new WhatsAppBotConfig();

        var agency = await _db.AgencySettings
            .AsNoTracking()
            .OrderBy(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken) ?? new AgencySettings();

        return new WhatsAppBotEnvironmentDto
        {
            Config = MapConfig(config),
            AgencyName = agency.AgencyName
        };
    }

    public async Task<WhatsAppBotConfigDto> GetConfigAsync(CancellationToken cancellationToken)
    {
        var config = await _db.WhatsAppBotConfigs
            .OrderBy(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (config is null)
        {
            config = new WhatsAppBotConfig();
            _db.WhatsAppBotConfigs.Add(config);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return MapConfig(config);
    }

    public async Task UpdateConfigAsync(UpdateWhatsAppBotConfigRequest request, CancellationToken cancellationToken)
    {
        var config = await _db.WhatsAppBotConfigs
            .OrderBy(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (config is null)
        {
            config = new WhatsAppBotConfig();
            _db.WhatsAppBotConfigs.Add(config);
        }

        config.WelcomeMessage = request.WelcomeMessage;
        config.AskInterestMessage = request.AskInterestMessage;
        config.AskDatesMessage = request.AskDatesMessage;
        config.AskTravelersMessage = request.AskTravelersMessage;
        config.ThanksMessage = request.ThanksMessage;
        config.AgentRequestMessage = request.AgentRequestMessage;
        config.DuplicateMessage = request.DuplicateMessage;
        config.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static WhatsAppBotConfigDto MapConfig(WhatsAppBotConfig config)
    {
        return new WhatsAppBotConfigDto
        {
            WelcomeMessage = config.WelcomeMessage,
            AskInterestMessage = config.AskInterestMessage,
            AskDatesMessage = config.AskDatesMessage,
            AskTravelersMessage = config.AskTravelersMessage,
            ThanksMessage = config.ThanksMessage,
            AgentRequestMessage = config.AgentRequestMessage,
            DuplicateMessage = config.DuplicateMessage
        };
    }
}
