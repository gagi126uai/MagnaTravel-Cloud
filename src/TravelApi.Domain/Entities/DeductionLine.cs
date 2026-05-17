using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>
/// FC1 BC-1 (ADR-002 §2.3 / §2.9, 2026-05-13): linea de deduccion tipificada
/// dentro de una <see cref="OperatorRefundAllocation"/>. Captura UN cargo o
/// retencion individual del operador con su documentacion asociada.
///
/// Por que 1:N en lugar de columnas planas:
///  - <see cref="DeductionKind"/> tiene 10 valores con reglas de validacion
///    diferentes (retenciones AR exigen certificado, ForeignTax exige country,
///    costos operativos exigen comprobante o justificacion).
///  - El operador puede aplicar varias deducciones distintas en un mismo allocation
///    (ej. AdministrativeFee + IvaWithholding + IIBBWithholding).
///
/// Validaciones de dominio (FC1.2, fuera de scope FC1.1):
///   - Amount &gt; 0 (INV-112, tambien validado con CHECK SQL <c>chk_deduction_amount_positive</c>).
///   - Si Kind in {IvaWithholding, IvaPerception, IncomeTaxWithholding,
///     IIBBWithholding, IIBBPerception} -&gt; CertificateNumber + CertificateDate
///     + CertificatePdfUrl obligatorios (INV-103). Solo aplica si la agencia es RI
///     (INV-116) — Monotributo NO computa credito fiscal.
///   - Si Kind in {IIBBWithholding, IIBBPerception} -&gt; Jurisdiction obligatorio (INV-104).
///   - Si Kind = ForeignTax -&gt; ForeignCountryCode + Description obligatorios (INV-107).
///   - Si Kind in {AdministrativeFee, BankingCost, CancellationPenalty} -&gt;
///     SupportingDocumentRef o (JustificationComment + MissingFiscalSupport=true) (INV-108).
///   - Si Kind = Other -&gt; Comment + RequiresAccountingReview=true (INV-109).
///   - Si Agency.TaxCondition = Monotributo O Supplier.TaxCondition = Monotributo
///     -&gt; bloquear kinds 10..39 (no aplica regimen retenciones AR, INV-105/INV-115).
/// </summary>
public class DeductionLine : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public int OperatorRefundAllocationId { get; set; }
    public OperatorRefundAllocation Allocation { get; set; } = null!;

    public DeductionKind Kind { get; set; }

    /// <summary>Monto positivo. INV-112 + CHECK SQL <c>chk_deduction_amount_positive</c>.</summary>
    public decimal Amount { get; set; }

    // ===== Campos solo para retenciones/percepciones AR (INV-103/104) =====

    [MaxLength(50)]
    public string? CertificateNumber { get; set; }

    /// <summary>URL del PDF del certificado fiscal. Almacenamiento via MinIO (presigned).</summary>
    [MaxLength(500)]
    public string? CertificatePdfUrl { get; set; }

    public DateTime? CertificateDate { get; set; }

    /// <summary>Jurisdiccion (provincia ISO) cuando aplica IIBB. INV-104.</summary>
    [MaxLength(50)]
    public string? Jurisdiction { get; set; }

    // ===== Campos solo para ForeignTax (INV-107) =====

    /// <summary>Codigo ISO 3166-1 alpha-2 (BR, US, ES, etc.) cuando Kind = ForeignTax.</summary>
    [MaxLength(2)]
    public string? ForeignCountryCode { get; set; }

    /// <summary>Descripcion del impuesto extranjero. Sin esquema fiscal AR.</summary>
    [MaxLength(500)]
    public string? Description { get; set; }

    // ===== Campos para costos operativos (INV-108) =====

    /// <summary>Numero/ref del comprobante que respalda el costo (factura del operador, recibo, etc.).</summary>
    [MaxLength(200)]
    public string? SupportingDocumentRef { get; set; }

    [MaxLength(1000)]
    public string? JustificationComment { get; set; }

    /// <summary>
    /// Flag explicito: el cashier reconoce que NO tiene respaldo fiscal del costo.
    /// Genera alerta para el contador (riesgo deducibilidad Ganancias bajo RI futuro).
    /// </summary>
    public bool MissingFiscalSupport { get; set; }

    // ===== Campos para Kind = Other (INV-109) =====

    [MaxLength(1000)]
    public string? Comment { get; set; }

    /// <summary>Flag de marca: contador debe revisar antes de cierre mensual.</summary>
    public bool RequiresAccountingReview { get; set; }
}
