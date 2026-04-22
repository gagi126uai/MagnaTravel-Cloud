using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface IWhatsAppBotConfigService
{
    Task<WhatsAppBotEnvironmentDto> GetBotEnvironmentAsync(CancellationToken cancellationToken);

    Task<WhatsAppBotConfigDto> GetConfigAsync(CancellationToken cancellationToken);

    Task UpdateConfigAsync(UpdateWhatsAppBotConfigRequest request, CancellationToken cancellationToken);
}
