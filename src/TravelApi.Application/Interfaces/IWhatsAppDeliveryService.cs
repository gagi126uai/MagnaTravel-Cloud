using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface IWhatsAppDeliveryService
{
    Task PrepareVoucherDraftAsync(int reservaId, CancellationToken cancellationToken);
    Task<WhatsAppVoucherPreviewResponse> GetVoucherPreviewAsync(int reservaId, CancellationToken cancellationToken);
    Task<WhatsAppVoucherPreviewResponse> UpdateReservaWhatsAppContactAsync(int reservaId, string? phoneOverride, CancellationToken cancellationToken);
    Task<IReadOnlyList<WhatsAppDeliveryDto>> GetHistoryAsync(int reservaId, CancellationToken cancellationToken);
    Task<WhatsAppDeliveryDto> SendVoucherAsync(int reservaId, string? caption, string performedBy, CancellationToken cancellationToken);
    Task<bool> TryHandleIncomingOperationalMessageAsync(string phone, string message, CancellationToken cancellationToken);
}
