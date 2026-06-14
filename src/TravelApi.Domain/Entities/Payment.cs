using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

public static class PaymentEntryTypes
{
    public const string Payment = "Payment";
    public const string CreditNoteReversal = "CreditNoteReversal";
}

public class Payment : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    // ====================================================================================
    // ADR-021 (multimoneda + cobro cruzado, 2026-06-08). En Capa 1 son SOLO modelo+columna:
    // nadie los setea todavia (el default deja todo en ARS no cruzado = identico a hoy).
    // El registro de pago que los completa y el calculo que los usa son capas siguientes.
    // ====================================================================================

    /// <summary>
    /// ADR-021: moneda REAL del pago, lo que efectivamente entro a caja. Es sagrada: la caja
    /// NO se convierte. NOT NULL, default ARS (la migracion pone el default a nivel BD para
    /// que los pagos legacy queden en pesos automaticamente). Valores: <c>Monedas.Soportadas</c>.
    /// </summary>
    [MaxLength(3)]
    public string Currency { get; set; } = Monedas.ARS;

    /// <summary>
    /// ADR-021: moneda del SALDO al que se imputa el pago. <c>null</c> = se imputa a su propia
    /// moneda (<see cref="Currency"/>), pago NO cruzado. Si difiere de <see cref="Currency"/>,
    /// el pago es cruzado y el bloque de TC de abajo pasa a ser obligatorio (validacion Capa 2).
    /// </summary>
    [MaxLength(3)]
    public string? ImputedCurrency { get; set; }

    /// <summary>
    /// ADR-021: tipo de cambio aplicado en un pago cruzado. Convencion FIJA (§2.2bis): unidades
    /// de ARS por 1 USD (ej. 1 USD = 1000 ARS -> 1000.000000), misma orientacion que
    /// <c>Invoice.MonCotiz</c>. Precision (18,6) alineada con MonCotiz. <c>null</c> si no hubo conversion.
    /// </summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal? ExchangeRate { get; set; }

    /// <summary>
    /// ADR-021: origen del tipo de cambio. Reusa el enum de ADR-012/ADR-002 (BCRA/BNA/AFIP/Manual...).
    /// Se persiste como <c>int</c>. <c>null</c> si no hubo conversion. En un pago cruzado nunca puede
    /// quedar <c>null</c> ni <c>Unset</c> (validacion Capa 2, espejo del CHECK fiscal de FC1).
    /// </summary>
    public ExchangeRateSource? ExchangeRateSource { get; set; }

    /// <summary>ADR-021: fecha del tipo de cambio aplicado. <c>null</c> si no hubo conversion.</summary>
    public DateTime? ExchangeRateAt { get; set; }

    /// <summary>
    /// ADR-021: monto EQUIVALENTE que baja del saldo de <see cref="ImputedCurrency"/> tras
    /// aplicar el TC (§2.2bis). <c>null</c> si no hubo conversion (entonces se imputa
    /// <see cref="Amount"/> sobre <see cref="Currency"/>). Precision (18,2) = escala canonica de plata.
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? ImputedAmount { get; set; }

    public DateTime PaidAt { get; set; } = DateTime.UtcNow;

    public string Method { get; set; } = "Transfer"; // Cash, Transfer, Card
    public string? Reference { get; set; } // Transaction ID, Check #, etc.

    public string Status { get; set; } = "Paid"; // Paid, Pending, Cancelled

    public string? Notes { get; set; }

    public string EntryType { get; set; } = PaymentEntryTypes.Payment;
    public bool AffectsCash { get; set; } = true;

    // B1.15 Fase 1: trazabilidad de quien y cuando se registro el pago.
    // CreatedAt es NOT NULL (default DateTime.UtcNow / CURRENT_TIMESTAMP).
    // CreatedBy* es nullable para soportar backfill de historicos.
    public string? CreatedByUserId { get; set; }
    public string? CreatedByUserName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Soft Delete
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }

    // Direct link to Reserva (preferred)
    public int? ReservaId { get; set; }
    public Reserva? Reserva { get; set; }

    // Link via Servicio (for backwards compatibility)
    public int? ServicioReservaId { get; set; }
    public ServicioReserva? ServicioReserva { get; set; }

    public int? RelatedInvoiceId { get; set; }
    public Invoice? RelatedInvoice { get; set; }

    // ====================================================================================
    // ADR-024 item 4 (vinculo basico cobro<->factura, ex ADR-023 T2.6, 2026-06-12).
    // ====================================================================================

    /// <summary>
    /// ADR-024: factura a la que el usuario asocia ESTE cobro, de forma INFORMATIVA. Es distinto de
    /// <see cref="RelatedInvoiceId"/>: aquel es el vinculo FISCAL/ECONOMICO (lo miran los guards de
    /// borrado/edicion y la reconciliacion de NC), este es solo un dato de presentacion ("este pago
    /// corresponde a tal factura"). DELIBERADAMENTE los guards y la reconciliacion NO miran este campo
    /// (review B1 de ADR-023): vincular un cobro a una factura por aca NO lo congela ni toca saldos. Sin
    /// indice unico: una factura puede tener varios cobros vinculados y un cobro vincularse o no.
    /// </summary>
    public int? LinkedInvoiceId { get; set; }
    public Invoice? LinkedInvoice { get; set; }

    public int? OriginalPaymentId { get; set; }
    public Payment? OriginalPayment { get; set; }
    public ICollection<Payment> Reversals { get; set; } = new List<Payment>();

    // ====================================================================================
    // FC4 (saldo a favor aplicado a otra reserva, 2026-06-14).
    // ====================================================================================

    /// <summary>
    /// FC4: cuando un saldo a favor del cliente se APLICA como pago de OTRA reserva (kind
    /// AppliedToNewBooking), el sistema crea un Payment "puente" POSITIVO en la reserva destino
    /// (no mueve caja, baja la deuda). Esta FK lo ata al <see cref="ClientCreditWithdrawal"/> que lo
    /// origino, de forma estructural (no por texto en Notes). Es lo que distingue el puente de
    /// aplicacion de un cobro real y del puente de sobrepago (ese usa <see cref="OriginalPaymentId"/>).
    ///
    /// <para><b>Por que una FK propia y no reusar OriginalPaymentId</b>: el puente de sobrepago apunta
    /// a un Payment fuente; este apunta a un withdrawal del bolsillo del cliente. Son orígenes
    /// distintos. Mantenerlos en columnas separadas hace que <c>IsOverpaymentBridge</c> y
    /// <c>IsAppliedCreditBridge</c> nunca se confundan entre sí.</para>
    /// </summary>
    public int? AppliedFromCreditWithdrawalId { get; set; }
    public ClientCreditWithdrawal? AppliedFromCreditWithdrawal { get; set; }

    public PaymentReceipt? Receipt { get; set; }
}
