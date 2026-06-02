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

    // ADR-014 (M1): CaptureDebitNoteClassification ahora consume el record neutro
    // PenaltyClassificationInput (compartido entre el path sincrono y el diferido). Estos
    // tests de no-regresion ejercen exactamente la misma logica de captura/guardas; solo
    // cambio la FORMA de pasar los datos (record en vez de ConfirmCancellationRequest).
    private static PenaltyClassificationInput RequestWith(
        CancellationConceptKind? concept = null,
        PenaltyStatus? status = null,
        DebitNotePurpose? purpose = null,
        decimal? amount = null)
        => new PenaltyClassificationInput(
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

        svc.CaptureDebitNoteClassification(bc, req, "user-backoffice", "Back Office",
            userCanClassifyAgencyPenalty: true, debitNoteFeatureEnabled: true);

        Assert.Equal(CancellationConceptKind.AgencyManagementFee, bc.ConceptKind);
        Assert.Equal(PenaltyStatus.Confirmed, bc.PenaltyStatus);
        Assert.Equal(DebitNotePurpose.PenaltyOrCancellationCharge, bc.DebitNotePurpose);
        Assert.Equal(30_000m, bc.PenaltyAmountAtEvent);

        // Auditoria: clasificador (concepto cambio respecto del default pass-through).
        Assert.Equal("user-backoffice", bc.ConceptClassifiedByUserId);
        Assert.Equal("Back Office", bc.ConceptClassifiedByUserName);
        Assert.NotNull(bc.ConceptClassifiedAt);
        // Auditoria: confirmador (estado Confirmed). M1: persiste tambien el nombre.
        Assert.Equal("user-backoffice", bc.PenaltyConfirmedByUserId);
        Assert.Equal("Back Office", bc.PenaltyConfirmedByUserName);
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

        svc.CaptureDebitNoteClassification(bc, req, "u", "n",
            userCanClassifyAgencyPenalty: true, debitNoteFeatureEnabled: true);

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

        svc.CaptureDebitNoteClassification(bc, req, "vendedor", "Vendedor",
            userCanClassifyAgencyPenalty: false, debitNoteFeatureEnabled: true);

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
    public void Capture_NoClassification_OperatorIsAgency_NoPermission_DegradesToPassThrough_NoThrow()
    {
        // B2-back (review 2026-06-01): el operador esta marcado Agency, asi que el DEFAULT
        // sugerido es agency-owned. Pero el usuario NO informo el concepto a mano y NO tiene
        // el permiso. Antes esto lanzaba INV-ADR013-PERM y abortaba un confirm que hoy
        // funciona; ahora degrada conservador a pass-through (NO ND) SIN lanzar excepcion.
        var svc = BuildService();
        var bc = NewDraftBc(supplierOwnership: PenaltyOwnership.Agency);
        var req = RequestWith(); // no informa concepto -> el default sale del operador (Agency)

        // No lanza.
        svc.CaptureDebitNoteClassification(bc, req, "vendedor", "Vendedor",
            userCanClassifyAgencyPenalty: false, debitNoteFeatureEnabled: true);

        // Degradacion conservadora: pass-through, NO ND.
        Assert.Equal(CancellationConceptKind.OperatorPenaltyPassThrough, bc.ConceptKind);
        // No cambio respecto del default de la entidad -> no se setea clasificador.
        Assert.Null(bc.ConceptClassifiedByUserId);
    }

    [Fact]
    public void Capture_NoClassification_OperatorIsAgency_WithPermission_AppliesAgencyFee()
    {
        // Contraparte del test de arriba: el MISMO escenario (default por operador Agency,
        // sin concepto explicito) pero con el permiso -> aplica el default agency-owned.
        var svc = BuildService();
        var bc = NewDraftBc(supplierOwnership: PenaltyOwnership.Agency);
        var req = RequestWith();

        svc.CaptureDebitNoteClassification(bc, req, "backoffice", "Back Office",
            userCanClassifyAgencyPenalty: true, debitNoteFeatureEnabled: true);

        Assert.Equal(CancellationConceptKind.AgencyCancellationFee, bc.ConceptKind);
        Assert.Equal("backoffice", bc.ConceptClassifiedByUserId);
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
            svc.CaptureDebitNoteClassification(bc, req, "vendedor", "Vendedor",
                userCanClassifyAgencyPenalty: false, debitNoteFeatureEnabled: true));
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

        svc.CaptureDebitNoteClassification(bc, req, "vendedor", "Vendedor",
            userCanClassifyAgencyPenalty: false, debitNoteFeatureEnabled: true);
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
            svc.CaptureDebitNoteClassification(bc, req, "admin", "Admin",
                userCanClassifyAgencyPenalty: true, debitNoteFeatureEnabled: true));
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
            svc.CaptureDebitNoteClassification(bc, req, "admin", "Admin",
                userCanClassifyAgencyPenalty: true, debitNoteFeatureEnabled: true));
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
        svc.CaptureDebitNoteClassification(bc, req, "admin", "Admin",
            userCanClassifyAgencyPenalty: true, debitNoteFeatureEnabled: true);
        Assert.Equal(CancellationConceptKind.AgencyManagementFee, bc.ConceptKind);
    }

    // =====================================================================
    // (B1) Flag OFF: la captura NO toca NADA ni lanza, aunque el request traiga
    //      los 4 campos de clasificacion. Garantiza byte-identidad con d29ac8a.
    // =====================================================================

    [Fact]
    public void Capture_FeatureFlagOff_DoesNotMutateAnything_NoThrow()
    {
        var svc = BuildService();
        var bc = NewDraftBc();
        // Request "agresivo": pide clasificar como ingreso propio + confirmado + monto,
        // SIN permiso (que con el flag ON dispararia INV-ADR013-PERM). Con el flag OFF
        // todo esto debe ignorarse por completo.
        var req = RequestWith(
            concept: CancellationConceptKind.AgencyManagementFee,
            status: PenaltyStatus.Confirmed,
            purpose: DebitNotePurpose.PenaltyOrCancellationCharge,
            amount: 30_000m);

        // No lanza (ni INV-ADR013-PERM ni INV-ADR013-002).
        svc.CaptureDebitNoteClassification(bc, req, "vendedor", "Vendedor",
            userCanClassifyAgencyPenalty: false, debitNoteFeatureEnabled: false);

        // NADA muto: el BC queda con los defaults de la entidad (= comportamiento d29ac8a).
        Assert.Equal(CancellationConceptKind.OperatorPenaltyPassThrough, bc.ConceptKind);
        Assert.Equal(PenaltyStatus.Estimated, bc.PenaltyStatus);
        Assert.Null(bc.DebitNotePurpose);
        Assert.Null(bc.PenaltyAmountAtEvent);
        Assert.Null(bc.ConceptClassifiedByUserId);
        Assert.Null(bc.ConceptClassifiedByUserName);
        Assert.Null(bc.ConceptClassifiedAt);
        Assert.Null(bc.PenaltyConfirmedByUserId);
        Assert.Null(bc.PenaltyConfirmedByUserName);
        Assert.Null(bc.PenaltyConfirmedAt);
    }

    [Fact]
    public void Capture_FeatureFlagOff_SupplierAgency_StaysPassThrough()
    {
        // Mismo escenario que activaba la disyuncion anti-doble-cobro en OperatorRefundService:
        // un supplier PenaltyOwnership.Agency. Con el flag OFF, el concepto NO debe quedar
        // agency-owned -> la disyuncion nunca se activa (regresion clave de B1).
        var svc = BuildService();
        var bc = NewDraftBc(supplierOwnership: PenaltyOwnership.Agency);
        var req = RequestWith();

        svc.CaptureDebitNoteClassification(bc, req, "vendedor", "Vendedor",
            userCanClassifyAgencyPenalty: false, debitNoteFeatureEnabled: false);

        Assert.Equal(CancellationConceptKind.OperatorPenaltyPassThrough, bc.ConceptKind);
        Assert.False(BookingCancellationService.ConceptIsAgencyOwnedDebitNote(bc.ConceptKind));
    }

    // =====================================================================
    // (e) Sellado del clasificador en modo forzado (path DIFERIDO, Dia N).
    //     sealClassifierAuditWhenMissing=true. Cubre el corazon del fix de
    //     2026-06-02: cerrar el gating B3 sin pisar un clasificador previo.
    // =====================================================================

    [Fact]
    public void Capture_ForcedSeal_SameConcept_NoPriorClassifier_SealsCurrentUser()
    {
        // Caso real del flujo diferido: el BC trae ConceptKind=AgencyManagementFee (mismo
        // concepto que confirma el Dia N) pero SIN clasificador registrado. El modo forzado
        // debe sellar al usuario actual como clasificador, asi el gating B3 puede emitir la ND.
        var svc = BuildService();
        var bc = NewDraftBc();
        bc.ConceptKind = CancellationConceptKind.AgencyManagementFee; // mismo concepto que el confirm
        Assert.Null(bc.ConceptClassifiedByUserId); // precondicion: sin clasificador previo

        var req = RequestWith(
            concept: CancellationConceptKind.AgencyManagementFee, // NO cambia el concepto
            status: PenaltyStatus.Confirmed,
            amount: 12_000m);

        svc.CaptureDebitNoteClassification(bc, req, "user-confirmador", "Confirmador",
            userCanClassifyAgencyPenalty: true, debitNoteFeatureEnabled: true,
            sealClassifierAuditWhenMissing: true);

        // Aunque el concepto no cambio, el modo forzado sello al usuario actual como
        // clasificador (no habia uno previo) -> el gating B3 (ConceptClassifiedByUserId != null) pasa.
        Assert.Equal("user-confirmador", bc.ConceptClassifiedByUserId);
        Assert.Equal("Confirmador", bc.ConceptClassifiedByUserName);
        Assert.NotNull(bc.ConceptClassifiedAt);
    }

    [Fact]
    public void Capture_ForcedSeal_SameConcept_WithPriorClassifier_DoesNotClobber()
    {
        // Anti-clobber (fix 2026-06-02): el usuario A clasifico el concepto en el Dia 0.
        // El usuario B confirma el Dia N con el MISMO concepto en modo forzado. El rastro
        // del clasificador NO debe pasar de A a B: B queda registrado solo como confirmador.
        var svc = BuildService();
        var bc = NewDraftBc();
        bc.ConceptKind = CancellationConceptKind.AgencyManagementFee;
        bc.ConceptClassifiedByUserId = "user-A";
        bc.ConceptClassifiedByUserName = "Usuario A";
        var classifiedAtDia0 = DateTime.UtcNow.AddDays(-3);
        bc.ConceptClassifiedAt = classifiedAtDia0;

        var req = RequestWith(
            concept: CancellationConceptKind.AgencyManagementFee, // NO cambia el concepto
            status: PenaltyStatus.Confirmed,
            amount: 12_000m);

        svc.CaptureDebitNoteClassification(bc, req, "user-B", "Usuario B",
            userCanClassifyAgencyPenalty: true, debitNoteFeatureEnabled: true,
            sealClassifierAuditWhenMissing: true);

        // El clasificador original (A) se preserva: NO lo piso B.
        Assert.Equal("user-A", bc.ConceptClassifiedByUserId);
        Assert.Equal("Usuario A", bc.ConceptClassifiedByUserName);
        Assert.Equal(classifiedAtDia0, bc.ConceptClassifiedAt);

        // B queda registrado en su PROPIA columna (confirmador), no en la del clasificador.
        Assert.Equal("user-B", bc.PenaltyConfirmedByUserId);
        Assert.Equal("Usuario B", bc.PenaltyConfirmedByUserName);
        Assert.NotNull(bc.PenaltyConfirmedAt);
    }

    [Fact]
    public void Capture_NotForced_SameConcept_NoPriorClassifier_DoesNotSeal()
    {
        // Path SINCRONO Dia 0 (default sealClassifierAuditWhenMissing=false): si el concepto
        // no cambia, NO se sella clasificador (comportamiento intacto). Congela que el fix no
        // toco la semantica del path sincrono.
        var svc = BuildService();
        var bc = NewDraftBc();
        bc.ConceptKind = CancellationConceptKind.AgencyManagementFee; // mismo concepto que el request
        Assert.Null(bc.ConceptClassifiedByUserId);

        var req = RequestWith(concept: CancellationConceptKind.AgencyManagementFee);

        // Sin pasar el parametro -> default false (como ConfirmAsync del Dia 0).
        svc.CaptureDebitNoteClassification(bc, req, "user-dia0", "Dia 0",
            userCanClassifyAgencyPenalty: true, debitNoteFeatureEnabled: true);

        // No cambio el concepto y no estamos en modo forzado -> no se sella clasificador.
        Assert.Null(bc.ConceptClassifiedByUserId);
        Assert.Null(bc.ConceptClassifiedByUserName);
        Assert.Null(bc.ConceptClassifiedAt);
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
