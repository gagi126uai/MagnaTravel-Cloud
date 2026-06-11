namespace TravelApi.Application.DTOs;

public class PaymentDto
{
    public Guid PublicId { get; set; }
    public decimal Amount { get; set; }
    public DateTime PaidAt { get; set; }
    public string Method { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public string Status { get; set; } = "Paid";
    public Guid? ReservaPublicId { get; set; }
    public string? NumeroReserva { get; set; }
    public string EntryType { get; set; } = string.Empty;
    public bool AffectsCash { get; set; }
    public Guid? RelatedInvoicePublicId { get; set; }
    public Guid? OriginalPaymentPublicId { get; set; }
    public PaymentReceiptDto? Receipt { get; set; }

    // ====================================================================================
    // ADR-021 Capa 7 (multimoneda + cobro cruzado). Aditivos: un pago ARS no cruzado
    // (todo lo legacy) sale Currency="ARS", ImputedCurrency=null y el resto en null =
    // identico a lo que el front viejo ya interpretaba. El front los usa para: mostrar la
    // moneda en el historial; mostrar "imputado a US$ X" en un cobro cruzado; y DETECTAR
    // que un cobro es cruzado (ImputedCurrency != null && != Currency) para BLOQUEAR su
    // edicion (decision C). Sin estos campos un cobro cruzado se editaria como uno normal.
    // ====================================================================================

    /// <summary>ADR-021: moneda REAL del cobro (lo que entro a caja). Normalizada (nunca null), default ARS.</summary>
    public string Currency { get; set; } = "ARS";

    /// <summary>ADR-021: moneda del SALDO al que se imputo. null = no cruzado (se imputo a su propia moneda).</summary>
    public string? ImputedCurrency { get; set; }

    /// <summary>ADR-021: tipo de cambio aplicado (ARS por 1 USD). null si no hubo conversion.</summary>
    public decimal? ExchangeRate { get; set; }

    /// <summary>ADR-021: origen del tipo de cambio (enum <c>ExchangeRateSource</c> serializado como int). null si no hubo conversion.</summary>
    public int? ExchangeRateSource { get; set; }

    /// <summary>ADR-021: fecha del tipo de cambio aplicado. null si no hubo conversion.</summary>
    public DateTime? ExchangeRateAt { get; set; }

    /// <summary>ADR-021: monto equivalente que bajo del saldo imputado tras aplicar el TC. null si no hubo conversion.</summary>
    public decimal? ImputedAmount { get; set; }
}
