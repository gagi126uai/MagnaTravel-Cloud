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

    /// <summary>
    /// Paso 5 (2026-06-24): vencimiento del CAE para mostrarlo en la linea de la factura del Estado de
    /// Cuenta (junto al numero de comprobante y el CAE). Es un dato FISCAL no sensible (no es costo ni
    /// margen). Mapea por convencion desde <c>Invoice.VencimientoCAE</c>. Null mientras la factura no
    /// este emitida (en proceso o rechazada). El front lo lee como <c>invoice.vencimientoCAE</c>.
    /// </summary>
    public DateTime? VencimientoCAE { get; set; }
    public string? Resultado { get; set; }
    public string? Observaciones { get; set; }
    public bool WasForced { get; set; }
    public string? ForceReason { get; set; }
    public string? ForcedByUserId { get; set; }
    public string? ForcedByUserName { get; set; }
    public DateTime? ForcedAt { get; set; }
    public decimal OutstandingBalanceAtIssuance { get; set; }
    public string InvoiceType { get; set; } = string.Empty; // Keep for convenience if needed

    /// <summary>
    /// Tarea 2026-07-16 (label del desplegable de facturas, ej. "Factura C 0001-00000051 — $ 125.000,50"
    /// vs "US$ 500,00"): moneda ISO 4217 de la factura ("ARS"/"USD"), para que el frontend sepa que
    /// simbolo/formato usar sin tener que interpretar el codigo ARCA.
    ///
    /// <para><b>De donde sale</b>: <c>Invoice.MonId</c> guarda la moneda en formato ARCA ("PES"/"DOL"),
    /// no ISO. Se traduce con <c>ArcaCurrencyMapper.ToIso</c> — el mismo helper que ya usa
    /// <c>ReservaService</c> para agrupar el extracto de cuenta por moneda (fuente unica, no se duplica
    /// la tabla de mapeo). Si <c>MonId</c> viene vacio o con un codigo que el mapper no reconoce (dato
    /// legacy raro), cae a <c>Monedas.ARS</c>: mismo criterio legacy que el extracto (todo lo viejo se
    /// factura en pesos).</para>
    /// </summary>
    public string Currency { get; set; } = TravelApi.Domain.Entities.Monedas.ARS;

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

    /// <summary>
    /// Sugerencia para pre-seleccionar la factura al cancelar UN servicio de la reserva (en vez de
    /// que el usuario adivine en un desplegable con varias facturas activas). Son los <c>PublicId</c>
    /// de los servicios a los que apunta esta factura, segun la trazabilidad UNIDA de dos fuentes (ver
    /// <c>ReservaService.PopulateInvoiceServicePublicIdsAsync</c>): la polimorfica
    /// <c>InvoiceItem.SourceServicePublicId</c> (cubre los 6 tipos de servicio) y la legacy
    /// <c>InvoiceItem.SourceServicioReservaId</c> (solo el servicio generico, FC1.3/ADR-009). Sin
    /// duplicados, nunca <c>null</c> (lista vacia cuando no hay dato).
    ///
    /// <para><b>Como se puebla hoy (2026-07-16)</b>: al crear una factura desde la sugerencia de items
    /// (<c>InvoiceSuggestedItemsBuilder</c>), la trazabilidad polimorfica viaja completa desde el front
    /// y queda grabada. Facturas armadas a mano (sin usar la sugerencia) o comprobantes anteriores a
    /// esta fecha siguen sin trazabilidad: para esos casos la lista queda vacia, y el frontend cae al
    /// desplegable manual de siempre — nunca rompe.</para>
    /// </summary>
    public List<Guid> ServicePublicIds { get; set; } = new();
}
