namespace TravelApi.Application.DTOs.Cancellation;

/// <summary>
/// FC1.3 Fase 3 (ADR-010, 2026-05-29): contrato de salida de una fila de la bandeja
/// de reconciliacion de NC parciales con recibos vivos. Lo consume el frontend
/// (pantalla clon de la inbox de aprobaciones).
///
/// <para>NO expone entidades de persistencia: es un DTO plano con los datos que la
/// pantalla necesita para que el encargado ubique el caso y acomode los recibos.</para>
/// </summary>
public class PartialCreditNoteReconciliationDto
{
    /// <summary>Identificador publico del caso (para llamar al endpoint de cierre).</summary>
    public Guid PublicId { get; set; }

    /// <summary>"Pending" | "Resolved". Estado del caso (string, igual que en BD).</summary>
    public string Status { get; set; } = string.Empty;

    public DateTime OpenedAt { get; set; }
    public string? OpenedByUserName { get; set; }

    // ===== Contexto fiscal / negocio (para que el encargado ubique el caso) =====

    /// <summary>Numero formateado de la NC parcial (ej. "00003-00000123").</summary>
    public string CreditNoteNumber { get; set; } = string.Empty;

    /// <summary>Numero formateado de la factura original (ej. "00003-00000099").</summary>
    public string OriginalInvoiceNumber { get; set; } = string.Empty;

    /// <summary>Monto fiscal acreditado por la NC parcial (informativo, NO el monto a devolver).</summary>
    public decimal FiscalAmountCredited { get; set; }

    /// <summary>Moneda ISO del caso (ARS/USD).</summary>
    public string Currency { get; set; } = "ARS";

    /// <summary>PublicId de la reserva, si existe (para linkear desde la UI).</summary>
    public Guid? ReservaPublicId { get; set; }

    /// <summary>Nombre de la reserva / cliente, para que el encargado sepa de que caso se trata.</summary>
    public string? ReservaName { get; set; }

    // ===== Cierre =====

    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedByUserName { get; set; }
    public string? ResolutionNotes { get; set; }
    public bool ClosedWithLiveReceipts { get; set; }
    public bool FourEyesBypassApplied { get; set; }

    /// <summary>Los recibos del snapshot, CON su estado vigente leido en vivo de PaymentReceipts.</summary>
    public List<PartialCreditNoteReconciliationReceiptDto> Receipts { get; set; } = new();
}

/// <summary>
/// FC1.3 Fase 3 (ADR-010): un recibo dentro de un caso de reconciliacion. Expone el
/// snapshot del momento de apertura + el estado VIGENTE del recibo (leido en vivo).
/// </summary>
public class PartialCreditNoteReconciliationReceiptDto
{
    /// <summary>
    /// PublicId del PAYMENT del recibo. El frontend lo usa para llamar al endpoint
    /// existente de anular recibo (<c>POST /api/payments/{paymentPublicId}/receipt/void</c>),
    /// que resuelve por Payment, no por receipt (ADR-010 N1).
    /// </summary>
    public Guid PaymentPublicId { get; set; }

    /// <summary>Id interno del recibo (snapshot).</summary>
    public int ReceiptId { get; set; }

    /// <summary>Numero del recibo (ej. "REC-000123"), para mostrar en la lista.</summary>
    public string? ReceiptNumber { get; set; }

    /// <summary>Monto del recibo al momento del snapshot.</summary>
    public decimal Amount { get; set; }

    /// <summary>Estado del recibo cuando se abrio el caso ("Issued").</summary>
    public string StatusAtOpen { get; set; } = string.Empty;

    /// <summary>
    /// Estado VIGENTE del recibo, leido en vivo de PaymentReceipts ("Issued" | "Voided").
    /// Esto es lo que permite a la UI mostrar "2 de 3 recibos ya anulados".
    /// </summary>
    public string CurrentStatus { get; set; } = string.Empty;

    /// <summary>Fecha de anulacion del recibo, si ya se anulo.</summary>
    public DateTime? VoidedAt { get; set; }

    /// <summary>Quien anulo el recibo, si ya se anulo.</summary>
    public string? VoidedByUserName { get; set; }
}

/// <summary>
/// FC1.3 Fase 3 (ADR-010 §5.3): body del endpoint de cierre manual de un caso.
/// </summary>
public class ResolvePartialCreditNoteReconciliationRequest
{
    /// <summary>
    /// Motivo del cierre. Obligatorio si:
    ///  - el cierre es self-close con bypass de admin unico (>= 100 chars, G5), o
    ///  - el caso se cierra con recibos todavia vivos / Issued (R4).
    /// Opcional en el cierre limpio (otra persona cierra y todos los recibos ya anulados).
    /// </summary>
    public string? Notes { get; set; }
}

/// <summary>
/// FC1.3 Fase 3 (ADR-010): query de listado de la bandeja. Hereda paginacion estandar.
/// Filtro por estado + mes (estilo MonthNavigator del frontend).
/// </summary>
public class PartialCreditNoteReconciliationListQuery : PagedQuery
{
    /// <summary>"pending" (default) | "resolved" | "all".</summary>
    public string Status { get; set; } = "pending";

    /// <summary>Anio del filtro mensual (opcional). Si viene, tambien debe venir Month.</summary>
    public int? Year { get; set; }

    /// <summary>Mes del filtro mensual 1..12 (opcional). Si viene, tambien debe venir Year.</summary>
    public int? Month { get; set; }

    public PartialCreditNoteReconciliationListQuery()
    {
        SortBy = "openedAt";
        SortDir = "desc";
    }
}
