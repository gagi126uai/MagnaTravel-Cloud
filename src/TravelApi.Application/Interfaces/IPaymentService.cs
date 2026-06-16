using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface IPaymentService
{
    Task<CollectionsSummaryDto> GetCollectionsSummaryAsync(CancellationToken cancellationToken);
    Task<PagedResponse<CollectionWorkItemDto>> GetCollectionsWorklistAsync(CollectionWorklistQuery query, CancellationToken cancellationToken);
    Task<PagedResponse<PaymentDto>> GetAllPaymentsAsync(PaymentsListQuery query, CancellationToken cancellationToken);
    Task<PagedResponse<FinanceHistoryItemDto>> GetHistoryAsync(FinanceHistoryQuery query, CancellationToken cancellationToken);
    Task<IEnumerable<PaymentDto>> GetPaymentsForReservaAsync(string reservaPublicIdOrLegacyId, CancellationToken cancellationToken);
    Task<PaymentDto> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken);
    Task<PaymentReceiptDto> IssueReceiptAsync(string paymentPublicIdOrLegacyId, CancellationToken cancellationToken);
    Task<byte[]> GetReceiptPdfAsync(string paymentPublicIdOrLegacyId, CancellationToken cancellationToken);

    /// <summary>
    /// B1.15 (2026-05-11): anular el comprobante interno de un pago. La fila Receipt
    /// se preserva (Status -> Voided + audit fields) para mantener numeracion correlativa.
    /// Si la <c>ApprovalPolicy</c> de <c>ReceiptVoidance</c> requiere aprobacion y el caller
    /// NO es Admin, lanza <see cref="Application.Exceptions.ApprovalRequiredException"/>.
    /// </summary>
    Task VoidReceiptAsync(
        string paymentPublicIdOrLegacyId,
        string? reason,
        string userId,
        string? userName,
        bool requesterIsAdmin,
        CancellationToken cancellationToken);
    Task<IEnumerable<object>> GetDeletedPaymentsAsync(CancellationToken cancellationToken);
    Task<Guid> RestorePaymentAsync(string paymentPublicIdOrLegacyId, CancellationToken cancellationToken);
    Task UpdatePaymentAsync(string paymentPublicIdOrLegacyId, UpdatePaymentRequest request, CancellationToken cancellationToken);
    Task DeletePaymentAsync(string paymentPublicIdOrLegacyId, CancellationToken cancellationToken);

    /// <summary>
    /// ADR-032 (2026-06-15): anula un cobro CON RASTRO. A diferencia del DELETE libre, opera tambien en
    /// reservas terminales (cancelada/cerrada) — es la salida valida para corregir un cobro mal cargado
    /// cuando la reserva ya no es cobrable. Reusa el mismo mecanismo de reversa (soft-delete + contra-asiento
    /// de caja). Mantiene los guards de puente y fiscales: un cobro con recibo/CAE vivo sigue exigiendo la
    /// anulacion fiscal existente. <paramref name="reason"/> es opcional y queda en el audit trail.
    /// </summary>
    Task AnnulPaymentAsync(string paymentPublicIdOrLegacyId, string? reason, CancellationToken cancellationToken);
}

public class CreatePaymentRequest
{
    public string ReservaId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Method { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string? Notes { get; set; }

    // ====================================================================================
    // ADR-021 Capa 4 (multimoneda + cobro cruzado, 2026-06-10). Todos OPCIONALES: un request
    // que no los manda queda en ARS no cruzado = byte-identico al comportamiento previo. El
    // front viejo (que no manda nada) sigue funcionando igual. La validacion server-side §8
    // vive en PaymentService.CreatePaymentAsync (no se confia en el front).
    // ====================================================================================

    /// <summary>ADR-021 §2.2: moneda REAL del cobro (lo que entro a caja). null/vacio = ARS.</summary>
    public string? Currency { get; set; }

    /// <summary>
    /// ADR-021 §2.7: moneda del SALDO al que se imputa. null = se imputa a su propia <see cref="Currency"/>
    /// (pago no cruzado). Si difiere de <see cref="Currency"/>, el pago es CRUZADO y el bloque de TC
    /// de abajo pasa a ser obligatorio (validacion §8.5).
    /// </summary>
    public string? ImputedCurrency { get; set; }

    /// <summary>ADR-021 §2.2bis: tipo de cambio. Convencion FIJA: unidades de ARS por 1 USD. Obligatorio si el pago cruza.</summary>
    public decimal? ExchangeRate { get; set; }

    /// <summary>ADR-021: origen del tipo de cambio (enum <c>ExchangeRateSource</c> como int). Obligatorio si el pago cruza.</summary>
    public int? ExchangeRateSource { get; set; }

    /// <summary>ADR-021: fecha del tipo de cambio. Obligatorio si el pago cruza.</summary>
    public DateTime? ExchangeRateAt { get; set; }

    /// <summary>
    /// ADR-021 §2.2bis: monto equivalente que baja del saldo de <see cref="ImputedCurrency"/>. Si el pago
    /// cruza y no se manda, el backend lo CALCULA con la formula de §2.2bis (no se confia en el front).
    /// </summary>
    public decimal? ImputedAmount { get; set; }

    /// <summary>
    /// ADR-021 Capa 7: fecha en que el usuario dice que se cobro el pago. OPCIONAL. null = ahora (UTC),
    /// que es el comportamiento previo. El backend la lleva a UTC antes de persistir (la columna en
    /// Postgres es timestamptz y EF exige Kind=Utc). Permitir fechar en el pasado es deliberado: un
    /// cobro se registra a veces dias despues de recibirlo; eso reubica el movimiento en el mes real
    /// de caja, que es el comportamiento contable correcto.
    /// </summary>
    public DateTime? PaidAt { get; set; }

    /// <summary>
    /// ADR-024 item 4 (vinculo basico cobro<->factura, 2026-06-12): PublicId de la factura a la que el
    /// usuario quiere asociar este cobro, de forma INFORMATIVA. OPCIONAL: null = cobro sin vinculo (igual
    /// que hoy). Si viene, el backend lo resuelve a la factura y valida que pertenezca a la MISMA reserva
    /// del cobro (si no, 400). El vinculo NO toca saldos ni congela el cobro (los guards no lo miran).
    /// </summary>
    public string? LinkedInvoicePublicId { get; set; }
}
public class UpdatePaymentRequest
{
    public decimal Amount { get; set; }
    public string Method { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string? Notes { get; set; }
}
