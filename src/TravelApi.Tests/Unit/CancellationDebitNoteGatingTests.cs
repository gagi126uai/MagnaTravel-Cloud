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
        // B3 (review 2026-06-01): el gating exige el rastro de auditoria (quien clasifico
        // el concepto y quien confirmo la penalidad). El caso feliz los trae poblados.
        ConceptClassifiedByUserId = "user-backoffice",
        PenaltyConfirmedByUserId = "user-backoffice",
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
    public void PassThrough_NowEmits_SignedFiscalRule()
    {
        // REGLA FISCAL CERRADA (firmada): la penalidad pass-through del operador SI emite ND al
        // cliente (la agencia replica la multa del operador como concepto NO gravado). Antes este
        // concepto se ruteaba a manual; ahora, con el resto del gating cumplido, EMITE.
        var bc = HappyBc();
        bc.ConceptKind = CancellationConceptKind.OperatorPenaltyPassThrough;
        bc.Supplier!.PenaltyOwnership = PenaltyOwnership.Operator; // operador retiene: es el caso tipico de pass-through
        Assert.Null(BookingCancellationService.EvaluateDebitNoteGating(bc, HappyInvoice()));
    }

    [Fact]
    public void OperatorRetainsPenalty_StillEmits_WhenPassThrough()
    {
        // Que el operador "retenga" la penalidad (PenaltyOwnership=Operator) ya NO bloquea la ND:
        // la regla cerrada dice que esa multa se le cobra al cliente con una ND pass-through.
        var bc = HappyBc();
        bc.ConceptKind = CancellationConceptKind.OperatorPenaltyPassThrough;
        bc.Supplier!.PenaltyOwnership = PenaltyOwnership.Operator;
        Assert.Null(BookingCancellationService.EvaluateDebitNoteGating(bc, HappyInvoice()));
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

    /// <summary>BC del caso feliz PERO con la multa declarada en USD (para factura USD).</summary>
    private static BookingCancellation HappyBcUsd()
    {
        var bc = HappyBc();
        bc.PenaltyCurrencyAtEvent = "USD"; // moneda declarada de la multa (ISO), coincide con la factura DOL
        return bc;
    }

    /// <summary>Factura del caso feliz PERO en USD con TC coherente.</summary>
    private static Invoice HappyInvoiceUsd(decimal rate = 1500m)
    {
        var invoice = HappyInvoice();
        invoice.MonId = "DOL";
        invoice.MonCotiz = rate;
        return invoice;
    }

    [Fact]
    public void NonArsCurrency_FlagOff_RoutesToManual()
    {
        // Moneda extranjera (declarada coherente) con la facturacion multimoneda DESHABILITADA (flag OFF, tambien
        // el default): vuelve a revision manual. No-regresion de "sin flag no se emite en divisa".
        Assert.NotNull(BookingCancellationService.EvaluateDebitNoteGating(
            HappyBcUsd(), HappyInvoiceUsd(), multiCurrencyInvoicingEnabled: false));
    }

    [Fact]
    public void NonArsCurrency_FlagOn_CoherentRate_DeclaredMatches_CanEmit()
    {
        // ADR-012/013 (2026-07-08): factura USD con TC coherente (1500), multa DECLARADA en USD + flag ON -> la
        // ND se emite en USD heredando MonId/MonCotiz del original. null = puede emitir.
        Assert.Null(BookingCancellationService.EvaluateDebitNoteGating(
            HappyBcUsd(), HappyInvoiceUsd(), multiCurrencyInvoicingEnabled: true));
    }

    [Theory]
    [InlineData(0)]   // TC 0: dato corrupto
    [InlineData(1)]   // TC 1: valuaria un dolar como un peso
    [InlineData(-5)]  // TC negativo: imposible
    public void NonArsCurrency_FlagOn_IncoherentRate_RoutesToManual(decimal incoherentRate)
    {
        // Guard 3 del gating multimoneda: aun con el flag ON y la moneda declarada coherente, un TC <= 0 o == 1
        // en una factura extranjera es incoherente -> revision manual (no emitimos un comprobante mal valuado).
        Assert.NotNull(BookingCancellationService.EvaluateDebitNoteGating(
            HappyBcUsd(), HappyInvoiceUsd(incoherentRate), multiCurrencyInvoicingEnabled: true));
    }

    [Fact]
    public void NonArsCurrency_FlagOn_UnsupportedCurrency_RoutesToManual()
    {
        // Una moneda fuera del catalogo ARCA soportado (ej. "EUR" legacy) va a revision manual aun con el flag ON.
        var invoice = HappyInvoice();
        invoice.MonId = "EUR";
        invoice.MonCotiz = 1100m;
        Assert.NotNull(BookingCancellationService.EvaluateDebitNoteGating(
            HappyBcUsd(), invoice, multiCurrencyInvoicingEnabled: true));
    }

    [Fact]
    public void ArsInvoice_FlagOn_UnaffectedByMultiCurrency_StillEmits()
    {
        // No-regresion ARS: una factura en pesos pasa el gating igual con el flag ON (la rama de moneda
        // extranjera ni se toca para ARS). El flag no altera el camino en pesos.
        Assert.Null(BookingCancellationService.EvaluateDebitNoteGating(
            HappyBc(), HappyInvoice(), multiCurrencyInvoicingEnabled: true));
    }

    // ---- B1 (security 2026-07-08): coherencia de la moneda DECLARADA vs la moneda de la factura ----

    [Fact]
    public void DeclaredArs_OnUsdInvoice_RoutesToManual_Mismatch()
    {
        // La multa se cargo "en pesos" pero la factura esta en dolares: NO emitir (la ND saldria por el numero
        // equivocado, ~1500x). El motivo es de negocio, con etiqueta amigable y sin codigos tecnicos.
        var bc = HappyBc();
        bc.PenaltyCurrencyAtEvent = "ARS"; // declarada en pesos
        var reason = BookingCancellationService.EvaluateDebitNoteGating(
            bc, HappyInvoiceUsd(), multiCurrencyInvoicingEnabled: true);
        Assert.NotNull(reason);
        Assert.Contains("dólares (US$)", reason);
        Assert.Contains("pesos", reason);
        Assert.DoesNotContain("DOL", reason);
    }

    [Fact]
    public void DeclaredUsd_OnArsInvoice_RoutesToManual_Mismatch()
    {
        // Espejo del anterior: multa declarada en USD sobre factura en pesos -> mismatch -> manual.
        var bc = HappyBc();
        bc.PenaltyCurrencyAtEvent = "USD";
        var reason = BookingCancellationService.EvaluateDebitNoteGating(
            bc, HappyInvoice(), multiCurrencyInvoicingEnabled: true);
        Assert.NotNull(reason);
        Assert.Contains("dólares (US$)", reason);
    }

    [Fact]
    public void DeclaredNull_OnUsdInvoice_RoutesToManual_NoCurrencyOnRecord()
    {
        // Confirmaciones VIEJAS (como el caso real que quedo pendiente): no quedo registrada la moneda de la
        // multa. Sobre una factura extranjera NO adivinamos -> revision manual, con motivo claro.
        var bc = HappyBc();
        bc.PenaltyCurrencyAtEvent = null;
        var reason = BookingCancellationService.EvaluateDebitNoteGating(
            bc, HappyInvoiceUsd(), multiCurrencyInvoicingEnabled: true);
        Assert.NotNull(reason);
        Assert.Contains("No quedó registrado", reason);
        Assert.Contains("dólares (US$)", reason);
    }

    [Fact]
    public void DeclaredNull_OnArsInvoice_CanEmit_NoScaleRisk()
    {
        // Sobre una factura en pesos, no registrar la moneda de la multa es seguro (la ND sale en pesos igual):
        // sigue emitiendo, byte-identico a como venia (no rompe los flujos ARS existentes).
        var bc = HappyBc();
        bc.PenaltyCurrencyAtEvent = null;
        Assert.Null(BookingCancellationService.EvaluateDebitNoteGating(
            bc, HappyInvoice(), multiCurrencyInvoicingEnabled: true));
    }

    // ---- MonedaLabel: etiquetas amigables sin codigos tecnicos (data-exposure) ----

    [Theory]
    [InlineData("PES", "pesos")]
    [InlineData("ARS", "pesos")]
    [InlineData("", "pesos")]      // MonId vacio = pesos (convencion legacy)
    [InlineData(null, "pesos")]
    [InlineData("DOL", "dólares (US$)")]
    [InlineData("USD", "dólares (US$)")]
    [InlineData("EUR", "una moneda no reconocida")]
    public void MonedaLabel_MapsToFriendlyLabel(string? code, string expected)
    {
        Assert.Equal(expected, BookingCancellationService.MonedaLabel(code));
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

    // ---- B3 (review 2026-06-01): auditoria como invariante del gating ----

    [Fact]
    public void MissingConceptClassifier_RoutesToManual()
    {
        // B3: si no hay rastro de QUIEN clasifico el concepto, no se emite (a manual),
        //     aunque todo lo demas del caso feliz se cumpla. Reproduce el camino real:
        //     un BC ya creado con ConceptKind=AgencyCancellationFee + un Confirm que lo
        //     deja igual -> la captura no setea ConceptClassifiedByUserId (queda null).
        var bc = HappyBc();
        bc.ConceptClassifiedByUserId = null;
        Assert.NotNull(BookingCancellationService.EvaluateDebitNoteGating(bc, HappyInvoice()));
    }

    [Fact]
    public void MissingPenaltyConfirmer_RoutesToManual()
    {
        // B3: si no hay rastro de QUIEN confirmo la penalidad, no se emite (a manual).
        var bc = HappyBc();
        bc.PenaltyConfirmedByUserId = null;
        Assert.NotNull(BookingCancellationService.EvaluateDebitNoteGating(bc, HappyInvoice()));
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

    /// <summary>
    /// Pass-through NO es "ingreso propio de la agencia": ese helper sigue devolviendo false para
    /// pass-through (la ND pass-through no declara ingreso gravado).
    ///
    /// <para>OJO (regla fiscal firmada): con EnableCancellationDebitNote ON, la disyuncion
    /// anti-doble-cobro del OperatorRefundService SI se activa para pass-through (usa
    /// <c>ConceptEmitsDebitNote</c>, mas amplio: la multa pass-through se cobra con ND, no se puede
    /// ademas netear del refund). Con el flag OFF, pass-through NO emite ND y la deduction
    /// CancellationPenalty se acepta como antes (byte-identidad). Este test fija solo el predicado
    /// estrecho (no agency-owned); la cobertura del flag vive en
    /// <see cref="PassThroughPenaltyDebitNoteTests"/> y en los tests de OperatorRefundService.</para>
    /// </summary>
    [Fact]
    public void PassThroughConcept_IsNotAgencyOwned()
    {
        Assert.False(BookingCancellationService.ConceptIsAgencyOwnedDebitNote(
            CancellationConceptKind.OperatorPenaltyPassThrough));
        // Pero SI emite ND (regla cerrada).
        Assert.True(BookingCancellationService.ConceptEmitsDebitNote(
            CancellationConceptKind.OperatorPenaltyPassThrough));
    }
}
