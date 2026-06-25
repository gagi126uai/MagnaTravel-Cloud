namespace TravelApi.Application.DTOs;

/// <summary>
/// H2 (2026-06-24): estado fiscal CLARO de una factura para que el front lo muestre sin conocer los
/// codigos internos de ARCA. Es el resultado del POLL que el front hace despues de emitir: la emision
/// es asincrona (POST /invoices encola; un job pide el CAE en segundo plano), asi que el front consulta
/// este endpoint hasta saber como termino.
///
/// <para><b>Solo lectura</b>: este DTO NO dispara ninguna emision; solo refleja el estado actual ya
/// persistido en la factura. Tres situaciones posibles, segun <see cref="Status"/>:
/// <list type="bullet">
///   <item><c>InProcess</c>: encolada, esperando a ARCA. CAE/numero vienen vacios.</item>
///   <item><c>Issued</c>: ARCA aprobo -> tipo + punto de venta + numero + CAE + vencimiento + total + moneda.</item>
///   <item><c>Rejected</c>: ARCA rechazo -> <see cref="RejectionReason"/> trae el motivo legible.</item>
/// </list></para>
/// </summary>
public class InvoiceFiscalStatusDto
{
    public Guid PublicId { get; set; }
    public Guid? ReservaPublicId { get; set; }

    /// <summary>"InProcess" / "Issued" / "Rejected" (nombre del enum InvoiceFiscalStatus).</summary>
    public string Status { get; set; } = string.Empty;

    // --- Datos del comprobante (solo significativos cuando Status == "Issued") ---

    /// <summary>Letra del comprobante: "A" / "B" / "C" / "M" / "UNK".</summary>
    public string InvoiceType { get; set; } = string.Empty;
    public int TipoComprobante { get; set; }
    public int PuntoDeVenta { get; set; }
    public long NumeroComprobante { get; set; }
    public string? CAE { get; set; }
    public DateTime? VencimientoCAE { get; set; }
    public decimal ImporteTotal { get; set; }

    /// <summary>Moneda del comprobante en codigo ARCA ("PES" / "DOL"). Default "PES".</summary>
    public string MonId { get; set; } = "PES";

    /// <summary>
    /// Motivo de rechazo de ARCA, legible (ya traducido por el backend). Solo viene cuando
    /// Status == "Rejected"; null en los otros estados. El front lo usa para "Corregir y reintentar".
    /// </summary>
    public string? RejectionReason { get; set; }

    /// <summary>Cuando se creo/encolo la factura.</summary>
    public DateTime CreatedAt { get; set; }
}
