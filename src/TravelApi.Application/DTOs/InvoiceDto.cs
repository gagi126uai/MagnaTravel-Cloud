namespace TravelApi.Application.DTOs;

public class InvoiceDto
{
    public Guid PublicId { get; set; }

    public Guid? ReservaPublicId { get; set; }
    public ReservaDto? Reserva { get; set; } // Navigation for frontend "Reserva" and "Client" columns
    public int TipoComprobante { get; set; } 
    public int PuntoDeVenta { get; set; }
    public long NumeroComprobante { get; set; }
    public decimal ImporteTotal { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CAE { get; set; }
    public string? Resultado { get; set; }
    public string? Observaciones { get; set; }
    public bool WasForced { get; set; }
    public string? ForceReason { get; set; }
    public string? ForcedByUserId { get; set; }
    public string? ForcedByUserName { get; set; }
    public DateTime? ForcedAt { get; set; }
    public decimal OutstandingBalanceAtIssuance { get; set; }
    public string InvoiceType { get; set; } = string.Empty; // Keep for convenience if needed

    // B1.15 (2026-05-11): para que UI distinga Factura/NC/ND y muestre relacion
    // factura↔NC. AnnulmentStatus = "None"/"Pending"/"Succeeded"/"Failed".
    // OriginalInvoice* != null cuando este comprobante es NC/ND emitida sobre
    // una factura previa.
    public string AnnulmentStatus { get; set; } = "None";
    public Guid? OriginalInvoicePublicId { get; set; }
    public long? OriginalInvoiceNumeroComprobante { get; set; }
    public int? OriginalInvoiceTipoComprobante { get; set; }
    public int? OriginalInvoicePuntoDeVenta { get; set; }

    /// <summary>
    /// Hallazgo auditoria ERP #9 (2026-06-13): aviso NO bloqueante que se completa al CREAR la factura
    /// cuando la suma de los items facturados no coincide con lo vendido confirmado de la reserva en esa
    /// moneda (ver <c>InvoiceMismatchChecker</c>). <c>null</c> cuando cuadra (caso normal). El operador
    /// puede facturar igual: el campo solo informa el descuadre para que lo revise. NO se persiste en
    /// la entidad <c>Invoice</c> — es transitorio, solo viaja en la respuesta de creacion.
    /// </summary>
    public string? Warning { get; set; }
}
