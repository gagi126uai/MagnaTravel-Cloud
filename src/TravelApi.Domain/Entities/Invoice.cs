using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

public class Invoice : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // AFIP Data
    public int TipoComprobante { get; set; } // 1 (A), 6 (B), 11 (C)
    public int PuntoDeVenta { get; set; }
    public long NumeroComprobante { get; set; }
    
    public string? CAE { get; set; }
    public DateTime? VencimientoCAE { get; set; }
    
    public string? Resultado { get; set; } // A (Aprobado), R (Rechazado), P (Parcial)
    public string? Observaciones { get; set; } // Error messages from AFIP

    // Financial Data
    [Column(TypeName = "decimal(18,2)")]
    public decimal ImporteTotal { get; set; }
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal ImporteNeto { get; set; }
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal ImporteIva { get; set; }

    public bool WasForced { get; set; }
    public string? ForceReason { get; set; }
    public string? ForcedByUserId { get; set; }
    public string? ForcedByUserName { get; set; }
    public DateTime? ForcedAt { get; set; }

    // B1.15 Fase 1: trazabilidad de quien y cuando se emitio la factura.
    // Nullable para soportar backfill de historicos (Name="(legacy)", Id=null).
    public string? IssuedByUserId { get; set; }
    public string? IssuedByUserName { get; set; }
    public DateTime? IssuedAt { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal OutstandingBalanceAtIssuance { get; set; }

    // Snapshots (JSON) for Immutability
    public string? AgencySnapshot { get; set; }
    public string? CustomerSnapshot { get; set; }
    
    // Relationships
    public int? ReservaId { get; set; }
    public Reserva? Reserva { get; set; }

    // Navigation for Items/Tributes
    public ICollection<InvoiceItem> Items { get; set; } = new List<InvoiceItem>();
    public ICollection<InvoiceTribute> Tributes { get; set; } = new List<InvoiceTribute>();

    // Self-Referencing for Credit/Debit Notes
    public int? OriginalInvoiceId { get; set; }
    [ForeignKey("OriginalInvoiceId")]
    public Invoice? OriginalInvoice { get; set; }
}
