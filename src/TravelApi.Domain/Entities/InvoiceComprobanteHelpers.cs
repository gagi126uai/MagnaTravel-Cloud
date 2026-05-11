namespace TravelApi.Domain.Entities;

/// <summary>
/// Categoria fiscal del comprobante AFIP. Refleja la naturaleza del documento
/// independientemente de la letra (A/B/C/M).
/// </summary>
public enum InvoiceComprobanteCategory
{
    /// <summary>Tipo no reconocido por el sistema (deberia loguearse como dato sucio).</summary>
    Unknown = 0,
    /// <summary>Factura (tipo 1=A, 6=B, 11=C, 51=M).</summary>
    Invoice = 1,
    /// <summary>Nota de Debito (tipo 2=A, 7=B, 12=C, 52=M).</summary>
    DebitNote = 2,
    /// <summary>Nota de Credito (tipo 3=A, 8=B, 13=C, 53=M).</summary>
    CreditNote = 3,
}

/// <summary>
/// Helper centralizado para clasificar los `cbteTipo` de AFIP.
///
/// Existe porque la discriminacion estaba duplicada y desalineada en varios
/// servicios (MovementsService, InvoiceService, PaymentService, TreasuryService,
/// CustomerService, InvoicePdfService, MappingProfile). Algunas implementaciones
/// olvidaban las Notas de Debito (tipo 2/7/12/52), clasificandolas como
/// "factura" en UI y permitiendo anulacion incorrecta.
///
/// IMPORTANTE: las queries LINQ-to-SQL (Sum, Where) NO pueden invocar estos
/// helpers directamente porque EF no las traduce. En esos casos sigue la
/// expansion inline (TipoComprobante == 3 || ... == 53) — el helper se usa
/// para proyecciones in-memory, switch de logica de aplicacion, validaciones
/// y construccion de DTOs.
/// </summary>
public static class InvoiceComprobanteHelpers
{
    /// <summary>
    /// Tipos de comprobante que el flujo de anulacion automatica (ProcessAnnulmentJob)
    /// sabe convertir a Nota de Credito.
    ///
    /// Restriccion deliberada: solo Facturas A/B/C. Factura M (51) NO esta soportada
    /// porque la implementacion actual de InvoiceService no tiene el mapeo 51 -> 53
    /// ni los tests de regresion. NDs (2,7,12,52) y NCs (3,8,13,53) tampoco deben
    /// ser anuladas desde la UI nueva — son resultado o ajuste de otro comprobante.
    /// </summary>
    public static bool IsSupportedForAnnulment(int tipoComprobante)
        => tipoComprobante is 1 or 6 or 11;

    /// <summary>True si es una Factura (incluye Factura M aunque la anulacion no este soportada).</summary>
    public static bool IsInvoice(int tipoComprobante)
        => tipoComprobante is 1 or 6 or 11 or 51;

    /// <summary>True si es una Nota de Debito (A/B/C/M).</summary>
    public static bool IsDebitNote(int tipoComprobante)
        => tipoComprobante is 2 or 7 or 12 or 52;

    /// <summary>True si es una Nota de Credito (A/B/C/M).</summary>
    public static bool IsCreditNote(int tipoComprobante)
        => tipoComprobante is 3 or 8 or 13 or 53;

    /// <summary>Clasifica el tipo de comprobante en su categoria fiscal.</summary>
    public static InvoiceComprobanteCategory Categorize(int tipoComprobante) =>
        tipoComprobante switch
        {
            1 or 6 or 11 or 51 => InvoiceComprobanteCategory.Invoice,
            2 or 7 or 12 or 52 => InvoiceComprobanteCategory.DebitNote,
            3 or 8 or 13 or 53 => InvoiceComprobanteCategory.CreditNote,
            _ => InvoiceComprobanteCategory.Unknown,
        };

    /// <summary>
    /// Devuelve el cbteTipo de la Nota de Credito correspondiente a una factura,
    /// o null si la factura origen no soporta anulacion automatica.
    ///
    /// Solo cubre Facturas A/B/C. Para NDs (2,7,12,52), Facturas M (51) y
    /// NCs (3,8,13,53), retorna null y el caller debe rechazar la operacion.
    /// </summary>
    public static int? GetCreditNoteTypeForInvoice(int invoiceTipo) =>
        invoiceTipo switch
        {
            1 => 3,    // Factura A -> NC A
            6 => 8,    // Factura B -> NC B
            11 => 13,  // Factura C -> NC C
            _ => null,
        };
}

/// <summary>
/// Discriminadores expuestos en MovementDto.Kind. Se centralizan aca para
/// evitar string literals duplicados entre servicio, controller y tests.
/// </summary>
public static class MovementKinds
{
    public const string Payment = "payment";
    public const string Invoice = "invoice";
    public const string DebitNote = "debit_note";
    public const string CreditNote = "credit_note";
    public const string CreditNoteReversal = "credit_note_reversal";
}
