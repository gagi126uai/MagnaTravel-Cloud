using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-013 §3.4.1 (P3 gating) — tests del gating de la Nota de Debito por penalidad.
///
/// <para>Son tests UNIT puros (sin DB ni Docker): <c>EvaluateDebitNoteGating</c> es una
/// funcion estatica que recibe el BC y la factura original y decide si el caso califica
/// para emitir ND automatica (devuelve <c>null</c>) o va a revision manual (devuelve un
/// motivo). El proyecto tiene <c>InternalsVisibleTo("TravelApi.Tests")</c>.</para>
///
/// <para>El espiritu del gating es CONSERVADOR: ante cualquier duda, NO emite. Estos
/// tests pinean tanto el caso feliz como cada motivo de ruteo a manual del §3.4.1.</para>
/// </summary>
public class CancellationDebitNoteGatingTests
{
    // ---- Builders del caso feliz, para mutar una sola dimension por test ----

    /// <summary>Factura original C=11, ARS, sin tributos, total 100.000 (el caso feliz).</summary>
    private static Invoice HappyInvoice() => new Invoice
    {
        Id = 1,
        TipoComprobante = 11, // Factura C
        MonId = "PES",
        ImporteTotal = 100_000m,
        PuntoDeVenta = 1,
        NumeroComprobante = 123,
        ReservaId = 10,
    };

    /// <summary>BC con concepto propio confirmado, penalidad 30.000, operador = agencia.</summary>
    private static BookingCancellation HappyBc() => new BookingCancellation
    {
        ConceptKind = CancellationConceptKind.AgencyManagementFee,
        PenaltyStatus = PenaltyStatus.Confirmed,
        DebitNotePurpose = DebitNotePurpose.PenaltyOrCancellationCharge,
        PenaltyAmountAtEvent = 30_000m,
        Supplier = new Supplier { Name = "Operador X", PenaltyOwnership = PenaltyOwnership.Agency },
    };

    [Fact]
    public void HappyPath_AgencyFee_FacturaC_Ars_Confirmed_ReturnsNull_CanEmit()
    {
        // TODO el gating se cumple -> null = puede emitir la ND C.
        var result = BookingCancellationService.EvaluateDebitNoteGating(HappyBc(), HappyInvoice());
        Assert.Null(result);
    }

    [Fact]
    public void HappyPath_AgencyCancellationFee_AlsoEmits()
    {
        var bc = HappyBc();
        bc.ConceptKind = CancellationConceptKind.AgencyCancellationFee;
        Assert.Null(BookingCancellationService.EvaluateDebitNoteGating(bc, HappyInvoice()));
    }

    // ---- Negativos: cada uno cae a revision manual (motivo != null) ----

    [Fact]
    public void PassThrough_DoesNotEmit()
    {
        // Concepto pass-through (default) = la plata es del operador -> NO ND.
        var bc = HappyBc();
        bc.ConceptKind = CancellationConceptKind.OperatorPenaltyPassThrough;
        Assert.NotNull(BookingCancellationService.EvaluateDebitNoteGating(bc, HappyInvoice()));
    }

    [Fact]
    public void OperatorRetainsPenalty_DoesNotEmit()
    {
        // Aunque el concepto fuera propio, si el operador esta marcado como pass-through,
        // la defensa redundante lo manda a manual.
        var bc = HappyBc();
        bc.Supplier!.PenaltyOwnership = PenaltyOwnership.Operator;
        Assert.NotNull(BookingCancellationService.EvaluateDebitNoteGating(bc, HappyInvoice()));
    }

    [Fact]
    public void EstimatedPenalty_DoesNotEmit()
    {
        // R5: no se emite comprobante sobre una penalidad estimada.
        var bc = HappyBc();
        bc.PenaltyStatus = PenaltyStatus.Estimated;
        Assert.NotNull(BookingCancellationService.EvaluateDebitNoteGating(bc, HappyInvoice()));
    }

    [Theory]
    [InlineData(1)]   // Factura A
    [InlineData(6)]   // Factura B
    [InlineData(51)]  // Factura M
    public void FacturaNotC_RoutesToManual(int tipoComprobante)
    {
        // M3: solo factura C (11/12) se automatiza. A/B/M -> manual.
        var invoice = HappyInvoice();
        invoice.TipoComprobante = tipoComprobante;
        Assert.NotNull(BookingCancellationService.EvaluateDebitNoteGating(HappyBc(), invoice));
    }

    [Fact]
    public void NonArsCurrency_RoutesToManual()
    {
        // Multimoneda -> futuro, manual en el MVP.
        var invoice = HappyInvoice();
        invoice.MonId = "DOL";
        Assert.NotNull(BookingCancellationService.EvaluateDebitNoteGating(HappyBc(), invoice));
    }

    [Theory]
    [InlineData(CancellationConceptKind.RealInsurancePremium)]
    [InlineData(CancellationConceptKind.AgencyCancellationCoverage)]
    [InlineData(CancellationConceptKind.AgencyInsuranceCommission)]
    public void InsuranceConcepts_RouteToManual(CancellationConceptKind concept)
    {
        var bc = HappyBc();
        bc.ConceptKind = concept;
        Assert.NotNull(BookingCancellationService.EvaluateDebitNoteGating(bc, HappyInvoice()));
    }

    [Theory]
    [InlineData(DebitNotePurpose.CorrectCreditNote)]
    [InlineData(DebitNotePurpose.FceMiPyme)]
    public void NonPenaltyPurpose_RoutesToManual(DebitNotePurpose purpose)
    {
        var bc = HappyBc();
        bc.DebitNotePurpose = purpose;
        Assert.NotNull(BookingCancellationService.EvaluateDebitNoteGating(bc, HappyInvoice()));
    }

    [Fact]
    public void PenaltyAboveInvoiceTotal_RoutesToManual()
    {
        // M2: una penalidad mayor a la factura es casi seguro un error de carga.
        var bc = HappyBc();
        bc.PenaltyAmountAtEvent = 150_000m; // > 100.000 de la factura
        Assert.NotNull(BookingCancellationService.EvaluateDebitNoteGating(bc, HappyInvoice()));
    }

    [Fact]
    public void PenaltyEqualToInvoiceTotal_StillEmits()
    {
        // El tope es <= total: la penalidad igual al total todavia es valida.
        var bc = HappyBc();
        bc.PenaltyAmountAtEvent = 100_000m;
        Assert.Null(BookingCancellationService.EvaluateDebitNoteGating(bc, HappyInvoice()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void NonPositivePenalty_RoutesToManual(decimal penalty)
    {
        var bc = HappyBc();
        bc.PenaltyAmountAtEvent = penalty;
        Assert.NotNull(BookingCancellationService.EvaluateDebitNoteGating(bc, HappyInvoice()));
    }

    [Fact]
    public void MissingPenaltyAmount_RoutesToManual()
    {
        var bc = HappyBc();
        bc.PenaltyAmountAtEvent = null;
        Assert.NotNull(BookingCancellationService.EvaluateDebitNoteGating(bc, HappyInvoice()));
    }

    [Fact]
    public void InvoiceWithProvincialTributes_RoutesToManual()
    {
        // R6: IIBB/percepciones discriminadas -> manual.
        var invoice = HappyInvoice();
        invoice.Tributes = new List<InvoiceTribute>
        {
            new InvoiceTribute { Description = "Percepcion IIBB", Importe = 500m },
        };
        Assert.NotNull(BookingCancellationService.EvaluateDebitNoteGating(HappyBc(), invoice));
    }

    [Fact]
    public void EmptyTributesCollection_GatingAlonePasses_DocumentsFalseNegative()
    {
        // ADR-013 fix del falso negativo de Tributos: el gating PURO solo ve la coleccion
        // en memoria. Si la factura llegara SIN el Include de Tributes, la coleccion queda
        // VACIA (default del constructor de Invoice, NO null), asi que el gating por si solo
        // devolveria null (emitiria) aunque la BD tenga IIBB. Este test PINEA ese limite del
        // gating puro y documenta por que NO alcanza con el gating: la proteccion real son
        // (1) el Include .ThenInclude(i => i.Tributes) en OnArcaSucceededAsync y (2) el
        // fail-safe que consulta los tributos directamente contra la BD antes de emitir
        // (ambos cubiertos por integration tests con Postgres real).
        var invoice = HappyInvoice();
        invoice.Tributes = new List<InvoiceTribute>(); // coleccion cargada vacia (sin Include)
        Assert.Null(BookingCancellationService.EvaluateDebitNoteGating(HappyBc(), invoice));
    }

    // ---- Disyuncion anti-doble-cobro (INV-ADR013-001, §3.3) ----

    /// <summary>
    /// El concepto "ingreso propio de la agencia" es el que decide si la penalidad se
    /// materializa como ND. La misma funcion la usan el gating de la ND y
    /// OperatorRefundService (para rechazar cargar la penalidad como deduction del refund).
    /// Las dos vias son mutuamente excluyentes: este helper es el eje de esa disyuncion.
    /// </summary>
    [Theory]
    [InlineData(CancellationConceptKind.AgencyManagementFee, true)]
    [InlineData(CancellationConceptKind.AgencyCancellationFee, true)]
    [InlineData(CancellationConceptKind.OperatorPenaltyPassThrough, false)]
    [InlineData(CancellationConceptKind.RealInsurancePremium, false)]
    [InlineData(CancellationConceptKind.AgencyCancellationCoverage, false)]
    [InlineData(CancellationConceptKind.AgencyInsuranceCommission, false)]
    public void ConceptIsAgencyOwnedDebitNote_MatchesAgencyConcepts(
        CancellationConceptKind concept, bool expected)
    {
        Assert.Equal(expected, BookingCancellationService.ConceptIsAgencyOwnedDebitNote(concept));
    }
}
