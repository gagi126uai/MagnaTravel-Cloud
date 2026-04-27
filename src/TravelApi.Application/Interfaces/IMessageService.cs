using TravelApi.Application.Contracts.Shared;
using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface IMessageService
{
    Task<IReadOnlyList<MessageRecipientDto>> GetRecipientsAsync(string? search, CancellationToken cancellationToken);
    Task<MessageDeliveryDto> SendSimpleMessageAsync(SendSimpleMessageRequest request, OperationActor actor, CancellationToken cancellationToken);
    Task<IReadOnlyList<MessageDeliveryDto>> SendVoucherMessageAsync(SendVoucherMessageRequest request, OperationActor actor, CancellationToken cancellationToken);
}
