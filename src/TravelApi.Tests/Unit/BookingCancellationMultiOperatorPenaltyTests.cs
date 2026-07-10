using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-044 T1 (2026-07-10) — tests UNIT de "la multa vive en la LÍNEA": una cancelación con servicios de MÁS de
/// un operador (ADR-025) puede confirmar/cerrar sin multa la penalidad de cada operador POR SEPARADO, en vez de
/// que <c>ConfirmPenaltyAsync</c>/<c>WaiveOperatorPenaltyAsync</c> queden hardcodeados al operador principal del
/// BC (<c>bc.SupplierId</c>) — el bug M2 del rediseño de multas, donde las multas de operadores SECUNDARIOS se
/// perdían silenciosamente.
///
/// <para>Cubre: (1) el candado que exige especificar el operador cuando hay más de uno en juego
/// (<c>ResolveTargetSupplierId</c>); (2) que confirmar dos operadores por separado neta el <c>RefundCap</c> del
/// operador CORRECTO en cada llamada (fix del bug M2); (3) que confirmarlos activa el candado YA EXISTENTE de
/// multi-operador de la ND (<c>CountSuppliersWithConfirmedPenaltyAsync</c>), que antes de esta tanda nunca
/// disparaba porque ninguna línea llegaba a <c>PenaltyStatus.Confirmed</c>; (4) que un operador SECUNDARIO se
/// puede cerrar sin multa sin que lo bloquee el comprobante (real o en curso) de OTRO operador; (5) paridad
/// byte-a-byte de <c>operatorPenaltySituations</c> contra el singular para el caso mono-operador (M3 del
/// desafío, el 100% de las reservas de hoy).</para>
///
/// <para>Mismo enfoque que <see cref="CancellationDeferredPenaltyTests"/>: DbContext InMemory + mocks, sin
/// Docker.</para>
/// </summary>
public class BookingCancellationMultiOperatorPenaltyTests
{
    // ============================================================
    // Builders (espejo minimal de CancellationDeferredPenaltyTests / CancellationWaivePenaltyTests)
    // ============================================================

    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"adr044-t1-multi-op-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private sealed record Harness(BookingCancellationService Service, AppDbContext Ctx);

    private static Harness BuildService()
    {
        var ctx = NewDbContext();
        var invoiceMock = new Mock<IInvoiceService>();
        var approvalMock = new Mock<IApprovalRequestService>();
        var auditMock = new Mock<IAuditService>();
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        var calculatorMock = new Mock<IFiscalLiquidationCalculator>();
        var adminCountMock = new Mock<IAdminUserCountService>();

        var settings = new OperationalFinanceSettings
        {
            EnableNewCancellationFlow = true,
            EnableCancellationDebitNote = true,
            CancellationDebitNoteGraceDays = 15,
            CancellationDebitNoteHardWarnDays = 60,
            // Umbral alto para que los montos chicos de estos tests NUNCA exijan 4-eyes (foco de estos tests:
            // el reparto por operador, no el circuito de doble firma, ya cubierto en otros archivos).
            CancellationDebitNoteFourEyesThreshold = 2_000_000m,
        };
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var service = new BookingCancellationService(
            ctx, invoiceMock.Object, approvalMock.Object, auditMock.Object,
            NullLogger<BookingCancellationService>.Instance, settingsMock.Object,
            calculatorMock.Object, adminCountMock.Object);

        return new Harness(service, ctx);
    }

    /// <summary>
    /// Semilla comun: factura C=11 con CAE, NC total ya emitida (CreditNoteInvoiceId), reserva, y el BC en
    /// AwaitingOperatorRefund apuntando a <paramref name="primarySupplier"/> como operador principal
    /// (<c>bc.SupplierId</c>). NO agrega lineas: cada test las agrega segun el escenario (mono/multi-operador).
    /// </summary>
    private static async Task<(Guid BcPublicId, BookingCancellation Bc, Reserva Reserva)> SeedPostNcAsync(
        AppDbContext ctx, Supplier primarySupplier)
    {
        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-ADR044-T1", Name = "Reserva multi-operador", PayerId = customer.Id,
            Status = EstadoReserva.PendingOperatorRefund, Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var original = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 500, CAE = "cae-orig",
            Resultado = "A", MonId = "PES", ImporteTotal = 150_000m, ImporteNeto = 150_000m,
            ImporteIva = 0m, ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None,
        };
        var creditNote = new Invoice
        {
            TipoComprobante = 13, PuntoDeVenta = 1, NumeroComprobante = 501, CAE = "cae-nc",
            Resultado = "A", ReservaId = reserva.Id,
        };
        ctx.Invoices.Add(original);
        ctx.Invoices.Add(creditNote);
        await ctx.SaveChangesAsync();
        creditNote.OriginalInvoiceId = original.Id;
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = customer.Id, SupplierId = primarySupplier.Id,
            OriginatingInvoiceId = original.Id, CreditNoteInvoiceId = creditNote.Id,
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            Reason = "Cancelacion con servicios de mas de un operador",
            DraftedByUserId = "vendedor-1", ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-10),
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        return (bc.PublicId, bc, reserva);
    }

    private static ConfirmPenaltyRequest Request(
        decimal amount, Guid? supplierPublicId,
        CancellationConceptKind concept = CancellationConceptKind.OperatorPenaltyPassThrough,
        string penaltyCurrency = "ARS")
        => new ConfirmPenaltyRequest(
            ConceptKind: concept,
            ConfirmedPenaltyAmount: amount,
            OperatorConfirmationDate: DateTime.UtcNow.AddDays(-1),
            DebitNotePurpose: null,
            PenaltyCurrency: penaltyCurrency,
            SupportingDocumentReference: "https://docs/operador.pdf", // evita 4-eyes
            SupplierPublicId: supplierPublicId);

    // ============================================================
    // (1) Candado de ambiguedad: mas de un operador, sin especificar cual
    // ============================================================

    [Fact]
    public async Task ConfirmPenalty_TwoOperatorsWithoutSelector_RequiresDisambiguation()
    {
        var h = BuildService();
        var supplierA = new Supplier { Name = "Operador A", IsActive = true, PenaltyOwnership = PenaltyOwnership.Operator };
        var supplierB = new Supplier { Name = "Operador B", IsActive = true, PenaltyOwnership = PenaltyOwnership.Operator };
        h.Ctx.Suppliers.AddRange(supplierA, supplierB);
        await h.Ctx.SaveChangesAsync();
        var (bcId, bc, _) = await SeedPostNcAsync(h.Ctx, supplierA);

        h.Ctx.BookingCancellationLines.Add(new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplierA.Id,
            ServiceTable = CancellableServiceTable.Hotel, ServiceId = 1,
            Scope = BookingCancellationLineScope.Full, Currency = "ARS", RefundCap = 100_000m,
        });
        h.Ctx.BookingCancellationLines.Add(new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplierB.Id,
            ServiceTable = CancellableServiceTable.Transfer, ServiceId = 2,
            Scope = BookingCancellationLineScope.Full, Currency = "ARS", RefundCap = 50_000m,
        });
        await h.Ctx.SaveChangesAsync();

        // Sin SupplierPublicId: el service NO adivina, pide que se especifique.
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ConfirmPenaltyAsync(
                bcId, Request(10_000m, supplierPublicId: null), "u", "U",
                requesterIsAdmin: false, ct: default, userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-ADR044-OPERATOR-REQUIRED", ex.InvariantCode);
    }

    [Fact]
    public async Task ConfirmPenalty_SupplierPublicIdNotInThisCancellation_Rejects()
    {
        var h = BuildService();
        var supplierA = new Supplier { Name = "Operador A", IsActive = true, PenaltyOwnership = PenaltyOwnership.Operator };
        var otroOperador = new Supplier { Name = "Operador ajeno", IsActive = true, PenaltyOwnership = PenaltyOwnership.Operator };
        h.Ctx.Suppliers.AddRange(supplierA, otroOperador);
        await h.Ctx.SaveChangesAsync();
        var (bcId, bc, _) = await SeedPostNcAsync(h.Ctx, supplierA);
        h.Ctx.BookingCancellationLines.Add(new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplierA.Id,
            ServiceTable = CancellableServiceTable.Hotel, ServiceId = 1,
            Scope = BookingCancellationLineScope.Full, Currency = "ARS", RefundCap = 100_000m,
        });
        await h.Ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ConfirmPenaltyAsync(
                bcId, Request(10_000m, supplierPublicId: otroOperador.PublicId), "u", "U",
                requesterIsAdmin: false, ct: default, userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-ADR044-OPERATOR-NOT-FOUND", ex.InvariantCode);
    }

    // ============================================================
    // (2)+(3) Nucleo: confirmar 2 operadores POR SEPARADO neta el RefundCap correcto de cada uno
    // y activa el candado multi-operador de la ND (fix del bug M2)
    // ============================================================

    [Fact]
    public async Task ConfirmPenalty_TwoOperators_ConfirmedIndependently_NetsCorrectOperatorRefundCap()
    {
        var h = BuildService();
        var supplierA = new Supplier { Name = "Operador A", IsActive = true, PenaltyOwnership = PenaltyOwnership.Operator };
        var supplierB = new Supplier { Name = "Operador B", IsActive = true, PenaltyOwnership = PenaltyOwnership.Operator };
        h.Ctx.Suppliers.AddRange(supplierA, supplierB);
        await h.Ctx.SaveChangesAsync();
        // supplierA es el operador PRINCIPAL del BC (bc.SupplierId); supplierB es SECUNDARIO.
        var (bcId, bc, reserva) = await SeedPostNcAsync(h.Ctx, supplierA);

        var lineA = new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplierA.Id,
            ServiceTable = CancellableServiceTable.Hotel, ServiceId = 1,
            Scope = BookingCancellationLineScope.Full, Currency = "ARS", RefundCap = 100_000m,
        };
        var lineB = new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplierB.Id,
            ServiceTable = CancellableServiceTable.Transfer, ServiceId = 2,
            Scope = BookingCancellationLineScope.Full, Currency = "ARS", RefundCap = 50_000m,
        };
        h.Ctx.BookingCancellationLines.AddRange(lineA, lineB);
        await h.Ctx.SaveChangesAsync();

        // Confirmar la multa del operador A (principal): 20.000 sobre un cap de 100.000. userId DISTINTO al de B.
        await h.Service.ConfirmPenaltyAsync(
            bcId, Request(20_000m, supplierPublicId: supplierA.PublicId), "userA", "Usuario A",
            requesterIsAdmin: false, ct: default, userCanClassifyAgencyPenalty: true);

        // Confirmar la multa del operador B (SECUNDARIO) DESPUES, en una llamada APARTE, con OTRO usuario: antes de
        // esta tanda, esto habria fallado (el candado de idempotencia miraba bc.PenaltyStatus, ya Confirmed por A) o
        // habria neteado sobre las lineas de A (bug M2, AllocateConfirmedPenaltyToLinesAsync hardcodeado a bc.SupplierId).
        await h.Service.ConfirmPenaltyAsync(
            bcId, Request(10_000m, supplierPublicId: supplierB.PublicId), "userB", "Usuario B",
            requesterIsAdmin: false, ct: default, userCanClassifyAgencyPenalty: true);

        var afterA = await h.Ctx.BookingCancellationLines.AsNoTracking().SingleAsync(l => l.Id == lineA.Id);
        var afterB = await h.Ctx.BookingCancellationLines.AsNoTracking().SingleAsync(l => l.Id == lineB.Id);

        // Cada operador neteo SU PROPIA multa contra SU PROPIO cap — no se cruzaron.
        Assert.Equal(80_000m, afterA.RefundCap);
        Assert.Equal(20_000m, afterA.PenaltyAmount);
        Assert.Equal(PenaltyStatus.Confirmed, afterA.PenaltyStatus);
        Assert.Equal(40_000m, afterB.RefundCap);
        Assert.Equal(10_000m, afterB.PenaltyAmount);
        Assert.Equal(PenaltyStatus.Confirmed, afterB.PenaltyStatus);

        // fix B1: el snapshot fiscal del BC padre lo escribio SOLO el operador principal (A). La confirmacion de B
        // (secundario) NO lo piso — con userIds distintos, una regresion en PenaltyConfirmedByUserName seria visible.
        var reloadedBc = await h.Ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Equal("Usuario A", reloadedBc.PenaltyConfirmedByUserName);
        Assert.Equal("userA", reloadedBc.PenaltyConfirmedByUserId);
        Assert.Equal(20_000m, reloadedBc.PenaltyAmountAtEvent); // el monto de A, NO 10.000 (B)

        // El candado multi-operador de la ND (YA EXISTENTE, "ARREGLO 2") ahora SI puede activarse: hay DOS
        // operadores con PenaltyStatus.Confirmed en sus lineas. Antes de esta tanda esto era IMPOSIBLE porque
        // ninguna linea llegaba a Confirmed (CountSuppliersWithConfirmedPenaltyAsync siempre daba 0).
        var situations = await h.Service.GetOperatorPenaltySituationsAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, ct: default);
        Assert.Equal(2, situations.Count);
        Assert.All(situations, s => Assert.Equal(
            OperatorPenaltySituationState.MultiOperatorNeedsManualReview.ToString(), s.State));
    }

    // ============================================================
    // (4) Cerrar sin multa a un operador SECUNDARIO no lo bloquea el comprobante de OTRO operador
    // ============================================================

    [Fact]
    public async Task WaiveOperatorPenalty_SecondaryOperator_NotBlockedByPrimaryDebitNoteInPlay()
    {
        var h = BuildService();
        var supplierA = new Supplier { Name = "Operador A", IsActive = true, PenaltyOwnership = PenaltyOwnership.Operator };
        var supplierB = new Supplier { Name = "Operador B", IsActive = true, PenaltyOwnership = PenaltyOwnership.Operator };
        h.Ctx.Suppliers.AddRange(supplierA, supplierB);
        await h.Ctx.SaveChangesAsync();
        var (bcId, bc, _) = await SeedPostNcAsync(h.Ctx, supplierA);

        var lineA = new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplierA.Id,
            ServiceTable = CancellableServiceTable.Hotel, ServiceId = 1,
            Scope = BookingCancellationLineScope.Full, Currency = "ARS", RefundCap = 100_000m,
        };
        var lineB = new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplierB.Id,
            ServiceTable = CancellableServiceTable.Transfer, ServiceId = 2,
            Scope = BookingCancellationLineScope.Full, Currency = "ARS", RefundCap = 50_000m,
        };
        h.Ctx.BookingCancellationLines.AddRange(lineA, lineB);
        await h.Ctx.SaveChangesAsync();

        // Simula que el operador PRINCIPAL (A) ya tiene una Nota de Debito EN CURSO (comprobante ajeno para B).
        bc.DebitNoteStatus = DebitNoteStatus.Pending;
        await h.Ctx.SaveChangesAsync();

        // Cerrar sin multa al operador SECUNDARIO (B): su propia pata nunca se confirmo (Estimated), asi que NO
        // hace falta ser Admin, y el documento de A (ajeno a B) NO debe bloquearlo.
        await h.Service.WaiveOperatorPenaltyAsync(
            bcId, "El operador B no cobro multa.", "u", "U", default,
            userCanClassifyAgencyPenalty: true, requesterIsAdmin: false, supplierPublicId: supplierB.PublicId);

        var afterA = await h.Ctx.BookingCancellationLines.AsNoTracking().SingleAsync(l => l.Id == lineA.Id);
        var afterB = await h.Ctx.BookingCancellationLines.AsNoTracking().SingleAsync(l => l.Id == lineB.Id);
        Assert.Equal(PenaltyStatus.Estimated, afterA.PenaltyStatus); // A nunca se toco
        Assert.Equal(PenaltyStatus.Waived, afterB.PenaltyStatus);    // B quedo cerrado sin multa

        // El snapshot BC-padre (que describe al PRINCIPAL) NO se toco por el cierre de un SECUNDARIO.
        var reloadedBc = await h.Ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Equal(DebitNoteStatus.Pending, reloadedBc.DebitNoteStatus);
        Assert.Equal(PenaltyStatus.Estimated, reloadedBc.PenaltyStatus);
    }

    // ============================================================
    // (B1) El test que atrapa el bloqueante: confirmar un SECUNDARIO con la ND del PRINCIPAL ya emitida con CAE
    // NO debe pisar NINGUN campo del snapshot fiscal del BC padre.
    // ============================================================

    [Fact]
    public async Task ConfirmPenalty_SecondaryOperator_DoesNotTouchParentFiscalSnapshot_EvenWithPrimaryDebitNoteIssued()
    {
        var h = BuildService();
        var supplierA = new Supplier { Name = "Operador A", IsActive = true, PenaltyOwnership = PenaltyOwnership.Operator };
        var supplierB = new Supplier { Name = "Operador B", IsActive = true, PenaltyOwnership = PenaltyOwnership.Operator };
        h.Ctx.Suppliers.AddRange(supplierA, supplierB);
        await h.Ctx.SaveChangesAsync();
        var (bcId, bc, _) = await SeedPostNcAsync(h.Ctx, supplierA);

        h.Ctx.BookingCancellationLines.Add(new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplierA.Id,
            ServiceTable = CancellableServiceTable.Hotel, ServiceId = 1,
            Scope = BookingCancellationLineScope.Full, Currency = "ARS", RefundCap = 100_000m,
        });
        h.Ctx.BookingCancellationLines.Add(new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplierB.Id,
            ServiceTable = CancellableServiceTable.Transfer, ServiceId = 2,
            Scope = BookingCancellationLineScope.Full, Currency = "USD", RefundCap = 50_000m,
        });
        await h.Ctx.SaveChangesAsync();

        // Estado REAL post-confirmacion del PRINCIPAL A, con su ND YA EMITIDA con CAE (Issued + DebitNoteInvoiceId).
        // Sembramos una Invoice de ND vinculada para que sea lo mas fiel posible a produccion.
        var nd = new Invoice
        {
            TipoComprobante = 12, PuntoDeVenta = 1, NumeroComprobante = 900, CAE = "cae-nd-A",
            Resultado = "A", ReservaId = bc.ReservaId, OriginalInvoiceId = bc.OriginatingInvoiceId,
        };
        h.Ctx.Invoices.Add(nd);
        await h.Ctx.SaveChangesAsync();

        bc.PenaltyStatus = PenaltyStatus.Confirmed;
        bc.PenaltyAmountAtEvent = 20_000m;
        bc.PenaltyCurrencyAtEvent = "ARS";
        bc.ConceptKind = CancellationConceptKind.OperatorPenaltyPassThrough;
        bc.PenaltyConfirmedByUserId = "userA";
        bc.PenaltyConfirmedByUserName = "Usuario A";
        bc.PenaltyConfirmedAt = new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc);
        bc.OperatorPenaltyConfirmedDate = new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc);
        bc.SupportingDocumentReference = "https://docs/operadorA.pdf";
        bc.DebitNoteInvoiceId = nd.Id;
        bc.DebitNoteStatus = DebitNoteStatus.Issued;
        await h.Ctx.SaveChangesAsync();

        // Confirmar la multa del SECUNDARIO B, en dolares (su linea es USD), con otro monto y usuario.
        await h.Service.ConfirmPenaltyAsync(
            bcId, Request(7_000m, supplierPublicId: supplierB.PublicId, penaltyCurrency: "USD"), "userB", "Usuario B",
            requesterIsAdmin: false, ct: default, userCanClassifyAgencyPenalty: true);

        // NINGUN campo del snapshot fiscal del padre cambio: sigue describiendo a A y su ND emitida.
        var after = await h.Ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Equal(PenaltyStatus.Confirmed, after.PenaltyStatus);
        Assert.Equal(20_000m, after.PenaltyAmountAtEvent);
        Assert.Equal("ARS", after.PenaltyCurrencyAtEvent);
        Assert.Equal("Usuario A", after.PenaltyConfirmedByUserName);
        Assert.Equal("userA", after.PenaltyConfirmedByUserId);
        Assert.Equal(new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc), after.OperatorPenaltyConfirmedDate);
        Assert.Equal("https://docs/operadorA.pdf", after.SupportingDocumentReference);
        Assert.Equal(nd.Id, after.DebitNoteInvoiceId);
        Assert.Equal(DebitNoteStatus.Issued, after.DebitNoteStatus);

        // Pero la multa de B SI quedo registrada en SUS lineas (su pata se confirmo).
        var afterB = await h.Ctx.BookingCancellationLines.AsNoTracking()
            .SingleAsync(l => l.SupplierId == supplierB.Id);
        Assert.Equal(PenaltyStatus.Confirmed, afterB.PenaltyStatus);
        Assert.Equal(7_000m, afterB.PenaltyAmount);
        Assert.Equal("USD", afterB.PenaltyCurrency);
    }

    // ============================================================
    // (B1 no-bloqueante) Un SECUNDARIO con concepto DISTINTO al del padre NO rebota por el candado
    // anti-reclasificacion del padre (EnsureConceptNotLockedByDebitNote), que solo aplica al principal.
    // ============================================================

    [Fact]
    public async Task ConfirmPenalty_SecondaryOperator_DifferentConcept_DoesNotHitParentConceptLock()
    {
        var h = BuildService();
        var supplierA = new Supplier { Name = "Operador A", IsActive = true, PenaltyOwnership = PenaltyOwnership.Operator };
        var supplierB = new Supplier { Name = "Operador B", IsActive = true, PenaltyOwnership = PenaltyOwnership.Agency };
        h.Ctx.Suppliers.AddRange(supplierA, supplierB);
        await h.Ctx.SaveChangesAsync();
        var (bcId, bc, _) = await SeedPostNcAsync(h.Ctx, supplierA);

        h.Ctx.BookingCancellationLines.Add(new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplierA.Id,
            ServiceTable = CancellableServiceTable.Hotel, ServiceId = 1,
            Scope = BookingCancellationLineScope.Full, Currency = "ARS", RefundCap = 100_000m,
        });
        h.Ctx.BookingCancellationLines.Add(new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplierB.Id,
            ServiceTable = CancellableServiceTable.Transfer, ServiceId = 2,
            Scope = BookingCancellationLineScope.Full, Currency = "ARS", RefundCap = 50_000m,
        });
        await h.Ctx.SaveChangesAsync();

        // El padre (A) tiene una ND en juego con concepto pass-through — condicion que dispararia el candado
        // anti-reclasificacion si el secundario B (concepto agency-owned distinto) lo mirara.
        bc.ConceptKind = CancellationConceptKind.OperatorPenaltyPassThrough;
        bc.DebitNoteStatus = DebitNoteStatus.Pending;
        await h.Ctx.SaveChangesAsync();

        // B confirma con concepto agency-owned (DISTINTO). NO debe rebotar por INV-ADR013-002 (candado del padre).
        await h.Service.ConfirmPenaltyAsync(
            bcId,
            Request(5_000m, supplierPublicId: supplierB.PublicId, concept: CancellationConceptKind.AgencyCancellationFee),
            "userB", "Usuario B", requesterIsAdmin: false, ct: default, userCanClassifyAgencyPenalty: true);

        var afterB = await h.Ctx.BookingCancellationLines.AsNoTracking().SingleAsync(l => l.SupplierId == supplierB.Id);
        Assert.Equal(PenaltyStatus.Confirmed, afterB.PenaltyStatus);
        // Concepto agency-owned: el operador reembolsa integro, NO se netea el RefundCap (queda intacto).
        Assert.Equal(50_000m, afterB.RefundCap);
        Assert.Null(afterB.PenaltyAmount);
    }

    // ============================================================
    // (B2) revert-waive de un SECUNDARIO: waive -> revert -> re-confirm posible (no queda irreversible).
    // ============================================================

    [Fact]
    public async Task RevertWaive_SecondaryOperator_ReopensItsOwnPenalty_WithoutTouchingParent()
    {
        var h = BuildService();
        var supplierA = new Supplier { Name = "Operador A", IsActive = true, PenaltyOwnership = PenaltyOwnership.Operator };
        var supplierB = new Supplier { Name = "Operador B", IsActive = true, PenaltyOwnership = PenaltyOwnership.Operator };
        h.Ctx.Suppliers.AddRange(supplierA, supplierB);
        await h.Ctx.SaveChangesAsync();
        var (bcId, bc, reserva) = await SeedPostNcAsync(h.Ctx, supplierA);

        h.Ctx.BookingCancellationLines.Add(new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplierA.Id,
            ServiceTable = CancellableServiceTable.Hotel, ServiceId = 1,
            Scope = BookingCancellationLineScope.Full, Currency = "ARS", RefundCap = 100_000m,
        });
        h.Ctx.BookingCancellationLines.Add(new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplierB.Id,
            ServiceTable = CancellableServiceTable.Transfer, ServiceId = 2,
            Scope = BookingCancellationLineScope.Full, Currency = "ARS", RefundCap = 50_000m,
        });
        await h.Ctx.SaveChangesAsync();

        // Cerrar sin multa al SECUNDARIO B (desde Estimated: no requiere Admin).
        await h.Service.WaiveOperatorPenaltyAsync(
            bcId, "El operador B no cobro multa.", "admin", "Admin", default,
            userCanClassifyAgencyPenalty: true, requesterIsAdmin: true, supplierPublicId: supplierB.PublicId);

        var waivedB = await h.Ctx.BookingCancellationLines.AsNoTracking().SingleAsync(l => l.SupplierId == supplierB.Id);
        Assert.Equal(PenaltyStatus.Waived, waivedB.PenaltyStatus);

        // Reabrir el cierre sin multa del SECUNDARIO B (antes de esta tanda esto era IMPOSIBLE: el revert miraba
        // bc.PenaltyStatus del padre, que nunca fue Waived para un secundario -> INV-WAIVE-REVERT-001 eterno).
        await h.Service.RevertWaivedOperatorPenaltyAsync(
            bcId, "El operador B si cobra la multa.", "admin", "Admin", requesterIsAdmin: true, default,
            supplierPublicId: supplierB.PublicId);

        var revertedB = await h.Ctx.BookingCancellationLines.AsNoTracking().SingleAsync(l => l.SupplierId == supplierB.Id);
        Assert.Equal(PenaltyStatus.Estimated, revertedB.PenaltyStatus); // volvio a pendiente

        // El padre (A) nunca se toco en todo el ciclo.
        var reloadedBc = await h.Ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Equal(PenaltyStatus.Estimated, reloadedBc.PenaltyStatus);

        // Y B se puede RE-CONFIRMAR por el camino normal (el ciclo waive->revert->confirm es coherente).
        await h.Service.ConfirmPenaltyAsync(
            bcId, Request(9_000m, supplierPublicId: supplierB.PublicId), "admin", "Admin",
            requesterIsAdmin: true, ct: default, userCanClassifyAgencyPenalty: true);
        var reconfirmedB = await h.Ctx.BookingCancellationLines.AsNoTracking().SingleAsync(l => l.SupplierId == supplierB.Id);
        Assert.Equal(PenaltyStatus.Confirmed, reconfirmedB.PenaltyStatus);
        Assert.Equal(9_000m, reconfirmedB.PenaltyAmount);
        Assert.Equal(41_000m, reconfirmedB.RefundCap); // 50.000 - 9.000
    }

    // ============================================================
    // (non-bloqueante #1) ResolveTargetSupplierId: BC legacy SIN lineas + supplierPublicId explicito
    // que coincide con el operador del BC padre -> resuelve, no rebota OPERATOR-NOT-FOUND.
    // ============================================================

    [Fact]
    public async Task ConfirmPenalty_LegacyBcNoLines_ExplicitSupplierMatchingParent_Resolves()
    {
        var h = BuildService();
        var supplier = new Supplier { Name = "Operador Legacy", IsActive = true, PenaltyOwnership = PenaltyOwnership.Agency };
        h.Ctx.Suppliers.Add(supplier);
        await h.Ctx.SaveChangesAsync();
        // SIN lineas (BC muy anterior a ADR-025, sin backfill).
        var (bcId, _, _) = await SeedPostNcAsync(h.Ctx, supplier);

        // Con el guid del operador del BC padre: NO debe rebotar OPERATOR-NOT-FOUND (fallback legacy).
        var dto = await h.Service.ConfirmPenaltyAsync(
            bcId, Request(3_000m, supplierPublicId: supplier.PublicId), "u", "U",
            requesterIsAdmin: false, ct: default, userCanClassifyAgencyPenalty: true);
        Assert.NotNull(dto);
    }

    // ============================================================
    // (5) M3 — paridad byte-a-byte con el singular para el caso mono-operador (legado)
    // ============================================================

    [Fact]
    public async Task GetOperatorPenaltySituationsAsync_MonoOperatorLegacyLine_MatchesSingularExactly()
    {
        var h = BuildService();
        var supplier = new Supplier { Name = "Operador Unico", IsActive = true, PenaltyOwnership = PenaltyOwnership.Operator };
        h.Ctx.Suppliers.Add(supplier);
        await h.Ctx.SaveChangesAsync();
        var (_, bc, reserva) = await SeedPostNcAsync(h.Ctx, supplier);

        // Linea SINTETICA de backfill legacy (ADR-025 DT.1.3): ServiceId=0, Generic.
        h.Ctx.BookingCancellationLines.Add(new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplier.Id,
            ServiceTable = CancellableServiceTable.Generic, ServiceId = 0,
            Scope = BookingCancellationLineScope.Full, Currency = "ARS", RefundCap = 100_000m,
        });
        await h.Ctx.SaveChangesAsync();

        var singular = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, ct: default);
        var list = await h.Service.GetOperatorPenaltySituationsAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, ct: default);

        Assert.Single(list);
        var fromList = list[0];
        // Paridad byte-a-byte de TODOS los campos que ya existian antes de esta tanda.
        Assert.Equal(singular.State, fromList.State);
        Assert.Equal(singular.Amount, fromList.Amount);
        Assert.Equal(singular.Currency, fromList.Currency);
        Assert.Equal(singular.Since, fromList.Since);
        Assert.Equal(singular.CanConfirm, fromList.CanConfirm);
        Assert.Equal(singular.CanRetryDebitNote, fromList.CanRetryDebitNote);
        Assert.Equal(singular.CanCorrectAmountCurrency, fromList.CanCorrectAmountCurrency);
        Assert.Equal(singular.CanWaive, fromList.CanWaive);
        Assert.Equal(singular.WaivedAt, fromList.WaivedAt);
        Assert.Equal(singular.WaivedByName, fromList.WaivedByName);
        // Campos NUEVOS de esta tanda (aditivos, el singular no los completa): la lista SI identifica al operador.
        Assert.Equal(supplier.PublicId, fromList.SupplierPublicId);
        Assert.Equal("Operador Unico", fromList.SupplierName);
    }

    [Fact]
    public async Task GetOperatorPenaltySituationsAsync_NoLiveCancellation_ReturnsEmptyList()
    {
        var h = BuildService();
        var supplier = new Supplier { Name = "Operador", IsActive = true, PenaltyOwnership = PenaltyOwnership.Operator };
        var customer = new Customer { FullName = "Cliente", IsActive = true };
        h.Ctx.Suppliers.Add(supplier);
        h.Ctx.Customers.Add(customer);
        await h.Ctx.SaveChangesAsync();
        var reserva = new Reserva
        {
            NumeroReserva = "R-SIN-BC", Name = "Reserva sin cancelacion", PayerId = customer.Id,
            Status = EstadoReserva.Confirmed, Balance = 0m,
        };
        h.Ctx.Reservas.Add(reserva);
        await h.Ctx.SaveChangesAsync();

        var list = await h.Service.GetOperatorPenaltySituationsAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, ct: default);

        Assert.Empty(list);
    }
}
