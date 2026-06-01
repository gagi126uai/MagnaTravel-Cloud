using System;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-013 (2026-06-01) — tests UNIT del wiring de CAPTURA de la clasificacion de la
/// penalidad y de las 3 guardas que viven en el service:
///   (1) permiso elevado para clasificar como ingreso propio de la agencia;
///   (2) anti-reclasificacion cuando la ND ya esta en juego;
///   (3) auditoria (quien clasifico el concepto / quien confirmo la penalidad).
///
/// <para>Son tests sin DB ni Docker: <c>CaptureDebitNoteClassification</c> es un metodo
/// internal que SOLO muta el BC en memoria (no toca el DbContext). Construimos el service
/// con mocks y un DbContext InMemory que ni siquiera se usa en este metodo. El proyecto
/// tiene <c>InternalsVisibleTo("TravelApi.Tests")</c>.</para>
/// </summary>
public class CancellationDebitNoteCaptureTests
{
    // ---- Builder minimo del service (el metodo bajo test no usa _db) ----

    private static BookingCancellationService BuildService()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"adr013-capture-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var ctx = new AppDbContext(options);

        return new BookingCancellationService(
            ctx,
            new Mock<IInvoiceService>().Object,
            new Mock<IApprovalRequestService>().Object,
            new Mock<IAuditService>().Object,
            NullLogger<BookingCancellationService>.Instance,
            new Mock<IOperationalFinanceSettingsService>().Object,
            new Mock<IFiscalLiquidationCalculator>().Object,
            new Mock<IAdminUserCountService>().Object);
    }

    /// <summary>BC recien drafteado: defaults conservadores (pass-through / Estimated).</summary>
    private static BookingCancellation NewDraftBc(PenaltyOwnership supplierOwnership = PenaltyOwnership.Operator)
        => new BookingCancellation
        {
            Id = 1,
            PublicId = Guid.NewGuid(),
            Supplier = new Supplier { Name = "Operador X", PenaltyOwnership = supplierOwnership },
            // defaults de la entidad: ConceptKind=OperatorPenaltyPassThrough, PenaltyStatus=Estimated,
            // DebitNoteStatus=NotApplicable.
        };

    private static ConfirmCancellationRequest RequestWith(
        CancellationConceptKind? concept = null,
        PenaltyStatus? status = null,
        DebitNotePurpose? purpose = null,
        decimal? amount = null)
        => new ConfirmCancellationRequest(
            SnapshotData: new FiscalSnapshotData(
                CurrencyAtEvent: "ARS",
                ExchangeRateAtOriginalInvoice: 1m,
                Source: ExchangeRateSource.Manual,
                ManualJustification: "Test",
                AgencyTaxConditionAtEvent: "Monotributo",
                SupplierTaxConditionAtEvent: "Monotributo",
                CustomerTaxConditionAtEvent: "ConsumidorFinal"),
            IsAdminOverride: false,
            OverrideReason: null,
            ApprovalRequestPublicId: null,
            PenaltyConceptKind: concept,
            PenaltyStatus: status,
            DebitNotePurpose: purpose,
            ConfirmedPenaltyAmount: amount);

    // =====================================================================
    // (a) Clasificar como ingreso propio + confirmar setea todos los campos
    //     + la auditoria del clasificador y del confirmador.
    // =====================================================================

    [Fact]
    public void Capture_AgencyOwned_Confirmed_SetsFieldsAndAudit()
    {
        var svc = BuildService();
        var bc = NewDraftBc();
        var req = RequestWith(
            concept: CancellationConceptKind.AgencyManagementFee,
            status: PenaltyStatus.Confirmed,
            purpose: DebitNotePurpose.PenaltyOrCancellationCharge,
            amount: 30_000m);

        svc.CaptureDebitNoteClassification(bc, req, "user-backoffice", "Back Office", userCanClassifyAgencyPenalty: true);

        Assert.Equal(CancellationConceptKind.AgencyManagementFee, bc.ConceptKind);
        Assert.Equal(PenaltyStatus.Confirmed, bc.PenaltyStatus);
        Assert.Equal(DebitNotePurpose.PenaltyOrCancellationCharge, bc.DebitNotePurpose);
        Assert.Equal(30_000m, bc.PenaltyAmountAtEvent);

        // Auditoria: clasificador (concepto cambio respecto del default pass-through).
        Assert.Equal("user-backoffice", bc.ConceptClassifiedByUserId);
        Assert.NotNull(bc.ConceptClassifiedAt);
        // Auditoria: confirmador (estado Confirmed).
        Assert.Equal("user-backoffice", bc.PenaltyConfirmedByUserId);
        Assert.NotNull(bc.PenaltyConfirmedAt);
    }

    [Fact]
    public void Capture_AgencyOwned_WithoutPurpose_DefaultsToPenaltyCharge()
    {
        var svc = BuildService();
        var bc = NewDraftBc();
        var req = RequestWith(
            concept: CancellationConceptKind.AgencyCancellationFee,
            status: PenaltyStatus.Confirmed,
            amount: 5_000m); // sin purpose explicito

        svc.CaptureDebitNoteClassification(bc, req, "u", "n", userCanClassifyAgencyPenalty: true);

        // El service defaultea la finalidad al unico caso que el MVP automatiza.
        Assert.Equal(DebitNotePurpose.PenaltyOrCancellationCharge, bc.DebitNotePurpose);
    }

    // =====================================================================
    // (b) Sin clasificar nada -> default segun el operador (pass-through si el
    //     operador retiene). No setea auditoria de confirmacion (Estimated).
    // =====================================================================

    [Fact]
    public void Capture_NoClassification_OperatorRetains_StaysPassThrough()
    {
        var svc = BuildService();
        var bc = NewDraftBc(supplierOwnership: PenaltyOwnership.Operator);
        var req = RequestWith(); // todo null

        svc.CaptureDebitNoteClassification(bc, req, "vendedor", "Vendedor", userCanClassifyAgencyPenalty: false);

        // Default conservador: pass-through (NO ND), igual a hoy.
        Assert.Equal(CancellationConceptKind.OperatorPenaltyPassThrough, bc.ConceptKind);
        Assert.Equal(PenaltyStatus.Estimated, bc.PenaltyStatus);
        Assert.Null(bc.DebitNotePurpose);
        // No hubo cambio de concepto (ya era pass-through) -> no se setea clasificador.
        Assert.Null(bc.ConceptClassifiedByUserId);
        // No esta Confirmed -> no se setea confirmador.
        Assert.Null(bc.PenaltyConfirmedByUserId);
    }

    [Fact]
    public void Capture_NoClassification_OperatorIsAgency_SuggestsAgencyFee_ButNeedsPermission()
    {
        var svc = BuildService();
        var bc = NewDraftBc(supplierOwnership: PenaltyOwnership.Agency);
        var req = RequestWith(); // no informa concepto -> el default sale del operador (Agency)

        // El default sugerido es AgencyCancellationFee (ingreso propio) -> exige permiso.
        // Sin permiso, debe rechazar aunque el usuario no haya informado el concepto a mano.
        Assert.Throws<BusinessInvariantViolationException>(() =>
            svc.CaptureDebitNoteClassification(bc, req, "vendedor", "Vendedor", userCanClassifyAgencyPenalty: false));
    }

    // =====================================================================
    // (d) Sin el permiso elevado, clasificar como ingreso propio se rechaza.
    // =====================================================================

    [Theory]
    [InlineData(CancellationConceptKind.AgencyManagementFee)]
    [InlineData(CancellationConceptKind.AgencyCancellationFee)]
    public void Capture_AgencyOwned_WithoutPermission_Throws(CancellationConceptKind concept)
    {
        var svc = BuildService();
        var bc = NewDraftBc();
        var req = RequestWith(concept: concept, status: PenaltyStatus.Confirmed, amount: 1_000m);

        var ex = Assert.Throws<BusinessInvariantViolationException>(() =>
            svc.CaptureDebitNoteClassification(bc, req, "vendedor", "Vendedor", userCanClassifyAgencyPenalty: false));
        Assert.Equal("INV-ADR013-PERM", ex.InvariantCode);

        // Y NO debe haber tocado el concepto (rechazo antes de mutar).
        Assert.Equal(CancellationConceptKind.OperatorPenaltyPassThrough, bc.ConceptKind);
    }

    [Fact]
    public void Capture_PassThrough_WithoutPermission_Allowed()
    {
        // Clasificar como pass-through NO emite ND -> NO requiere el permiso elevado.
        var svc = BuildService();
        var bc = NewDraftBc();
        var req = RequestWith(concept: CancellationConceptKind.OperatorPenaltyPassThrough);

        svc.CaptureDebitNoteClassification(bc, req, "vendedor", "Vendedor", userCanClassifyAgencyPenalty: false);
        Assert.Equal(CancellationConceptKind.OperatorPenaltyPassThrough, bc.ConceptKind);
    }

    // =====================================================================
    // (c) Anti-reclasificacion: cambiar el concepto con la ND en juego se rechaza.
    // =====================================================================

    [Theory]
    [InlineData(DebitNoteStatus.Pending)]
    [InlineData(DebitNoteStatus.Issued)]
    public void Capture_Reclassify_WhenDebitNoteInPlayByStatus_Throws(DebitNoteStatus status)
    {
        var svc = BuildService();
        var bc = NewDraftBc();
        bc.ConceptKind = CancellationConceptKind.AgencyManagementFee; // ya clasificado como ND propia
        bc.DebitNoteStatus = status;

        // Intentar reclasificar a pass-through (la ventana de doble cobro).
        var req = RequestWith(concept: CancellationConceptKind.OperatorPenaltyPassThrough);

        var ex = Assert.Throws<BusinessInvariantViolationException>(() =>
            svc.CaptureDebitNoteClassification(bc, req, "admin", "Admin", userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-ADR013-002", ex.InvariantCode);
    }

    [Fact]
    public void Capture_Reclassify_WhenDebitNoteAlreadyLinked_Throws()
    {
        var svc = BuildService();
        var bc = NewDraftBc();
        bc.ConceptKind = CancellationConceptKind.AgencyManagementFee;
        bc.DebitNoteInvoiceId = 99; // ND ya vinculada

        var req = RequestWith(concept: CancellationConceptKind.AgencyCancellationFee); // cambio real

        var ex = Assert.Throws<BusinessInvariantViolationException>(() =>
            svc.CaptureDebitNoteClassification(bc, req, "admin", "Admin", userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-ADR013-002", ex.InvariantCode);
    }

    [Fact]
    public void Capture_SameConcept_WhenDebitNoteInPlay_IsNoOp_Allowed()
    {
        // Si el concepto requerido es IGUAL al actual, no hay reclasificacion real:
        // se permite (ej. un re-confirm que no cambia el concepto).
        var svc = BuildService();
        var bc = NewDraftBc();
        bc.ConceptKind = CancellationConceptKind.AgencyManagementFee;
        bc.DebitNoteStatus = DebitNoteStatus.Issued;

        var req = RequestWith(concept: CancellationConceptKind.AgencyManagementFee);

        // No lanza.
        svc.CaptureDebitNoteClassification(bc, req, "admin", "Admin", userCanClassifyAgencyPenalty: true);
        Assert.Equal(CancellationConceptKind.AgencyManagementFee, bc.ConceptKind);
    }

    // =====================================================================
    // Helpers puros: el default por operador y la guarda anti-reclasificacion.
    // =====================================================================

    [Theory]
    [InlineData(PenaltyOwnership.Operator, CancellationConceptKind.OperatorPenaltyPassThrough)]
    [InlineData(PenaltyOwnership.Agency, CancellationConceptKind.AgencyCancellationFee)]
    public void DefaultConceptFromSupplier_FollowsOwnership(PenaltyOwnership ownership, CancellationConceptKind expected)
    {
        Assert.Equal(expected, BookingCancellationService.DefaultConceptFromSupplier(ownership));
    }

    [Fact]
    public void DefaultConceptFromSupplier_NullOwnership_IsConservativePassThrough()
    {
        Assert.Equal(
            CancellationConceptKind.OperatorPenaltyPassThrough,
            BookingCancellationService.DefaultConceptFromSupplier(null));
    }
}
