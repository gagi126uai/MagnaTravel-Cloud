using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface IWhatsAppConversationService
{
    Task<IReadOnlyList<WhatsAppConversationListItemDto>> GetConversationsAsync(CancellationToken cancellationToken);

    Task<WhatsAppConversationDetailDto?> GetConversationDetailAsync(
        string conversationType,
        string publicIdOrLegacyId,
        CancellationToken cancellationToken);
}
