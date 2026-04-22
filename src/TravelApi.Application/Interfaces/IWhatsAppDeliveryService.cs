using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface IWhatsAppDeliveryService
{
    Task PrepareVoucherDraftAsync(string reservaPublicIdOrLegacyId, CancellationToken cancellationToken);
    Task<WhatsAppVoucherPreviewResponse> GetVoucherPreviewAsync(string reservaPublicIdOrLegacyId, CancellationToken cancellationToken);
    Task<WhatsAppVoucherPreviewResponse> UpdateReservaWhatsAppContactAsync(string reservaPublicIdOrLegacyId, string? phoneOverride, CancellationToken cancellationToken);
    Task<IReadOnlyList<WhatsAppDeliveryDto>> GetHistoryAsync(string reservaPublicIdOrLegacyId, CancellationToken cancellationToken);
    Task<WhatsAppDeliveryDto> SendVoucherAsync(string reservaPublicIdOrLegacyId, string? caption, string performedBy, CancellationToken cancellationToken);
    Task<bool> TryHandleIncomingOperationalMessageAsync(string phone, string message, CancellationToken cancellationToken);
}
