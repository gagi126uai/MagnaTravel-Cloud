namespace TravelApi.Application.DTOs;

public class MessageRecipientDto
{
    public string PersonType { get; set; } = string.Empty;
    public Guid PersonPublicId { get; set; }
    public Guid ReservaPublicId { get; set; }
    public string NumeroReserva { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public bool HasPhone => !string.IsNullOrWhiteSpace(Phone);
    public IReadOnlyList<VoucherDto> Vouchers { get; set; } = Array.Empty<VoucherDto>();
}

public class SendSimpleMessageRequest
{
    public string PersonType { get; set; } = string.Empty;
    public string PersonId { get; set; } = string.Empty;
    public string ReservaId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class SendVoucherMessageRequest
{
    public string PersonType { get; set; } = string.Empty;
    public string PersonId { get; set; } = string.Empty;
    public string ReservaId { get; set; } = string.Empty;
    public List<string> VoucherIds { get; set; } = new();
    public string? Caption { get; set; }
    public VoucherExceptionRequest? Exception { get; set; }
}

/// <summary>
/// Paso 5 (2026-06-24): pedido para enviar el PDF de una FACTURA EMITIDA al cliente de la reserva por
/// WhatsApp. A diferencia del voucher (que puede ir al pasajero), el destinatario por defecto es el
/// CLIENTE/pagador de la reserva: la factura es un documento fiscal a su nombre. <c>PersonType</c> y
/// <c>PersonId</c> se mantienen por simetria con el voucher y para validar que la persona corresponda a
/// la reserva, pero el caso normal es <c>PersonType = "customer"</c>.
/// </summary>
public class SendInvoiceMessageRequest
{
    public string PersonType { get; set; } = "customer";
    public string PersonId { get; set; } = string.Empty;
    public string ReservaId { get; set; } = string.Empty;
    public string InvoicePublicId { get; set; } = string.Empty;
    public string? Caption { get; set; }
}

public class MessageDeliveryDto
{
    public Guid PublicId { get; set; }
    public Guid? ReservaPublicId { get; set; }
    public Guid? VoucherPublicId { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string? MessageText { get; set; }
    public string? AttachmentName { get; set; }
    public string? BotMessageId { get; set; }
    public string? SentByUserName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public string? Error { get; set; }
}
