using TravelApi.Application.Contracts.Shared;
using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface IWhatsAppConversationService
{
    Task<IReadOnlyList<WhatsAppConversationListItemDto>> GetConversationsAsync(OperationActor actor, CancellationToken cancellationToken);

    Task<WhatsAppConversationDetailDto?> GetConversationDetailAsync(
        string conversationType,
        string publicIdOrLegacyId,
        OperationActor actor,
        CancellationToken cancellationToken);
}
