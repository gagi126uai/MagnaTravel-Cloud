using TravelApi.Application.Contracts.Shared;
using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface IMessageService
{
    Task<IReadOnlyList<MessageRecipientDto>> GetRecipientsAsync(string? search, OperationActor actor, CancellationToken cancellationToken);
    Task<MessageDeliveryDto> SendSimpleMessageAsync(SendSimpleMessageRequest request, OperationActor actor, CancellationToken cancellationToken);
    Task<IReadOnlyList<MessageDeliveryDto>> SendVoucherMessageAsync(SendVoucherMessageRequest request, OperationActor actor, CancellationToken cancellationToken);

    // Paso 5 (2026-06-24): envia el PDF de una factura EMITIDA (con CAE) al cliente de la reserva por
    // WhatsApp. Reusa la misma generacion de PDF que "Ver PDF" (IInvoiceService.GetPdfAsync) y registra
    // la entrega como un MessageDelivery (Kind = "Invoice"), igual que el voucher.
    Task<MessageDeliveryDto> SendInvoiceMessageAsync(SendInvoiceMessageRequest request, OperationActor actor, CancellationToken cancellationToken);
}
