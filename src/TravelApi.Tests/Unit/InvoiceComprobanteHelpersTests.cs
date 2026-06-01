using TravelApi.Domain.Entities;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// 2026-05-11 (fix arca-tax-expert, fiscal critico): clasificacion de cbteTipo
/// AFIP. Antes la logica vivia inline duplicada en varios servicios y olvidaba
/// Notas de Debito (2,7,12,52), lo que permitia ofrecer "Anular" en UI sobre
/// una ND aprobada — error fiscal.
///
/// Estos tests pinean el contrato del helper centralizado. Si la matriz se
/// modifica, tienen que romperse para forzar revision contable.
/// </summary>
public class InvoiceComprobanteHelpersTests
{
    // ===================== Categorize =====================

    [Theory]
    [InlineData(1, InvoiceComprobanteCategory.Invoice)]    // Factura A
    [InlineData(6, InvoiceComprobanteCategory.Invoice)]    // Factura B
    [InlineData(11, InvoiceComprobanteCategory.Invoice)]   // Factura C
    [InlineData(51, InvoiceComprobanteCategory.Invoice)]   // Factura M
    [InlineData(2, InvoiceComprobanteCategory.DebitNote)]  // ND A
    [InlineData(7, InvoiceComprobanteCategory.DebitNote)]  // ND B
    [InlineData(12, InvoiceComprobanteCategory.DebitNote)] // ND C
    [InlineData(52, InvoiceComprobanteCategory.DebitNote)] // ND M
    [InlineData(3, InvoiceComprobanteCategory.CreditNote)] // NC A
    [InlineData(8, InvoiceComprobanteCategory.CreditNote)] // NC B
    [InlineData(13, InvoiceComprobanteCategory.CreditNote)]// NC C
    [InlineData(53, InvoiceComprobanteCategory.CreditNote)]// NC M
    [InlineData(0, InvoiceComprobanteCategory.Unknown)]
    [InlineData(99, InvoiceComprobanteCategory.Unknown)]
    [InlineData(201, InvoiceComprobanteCategory.Unknown)]  // Fac MiPyME — no soportado hoy.
    public void Categorize_KnownAndUnknownTypes(int tipo, InvoiceComprobanteCategory expected)
    {
        Assert.Equal(expected, InvoiceComprobanteHelpers.Categorize(tipo));
    }

    // ===================== Predicates =====================

    [Theory]
    [InlineData(1, true)]
    [InlineData(6, true)]
    [InlineData(11, true)]
    [InlineData(51, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, false)]
    public void IsInvoice(int tipo, bool expected)
    {
        Assert.Equal(expected, InvoiceComprobanteHelpers.IsInvoice(tipo));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(7, true)]
    [InlineData(12, true)]
    [InlineData(52, true)]
    [InlineData(1, false)]
    [InlineData(3, false)]
    public void IsDebitNote(int tipo, bool expected)
    {
        Assert.Equal(expected, InvoiceComprobanteHelpers.IsDebitNote(tipo));
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(8, true)]
    [InlineData(13, true)]
    [InlineData(53, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public void IsCreditNote(int tipo, bool expected)
    {
        Assert.Equal(expected, InvoiceComprobanteHelpers.IsCreditNote(tipo));
    }

    // ===================== IsSupportedForAnnulment =====================

    /// <summary>
    /// CRITICO: la lista de tipos soportados gobierna la habilitacion del flujo
    /// de anulacion automatica. Cualquier cambio aca debe ir acompanado del
    /// switch en InvoiceService.ProcessAnnulmentJob y de validacion fiscal.
    /// </summary>
    [Theory]
    [InlineData(1, true)]   // Factura A — soportada
    [InlineData(6, true)]   // Factura B — soportada
    [InlineData(11, true)]  // Factura C — soportada
    [InlineData(51, false)] // Factura M — NO soportada (sin mapeo a NC M en switch)
    [InlineData(2, false)]  // ND A — NO soportada
    [InlineData(7, false)]
    [InlineData(12, false)]
    [InlineData(52, false)]
    [InlineData(3, false)]  // NC A — NO soportada (no se "anula" una NC con otra NC)
    [InlineData(8, false)]
    [InlineData(13, false)]
    [InlineData(53, false)]
    [InlineData(0, false)]
    [InlineData(99, false)]
    public void IsSupportedForAnnulment(int tipo, bool expected)
    {
        Assert.Equal(expected, InvoiceComprobanteHelpers.IsSupportedForAnnulment(tipo));
    }

    // ===================== GetCreditNoteTypeForInvoice =====================

    [Theory]
    [InlineData(1, 3)]    // Factura A -> NC A
    [InlineData(6, 8)]    // Factura B -> NC B
    [InlineData(11, 13)]  // Factura C -> NC C
    public void GetCreditNoteTypeForInvoice_KnownInvoices_ReturnsMapping(int facturaTipo, int ncTipo)
    {
        Assert.Equal(ncTipo, InvoiceComprobanteHelpers.GetCreditNoteTypeForInvoice(facturaTipo));
    }

    [Theory]
    [InlineData(51)]  // Factura M — sin mapeo confirmado
    [InlineData(2)]   // ND A
    [InlineData(3)]   // NC A
    [InlineData(0)]
    [InlineData(99)]
    public void GetCreditNoteTypeForInvoice_UnsupportedTypes_ReturnsNull(int tipo)
    {
        Assert.Null(InvoiceComprobanteHelpers.GetCreditNoteTypeForInvoice(tipo));
    }

    // ===================== GetDebitNoteTypeForAssociated (ADR-013 §3.9, FIX M1) =====================

    /// <summary>
    /// ADR-013 §3.9 (M1, FIX bloqueante): la ND debe derivar su tipo del comprobante
    /// ASOCIADO, cubriendo TANTO facturas COMO notas de credito como origen.
    ///
    /// <para><b>El caso que prueba que el bug se arreglo</b>: factura C=11 -> ND C=12.
    /// Antes del fix, el switch inline de AfipService solo contemplaba NC como origen
    /// (13 -> 12) y NO la factura (11), asi que una ND asociada a una factura C salia con
    /// el tipo equivocado (11 = factura). Estos casos pinean el mapeo correcto.</para>
    /// </summary>
    [Theory]
    // Origen = FACTURA (el caso del MVP de ADR-013).
    [InlineData(1, 2)]    // Factura A -> ND A
    [InlineData(6, 7)]    // Factura B -> ND B
    [InlineData(11, 12)]  // Factura C -> ND C   <-- EL caso del MVP. Antes salia 11 (bug).
    [InlineData(51, 52)]  // Factura M -> ND M
    // Origen = NOTA DE CREDITO (no-regresion: lo que el switch ya hacia).
    [InlineData(3, 2)]    // NC A -> ND A
    [InlineData(8, 7)]    // NC B -> ND B
    [InlineData(13, 12)]  // NC C -> ND C
    [InlineData(53, 52)]  // NC M -> ND M
    public void GetDebitNoteTypeForAssociated_KnownTypes_ReturnsMapping(int associatedTipo, int expectedNdTipo)
    {
        Assert.Equal(expectedNdTipo, InvoiceComprobanteHelpers.GetDebitNoteTypeForAssociated(associatedTipo));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(201)]  // Factura MiPyME — no soportada hoy.
    public void GetDebitNoteTypeForAssociated_UnknownTypes_ReturnsNull(int tipo)
    {
        Assert.Null(InvoiceComprobanteHelpers.GetDebitNoteTypeForAssociated(tipo));
    }
}
