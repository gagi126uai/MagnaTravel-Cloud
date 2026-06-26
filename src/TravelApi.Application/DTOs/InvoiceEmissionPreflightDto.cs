namespace TravelApi.Application.DTOs;

/// <summary>
/// Fase 4 (2026-06-26): PRE-CHEQUEO de emisión de factura. El vendedor NO elige la letra: la deriva el
/// backend (<c>InvoiceTypeResolver.ResolveSaleInvoiceType(emisor, receptor)</c>, la MISMA matriz fiscal de
/// fondo confirmada por dueño + contador que usa <c>AfipService.CreatePendingInvoice</c>). Este DTO es un
/// PREVIEW de solo lectura para AVISAR antes de emitir, así no se manda a ARCA un comprobante que va a rebotar.
///
/// <para><b>Único bloqueo duro</b>: cliente que recibiría <b>Factura A</b> (RI o Monotributo) pero SIN CUIT
/// válido (DocTipo ARCA != 80). Ese es el caso que ARCA rechaza con seguridad. Todo lo demás es informativo
/// (regla de oro CONSERVADORA: ante cualquier duda que NO sea "A sin CUIT", <see cref="Allowed"/> = true; no
/// se frena la emisión).</para>
///
/// <para><b>Fuera de alcance</b>: el caso Exterior / Factura E queda a confirmar con el contador (no se evalúa
/// acá). Este preview NO muta nada ni reabre la matriz fiscal: solo la consume.</para>
/// </summary>
public class InvoiceEmissionPreflightDto
{
    /// <summary>Letra que se EMITIRÍA hoy: "A" / "B" / "C". Coincide con lo que emitiría CreatePendingInvoice.</summary>
    public string WillEmitLetter { get; set; } = "B";

    /// <summary>true si se puede emitir (no hay bloqueo duro). Solo false en el caso "A sin CUIT".</summary>
    public bool Allowed { get; set; } = true;

    /// <summary>Gravedad para el front: "block" (no se puede), "warn" (se puede pero revisar), "ok".</summary>
    public string Severity { get; set; } = "ok";

    /// <summary>Texto legible (sin datos sensibles) para mostrar. null cuando no hay nada que avisar.</summary>
    public string? Reason { get; set; }

    /// <summary>Condición de IVA del cliente (texto crudo de la ficha), para que el front la muestre.</summary>
    public string CustomerTaxCondition { get; set; } = string.Empty;

    /// <summary>Datos que faltan y bloquean. Hoy a lo sumo ["CUIT"] (caso A sin CUIT). Vacío si no falta nada.</summary>
    public List<string> MissingData { get; set; } = new();
}
