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

    /// <summary>
    /// ADR-013 §3.9 (M1, 2026-06-01): devuelve el cbteTipo de la Nota de Debito que
    /// corresponde a un comprobante asociado, derivando la LETRA (A/B/C/M) del tipo
    /// del comprobante asociado — NO de la condicion fiscal del emisor.
    ///
    /// <para><b>Por que cubre TANTO facturas COMO notas de credito como origen</b>:
    /// una ND se puede asociar a la factura original (caso del MVP: penalidad de
    /// cancelacion -> ND C asociada a la factura C) o a una NC previa (caso
    /// CorrectCreditNote, futuro). En ambos casos la ND debe tener la MISMA letra que
    /// el comprobante asociado (RG 4540: comprobante asociado). Por eso mapeamos las
    /// dos familias a la misma letra:
    /// <list type="bullet">
    /// <item>A: factura 1, ND 2, NC 3  -> ND A = 2</item>
    /// <item>B: factura 6, ND 7, NC 8  -> ND B = 7</item>
    /// <item>C: factura 11, ND 12, NC 13 -> ND C = 12</item>
    /// <item>M: factura 51, ND 52, NC 53 -> ND M = 52</item>
    /// </list></para>
    ///
    /// <para><b>El bug que arregla</b>: antes el switch inline de AfipService solo
    /// contemplaba los tipos de NC (3/8/13/53) como origen de una ND. Una ND asociada
    /// a una FACTURA C=11 (el caso EXACTO del MVP) no matcheaba ninguna rama y salia
    /// con el tipo de la factura (11) en vez de ND C (12). Este helper cubre las dos
    /// familias para que ese caso derive 12 correctamente.</para>
    ///
    /// <para>Retorna null si el tipo asociado no es uno de los conocidos (el caller
    /// debe rechazar la operacion en vez de emitir un comprobante con tipo dudoso).</para>
    /// </summary>
    public static int? GetDebitNoteTypeForAssociated(int associatedTipo) =>
        associatedTipo switch
        {
            1 or 2 or 3 => 2,      // Factura A / ND A / NC A  -> ND A
            6 or 7 or 8 => 7,      // Factura B / ND B / NC B  -> ND B
            11 or 12 or 13 => 12,  // Factura C / ND C / NC C  -> ND C
            51 or 52 or 53 => 52,  // Factura M / ND M / NC M  -> ND M
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
