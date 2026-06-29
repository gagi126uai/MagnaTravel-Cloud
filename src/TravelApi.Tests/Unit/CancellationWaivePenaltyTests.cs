using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Constants;
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
/// Fase A (2026-06-28) — tests UNIT del cierre SIN multa de la pata del operador
/// (<c>WaiveOperatorPenaltyAsync</c>). Cubre el caso mas comun del negocio: el operador no cobro ninguna
/// multa (devuelve todo) y la cancelacion debe poder cerrarse sin inventar una penalidad ni emitir Nota de
/// Debito.
///
/// <para>Mismo enfoque que <see cref="CancellationDeferredPenaltyTests"/>: DbContext InMemory + mocks, sin
/// Docker. Verificamos: el cierre limpia <c>HasPendingOperatorPenalty</c> SIN emitir ND, el audit obligatorio
/// (Condicion 1 del review), idempotencia (waive doble => 409), el candado compartido con confirmar la multa,
/// el permiso, y la VERIFICACION downstream (Condicion 2): waive no toca las lineas (asi el cierre por reembolso
/// total sigue funcionando) ni aparece en la bandeja de NDs huerfanas.</para>
/// </summary>
public class CancellationWaivePenaltyTests
{
    // ============================================================
    // Builders (espejo minimal de CancellationDeferredPenaltyTests)
    // ============================================================

    private static AppDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"waive-penalty-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private sealed record Harness(
        BookingCancellationService Service,
        AppDbContext Ctx,
        Mock<IInvoiceService> InvoiceMock,
        Mock<IAuditService> AuditMock,
        OperationalFinanceSettings Settings);

    private static Harness BuildService(bool flagOn = true)
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
            EnableCancellationDebitNote = flagOn,
            CancellationDebitNoteGraceDays = 15,
            CancellationDebitNoteHardWarnDays = 60,
            CancellationDebitNoteFourEyesThreshold = 2_000_000m,
        };
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var service = new BookingCancellationService(
            ctx,
            invoiceMock.Object,
            approvalMock.Object,
            auditMock.Object,
            NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object,
            calculatorMock.Object,
            adminCountMock.Object);

        return new Harness(service, ctx, invoiceMock, auditMock, settings);
    }

    /// <summary>
    /// Semilla del caso post-NC: factura C=11 con CAE, reserva en PendingOperatorRefund, supplier, BC en
    /// AwaitingOperatorRefund con la NC total ya emitida (CreditNoteInvoiceId), concepto pass-through + penalidad
    /// Estimated (estado tipico al llegar el momento de resolver la pata del operador).
    /// </summary>
    private static async Task<(Guid BcPublicId, BookingCancellation Bc, Supplier Supplier, Reserva Reserva)>
        SeedPostNcAsync(
            AppDbContext ctx,
            BookingCancellationStatus status = BookingCancellationStatus.AwaitingOperatorRefund,
            bool postNc = true)
    {
        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        var supplier = new Supplier { Name = "Operador X", IsActive = true, PenaltyOwnership = PenaltyOwnership.Operator };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-WAIVE",
            Name = "Reserva Test",
            PayerId = customer.Id,
            Status = EstadoReserva.PendingOperatorRefund,
            Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var original = new Invoice
        {
            TipoComprobante = 11,
            PuntoDeVenta = 1,
            NumeroComprobante = 100,
            CAE = "12345678",
            Resultado = "A",
            MonId = "PES",
            ImporteTotal = 100_000m,
            ImporteNeto = 100_000m,
            ImporteIva = 0m,
            ReservaId = reserva.Id,
            AnnulmentStatus = AnnulmentStatus.None,
        };
        ctx.Invoices.Add(original);
        await ctx.SaveChangesAsync();

        Invoice? creditNote = null;
        if (postNc)
        {
            creditNote = new Invoice
            {
                TipoComprobante = 13, // NC C
                PuntoDeVenta = 1,
                NumeroComprobante = 101,
                CAE = "99999999",
                Resultado = "A",
                ReservaId = reserva.Id,
                OriginalInvoiceId = original.Id,
            };
            ctx.Invoices.Add(creditNote);
            await ctx.SaveChangesAsync();
        }

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = original.Id,
            CreditNoteInvoiceId = creditNote?.Id,
            Status = status,
            Reason = "Cliente cancelo; el operador devuelve todo, sin multa",
            DraftedByUserId = "vendedor-1",
            ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-10),
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        return (bc.PublicId, bc, supplier, reserva);
    }

    /// <summary>Hace que CreateAsync inserte una ND en la BD InMemory y devuelva su DTO (para el path confirmar).</summary>
    private static void SetupCreateEmitsDebitNote(Harness h)
    {
        h.InvoiceMock
            .Setup(s => s.CreateAsync(
                It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateInvoiceRequest req, string? uid, string? uname, CancellationToken ct) =>
            {
                var reservaId = h.Ctx.Reservas.First().Id;
                var originalId = h.Ctx.Invoices.First(i => i.TipoComprobante == 11).Id;
                var nd = new Invoice
                {
                    TipoComprobante = 12, // ND C
                    PuntoDeVenta = 1,
                    NumeroComprobante = 200,
                    Resultado = "PENDING",
                    ReservaId = reservaId,
                    OriginalInvoiceId = originalId,
                };
                h.Ctx.Invoices.Add(nd);
                h.Ctx.SaveChanges();
                return new InvoiceDto { PublicId = nd.PublicId };
            });
    }

    // ============================================================
    // Cierre sin multa — happy path
    // ============================================================

    [Fact]
    public async Task Waive_ClearsPendingPenalty_WithoutEmittingDebitNote()
    {
        var h = BuildService();
        var (bcId, bc, _, reserva) = await SeedPostNcAsync(h.Ctx);

        // Antes de cerrar, la reserva TIENE una multa pendiente de resolver.
        Assert.True(await h.Service.HasPendingOperatorPenaltyAsync(reserva.PublicId, default));

        await h.Service.WaiveOperatorPenaltyAsync(
            bcId, "El operador confirmo por mail que no cobra multa.", "u", "U", default,
            userCanClassifyAgencyPenalty: true);

        var after = h.Ctx.BookingCancellations.Single();
        // Estado terminal "sin multa" + monto 0, SIN ninguna huella de ND.
        Assert.Equal(PenaltyStatus.Waived, after.PenaltyStatus);
        Assert.Equal(0m, after.PenaltyAmountAtEvent);
        Assert.Equal(DebitNoteStatus.NotApplicable, after.DebitNoteStatus);
        Assert.Null(after.DebitNoteInvoiceId);
        // Rastro de quien/cuando resolvio.
        Assert.Equal("u", after.PenaltyConfirmedByUserId);
        Assert.NotNull(after.PenaltyConfirmedAt);

        // El boton pendiente se limpia (Condicion del pedido).
        Assert.False(await h.Service.HasPendingOperatorPenaltyAsync(reserva.PublicId, default));

        // NO se emitio ningun comprobante fiscal.
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Waive_EmitsMandatoryAudit_WithReason()
    {
        var h = BuildService();
        var (bcId, _, _, _) = await SeedPostNcAsync(h.Ctx);

        await h.Service.WaiveOperatorPenaltyAsync(
            bcId, "Sin penalidad segun acuerdo comercial.", "u", "U", default,
            userCanClassifyAgencyPenalty: true);

        // Condicion 1 (mandatory): audit OperatorPenaltyWaived con el actor.
        h.AuditMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.OperatorPenaltyWaived,
            AuditActions.BookingCancellationEntityName,
            It.IsAny<string>(),
            It.Is<string>(details => details.Contains("Sin penalidad segun acuerdo comercial.")),
            "u",
            "U",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ============================================================
    // Condicion 2 — verificacion downstream
    // ============================================================

    [Fact]
    public async Task Waive_DoesNotTouchOperatorLines_SoCloseByRefundStillWorks()
    {
        var h = BuildService();
        var (bcId, bc, supplier, _) = await SeedPostNcAsync(h.Ctx);

        // Linea del operador que ESPERA reembolso completo (RefundCap = total pagado, sin multa).
        var line = new BookingCancellationLine
        {
            BookingCancellationId = bc.Id,
            SupplierId = supplier.Id,
            Currency = Monedas.ARS,
            RefundCap = 100_000m,
            ReceivedRefundAmount = 0m,
            RefundStatus = BookingCancellationLineRefundStatus.None,
        };
        h.Ctx.BookingCancellationLines.Add(line);
        await h.Ctx.SaveChangesAsync();

        await h.Service.WaiveOperatorPenaltyAsync(
            bcId, "El operador no cobra multa.", "u", "U", default,
            userCanClassifyAgencyPenalty: true);

        // El cap del reembolso esperado queda COMPLETO y la linea sin penalidad: el cierre por reembolso total
        // (CloseReservaIfOperatorRefundComplete, que keyea por RefundCap>0 + Settled) no se ve afectado.
        var afterLine = h.Ctx.BookingCancellationLines.Single();
        Assert.Equal(100_000m, afterLine.RefundCap);
        Assert.Null(afterLine.PenaltyAmount);
        Assert.Equal(PenaltyStatus.Estimated, afterLine.PenaltyStatus); // no se toca la linea
    }

    [Fact]
    public async Task Waive_DoesNotAppearInMissingDebitNoteTray()
    {
        var h = BuildService();
        var (bcId, _, _, _) = await SeedPostNcAsync(h.Ctx);

        await h.Service.WaiveOperatorPenaltyAsync(
            bcId, "Operador no cobra multa.", "u", "U", default,
            userCanClassifyAgencyPenalty: true);

        // Condicion 2 clave: una cancelacion cerrada SIN multa NUNCA debe pedir que se emita una ND. La bandeja
        // de NDs huerfanas keyea por PenaltyStatus==Confirmed; Waived la excluye.
        var rows = await h.Service.GetCancellationsWithMissingDebitNoteAsync(default);
        var bc = h.Ctx.BookingCancellations.Single();
        Assert.DoesNotContain(rows, r => r.BookingCancellationPublicId == bc.PublicId);
    }

    // ============================================================
    // Idempotencia + candado compartido con confirmar la multa
    // ============================================================

    [Fact]
    public async Task Waive_Twice_SecondCallRebounds409()
    {
        var h = BuildService();
        var (bcId, _, _, _) = await SeedPostNcAsync(h.Ctx);

        await h.Service.WaiveOperatorPenaltyAsync(
            bcId, "Sin multa.", "u", "U", default, userCanClassifyAgencyPenalty: true);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.WaiveOperatorPenaltyAsync(
                bcId, "Sin multa otra vez.", "u", "U", default, userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-WAIVE-003", ex.InvariantCode);
    }

    [Fact]
    public async Task Confirm_AfterWaive_Rebounds409()
    {
        var h = BuildService();
        var (bcId, _, _, _) = await SeedPostNcAsync(h.Ctx);

        await h.Service.WaiveOperatorPenaltyAsync(
            bcId, "Sin multa.", "u", "U", default, userCanClassifyAgencyPenalty: true);

        // Despues de cerrar sin multa, confirmar una multa real es contradictorio -> 409 idempotente.
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ConfirmPenaltyAsync(
                bcId,
                new ConfirmPenaltyRequest(
                    ConceptKind: CancellationConceptKind.AgencyManagementFee,
                    ConfirmedPenaltyAmount: 30_000m,
                    OperatorConfirmationDate: DateTime.UtcNow.AddDays(-2),
                    SupportingDocumentReference: "https://docs/mail.pdf"),
                "u", "U", false, default, userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-ADR014-003", ex.InvariantCode);
    }

    [Fact]
    public async Task Waive_AfterConfirm_Rebounds409()
    {
        var h = BuildService();
        var (bcId, _, _, _) = await SeedPostNcAsync(h.Ctx);
        SetupCreateEmitsDebitNote(h);

        // Confirmamos una multa real primero (la marca Confirmed se persiste).
        await h.Service.ConfirmPenaltyAsync(
            bcId,
            new ConfirmPenaltyRequest(
                ConceptKind: CancellationConceptKind.AgencyManagementFee,
                ConfirmedPenaltyAmount: 30_000m,
                OperatorConfirmationDate: DateTime.UtcNow.AddDays(-2),
                SupportingDocumentReference: "https://docs/mail.pdf"),
            "u", "U", false, default, userCanClassifyAgencyPenalty: true);

        // Ahora cerrar sin multa debe rebotar: la pata ya se resolvio (con multa).
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.WaiveOperatorPenaltyAsync(
                bcId, "Sin multa.", "u", "U", default, userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-WAIVE-003", ex.InvariantCode);
    }

    // ============================================================
    // Permiso / estado / flag
    // ============================================================

    [Fact]
    public async Task Waive_WithoutPermission_Throws()
    {
        var h = BuildService();
        var (bcId, _, _, _) = await SeedPostNcAsync(h.Ctx);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.WaiveOperatorPenaltyAsync(
                bcId, "Sin multa.", "u", "U", default, userCanClassifyAgencyPenalty: false));
        Assert.Equal("INV-WAIVE-PERM", ex.InvariantCode);
    }

    [Fact]
    public async Task Waive_BeforeCreditNoteIssued_Throws()
    {
        var h = BuildService();
        // BC sin NC total emitida (CreditNoteInvoiceId null + estado no post-NC).
        var (bcId, _, _, _) = await SeedPostNcAsync(
            h.Ctx, status: BookingCancellationStatus.Drafted, postNc: false);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.WaiveOperatorPenaltyAsync(
                bcId, "Sin multa.", "u", "U", default, userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-WAIVE-001", ex.InvariantCode);
    }

    [Fact]
    public async Task Waive_WithFlagOff_Throws()
    {
        var h = BuildService(flagOn: false);
        var (bcId, _, _, _) = await SeedPostNcAsync(h.Ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Service.WaiveOperatorPenaltyAsync(
                bcId, "Sin multa.", "u", "U", default, userCanClassifyAgencyPenalty: true));
    }

    [Fact]
    public async Task Waive_EmptyReason_Throws()
    {
        var h = BuildService();
        var (bcId, _, _, _) = await SeedPostNcAsync(h.Ctx);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Service.WaiveOperatorPenaltyAsync(
                bcId, "   ", "u", "U", default, userCanClassifyAgencyPenalty: true));
    }

    [Fact]
    public async Task Waive_UnknownBc_Throws404()
    {
        var h = BuildService();
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            h.Service.WaiveOperatorPenaltyAsync(
                Guid.NewGuid(), "Sin multa.", "u", "U", default, userCanClassifyAgencyPenalty: true));
    }

    // ============================================================
    // Sanity: confirmar una multa real sigue funcionando sin cambios
    // ============================================================

    [Fact]
    public async Task ConfirmRealPenalty_StillEmitsDebitNote_Unchanged()
    {
        var h = BuildService();
        var (bcId, _, _, _) = await SeedPostNcAsync(h.Ctx);

        // CreateAsync emite una ND en la BD InMemory.
        h.InvoiceMock
            .Setup(s => s.CreateAsync(
                It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateInvoiceRequest req, string? uid, string? uname, CancellationToken ct) =>
            {
                var reservaId = h.Ctx.Reservas.First().Id;
                var originalId = h.Ctx.Invoices.First(i => i.TipoComprobante == 11).Id;
                var nd = new Invoice
                {
                    TipoComprobante = 12, // ND C
                    PuntoDeVenta = 1,
                    NumeroComprobante = 200,
                    Resultado = "PENDING",
                    ReservaId = reservaId,
                    OriginalInvoiceId = originalId,
                };
                h.Ctx.Invoices.Add(nd);
                h.Ctx.SaveChanges();
                return new InvoiceDto { PublicId = nd.PublicId };
            });

        await h.Service.ConfirmPenaltyAsync(
            bcId,
            new ConfirmPenaltyRequest(
                ConceptKind: CancellationConceptKind.AgencyManagementFee,
                ConfirmedPenaltyAmount: 30_000m,
                OperatorConfirmationDate: DateTime.UtcNow.AddDays(-2),
                SupportingDocumentReference: "https://docs/mail.pdf"),
            "u", "U", false, default, userCanClassifyAgencyPenalty: true);

        var after = h.Ctx.BookingCancellations.Single();
        Assert.Equal(PenaltyStatus.Confirmed, after.PenaltyStatus);
        Assert.Equal(DebitNoteStatus.Pending, after.DebitNoteStatus);
        Assert.NotNull(after.DebitNoteInvoiceId);
    }

    // ============================================================
    // Reversa del cierre sin multa (RevertWaivedOperatorPenaltyAsync)
    // ============================================================

    [Fact]
    public async Task Revert_FlipsWaivedToEstimated_AndReEnablesPendingPenalty()
    {
        var h = BuildService();
        var (bcId, _, _, reserva) = await SeedPostNcAsync(h.Ctx);

        // Primero cerramos sin multa (queda Waived, sin pendiente).
        await h.Service.WaiveOperatorPenaltyAsync(
            bcId, "El operador no cobra multa.", "u", "U", default, userCanClassifyAgencyPenalty: true);
        Assert.False(await h.Service.HasPendingOperatorPenaltyAsync(reserva.PublicId, default));

        // Un Admin lo reabre.
        await h.Service.RevertWaivedOperatorPenaltyAsync(
            bcId, "Cargado por error: el operador si cobro.", "admin", "Admin", requesterIsAdmin: true, default);

        var after = h.Ctx.BookingCancellations.Single();
        // Vuelve LIMPIO al estado pendiente: Estimated + defaults restaurados (sin monto, sin confirmado-por).
        Assert.Equal(PenaltyStatus.Estimated, after.PenaltyStatus);
        Assert.Null(after.PenaltyAmountAtEvent);
        Assert.Null(after.PenaltyConfirmedByUserId);
        Assert.Null(after.PenaltyConfirmedByUserName);
        Assert.Null(after.PenaltyConfirmedAt);
        // Sin huella de ND (nunca la hubo).
        Assert.Equal(DebitNoteStatus.NotApplicable, after.DebitNoteStatus);
        Assert.Null(after.DebitNoteInvoiceId);

        // La multa vuelve a estar pendiente -> "Confirmar multa" disponible otra vez.
        Assert.True(await h.Service.HasPendingOperatorPenaltyAsync(reserva.PublicId, default));
    }

    [Fact]
    public async Task Revert_ReEnablesWaiveAgain()
    {
        var h = BuildService();
        var (bcId, _, _, _) = await SeedPostNcAsync(h.Ctx);

        await h.Service.WaiveOperatorPenaltyAsync(
            bcId, "Sin multa.", "u", "U", default, userCanClassifyAgencyPenalty: true);
        await h.Service.RevertWaivedOperatorPenaltyAsync(
            bcId, "Reabro para corregir.", "admin", "Admin", requesterIsAdmin: true, default);

        // Tras reabrir, volver a cerrar sin multa funciona (la pata quedo pendiente de nuevo).
        await h.Service.WaiveOperatorPenaltyAsync(
            bcId, "Confirmado: realmente no cobra multa.", "u", "U", default, userCanClassifyAgencyPenalty: true);

        Assert.Equal(PenaltyStatus.Waived, h.Ctx.BookingCancellations.Single().PenaltyStatus);
    }

    [Fact]
    public async Task Revert_EmitsMandatoryAudit_WithReason()
    {
        var h = BuildService();
        var (bcId, _, _, _) = await SeedPostNcAsync(h.Ctx);

        await h.Service.WaiveOperatorPenaltyAsync(
            bcId, "Sin multa.", "u", "U", default, userCanClassifyAgencyPenalty: true);

        await h.Service.RevertWaivedOperatorPenaltyAsync(
            bcId, "Multa tardia del operador.", "admin", "Admin", requesterIsAdmin: true, default);

        // Audit OBLIGATORIO OperatorPenaltyWaiveReverted con el actor y el motivo.
        h.AuditMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.OperatorPenaltyWaiveReverted,
            AuditActions.BookingCancellationEntityName,
            It.IsAny<string>(),
            It.Is<string>(details => details.Contains("Multa tardia del operador.")),
            "admin",
            "Admin",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Revert_NonAdmin_Throws()
    {
        var h = BuildService();
        var (bcId, _, _, _) = await SeedPostNcAsync(h.Ctx);

        await h.Service.WaiveOperatorPenaltyAsync(
            bcId, "Sin multa.", "u", "U", default, userCanClassifyAgencyPenalty: true);

        // Aunque tenga el permiso de clasificar penalidad, sin rol Admin NO puede reabrir (defensa en profundidad).
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.RevertWaivedOperatorPenaltyAsync(
                bcId, "Intento sin ser admin.", "vendedor", "Vendedor", requesterIsAdmin: false, default));
        Assert.Equal("INV-WAIVE-REVERT-PERM", ex.InvariantCode);

        // Y el estado NO cambio.
        Assert.Equal(PenaltyStatus.Waived, h.Ctx.BookingCancellations.Single().PenaltyStatus);
    }

    [Fact]
    public async Task Revert_WhenNotWaived_Throws409()
    {
        var h = BuildService();
        // BC recien sembrado: penalidad Estimated (NO cerrada sin multa).
        var (bcId, _, _, _) = await SeedPostNcAsync(h.Ctx);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.RevertWaivedOperatorPenaltyAsync(
                bcId, "No hay nada que reabrir.", "admin", "Admin", requesterIsAdmin: true, default));
        Assert.Equal("INV-WAIVE-REVERT-001", ex.InvariantCode);
    }

    [Fact]
    public async Task Revert_Twice_SecondCallRebounds409()
    {
        var h = BuildService();
        var (bcId, _, _, _) = await SeedPostNcAsync(h.Ctx);

        await h.Service.WaiveOperatorPenaltyAsync(
            bcId, "Sin multa.", "u", "U", default, userCanClassifyAgencyPenalty: true);
        await h.Service.RevertWaivedOperatorPenaltyAsync(
            bcId, "Reabro.", "admin", "Admin", requesterIsAdmin: true, default);

        // Segunda reversa: ya esta Estimated (no Waived) -> rebota 409 idempotente.
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.RevertWaivedOperatorPenaltyAsync(
                bcId, "Reabro otra vez.", "admin", "Admin", requesterIsAdmin: true, default));
        Assert.Equal("INV-WAIVE-REVERT-001", ex.InvariantCode);
    }

    [Fact]
    public async Task Revert_OnConfirmedPenalty_Throws409()
    {
        var h = BuildService();
        var (bcId, _, _, _) = await SeedPostNcAsync(h.Ctx);
        SetupCreateEmitsDebitNote(h);

        // Confirmamos una multa REAL (queda Confirmed con ND): la reversa de waive NO aplica aca.
        await h.Service.ConfirmPenaltyAsync(
            bcId,
            new ConfirmPenaltyRequest(
                ConceptKind: CancellationConceptKind.AgencyManagementFee,
                ConfirmedPenaltyAmount: 30_000m,
                OperatorConfirmationDate: DateTime.UtcNow.AddDays(-2),
                SupportingDocumentReference: "https://docs/mail.pdf"),
            "u", "U", false, default, userCanClassifyAgencyPenalty: true);

        // No esta Waived -> 409. (El guard INV-WAIVE-REVERT-001 corre antes que el de la ND.)
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.RevertWaivedOperatorPenaltyAsync(
                bcId, "Intento reabrir una multa confirmada.", "admin", "Admin", requesterIsAdmin: true, default));
        Assert.Equal("INV-WAIVE-REVERT-001", ex.InvariantCode);
    }

    [Fact]
    public async Task FullCycle_WaiveThenRevertThenConfirmRealPenalty_EmitsDebitNote()
    {
        var h = BuildService();
        var (bcId, _, _, _) = await SeedPostNcAsync(h.Ctx);
        SetupCreateEmitsDebitNote(h);

        // 1) Se cierra sin multa.
        await h.Service.WaiveOperatorPenaltyAsync(
            bcId, "Aparentemente no cobra multa.", "u", "U", default, userCanClassifyAgencyPenalty: true);

        // 2) Admin reabre (el operador termino cobrando una multa tardia).
        await h.Service.RevertWaivedOperatorPenaltyAsync(
            bcId, "Multa tardia del operador.", "admin", "Admin", requesterIsAdmin: true, default);

        // 3) Se confirma la multa REAL -> emite la ND como en el flujo normal.
        await h.Service.ConfirmPenaltyAsync(
            bcId,
            new ConfirmPenaltyRequest(
                ConceptKind: CancellationConceptKind.AgencyManagementFee,
                ConfirmedPenaltyAmount: 25_000m,
                OperatorConfirmationDate: DateTime.UtcNow.AddDays(-1),
                SupportingDocumentReference: "https://docs/mail-tardio.pdf"),
            "u", "U", false, default, userCanClassifyAgencyPenalty: true);

        var after = h.Ctx.BookingCancellations.Single();
        Assert.Equal(PenaltyStatus.Confirmed, after.PenaltyStatus);
        Assert.Equal(DebitNoteStatus.Pending, after.DebitNoteStatus);
        Assert.NotNull(after.DebitNoteInvoiceId);
    }

    [Fact]
    public async Task Revert_EmptyReason_Throws()
    {
        var h = BuildService();
        var (bcId, _, _, _) = await SeedPostNcAsync(h.Ctx);
        await h.Service.WaiveOperatorPenaltyAsync(
            bcId, "Sin multa.", "u", "U", default, userCanClassifyAgencyPenalty: true);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Service.RevertWaivedOperatorPenaltyAsync(
                bcId, "   ", "admin", "Admin", requesterIsAdmin: true, default));
    }

    [Fact]
    public async Task Revert_UnknownBc_Throws404()
    {
        var h = BuildService();
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            h.Service.RevertWaivedOperatorPenaltyAsync(
                Guid.NewGuid(), "Sin BC.", "admin", "Admin", requesterIsAdmin: true, default));
    }

    [Fact]
    public async Task Revert_WithFlagOff_Throws()
    {
        var h = BuildService(flagOn: false);
        var (bcId, _, _, _) = await SeedPostNcAsync(h.Ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Service.RevertWaivedOperatorPenaltyAsync(
                bcId, "Flag off.", "admin", "Admin", requesterIsAdmin: true, default));
    }

    // ============================================================
    // Fase A (2026-06-28): GetOperatorPenaltyOutcomeAsync — el RESULTADO de la pata del operador que la ficha
    // de la reserva lee al cargar (None / Pending / Confirmed / Waived).
    // ============================================================

    [Fact]
    public async Task Outcome_WhenPendingPenalty_IsPending()
    {
        var h = BuildService();
        var (_, _, _, reserva) = await SeedPostNcAsync(h.Ctx);

        // Recien sembrada (penalidad Estimated, NC con CAE): la pata del operador esta pendiente de resolver.
        Assert.Equal(
            OperatorPenaltyOutcome.Pending,
            await h.Service.GetOperatorPenaltyOutcomeAsync(reserva.PublicId, default));
    }

    [Fact]
    public async Task Outcome_AfterWaive_IsWaived()
    {
        var h = BuildService();
        var (bcId, _, _, reserva) = await SeedPostNcAsync(h.Ctx);

        await h.Service.WaiveOperatorPenaltyAsync(
            bcId, "El operador no cobra multa.", "u", "U", default, userCanClassifyAgencyPenalty: true);

        // Cerrada sin multa -> Waived (lo que el front necesita para "Cerrada sin multa del operador").
        Assert.Equal(
            OperatorPenaltyOutcome.Waived,
            await h.Service.GetOperatorPenaltyOutcomeAsync(reserva.PublicId, default));
        // Y NO sigue figurando como pendiente.
        Assert.NotEqual(
            OperatorPenaltyOutcome.Pending,
            await h.Service.GetOperatorPenaltyOutcomeAsync(reserva.PublicId, default));
    }

    [Fact]
    public async Task Outcome_AfterConfirmRealPenalty_IsConfirmed()
    {
        var h = BuildService();
        var (bcId, _, _, reserva) = await SeedPostNcAsync(h.Ctx);
        SetupCreateEmitsDebitNote(h);

        await h.Service.ConfirmPenaltyAsync(
            bcId,
            new ConfirmPenaltyRequest(
                ConceptKind: CancellationConceptKind.AgencyManagementFee,
                ConfirmedPenaltyAmount: 30_000m,
                OperatorConfirmationDate: DateTime.UtcNow.AddDays(-2),
                SupportingDocumentReference: "https://docs/mail.pdf"),
            "u", "U", false, default, userCanClassifyAgencyPenalty: true);

        Assert.Equal(
            OperatorPenaltyOutcome.Confirmed,
            await h.Service.GetOperatorPenaltyOutcomeAsync(reserva.PublicId, default));
    }

    [Fact]
    public async Task Outcome_WhenNoCancellation_IsNone()
    {
        var h = BuildService();
        var reserva = new Reserva
        {
            NumeroReserva = "R-NOCANCEL",
            Name = "Reserva sin cancelacion",
            Status = EstadoReserva.Confirmed,
            Balance = 0m,
        };
        h.Ctx.Reservas.Add(reserva);
        await h.Ctx.SaveChangesAsync();

        Assert.Equal(
            OperatorPenaltyOutcome.None,
            await h.Service.GetOperatorPenaltyOutcomeAsync(reserva.PublicId, default));
    }

    // ============================================================
    // Issue 2 (2026-06-28): saneo de mensajes — ningun mensaje de cara al usuario debe filtrar el slug del
    // permiso, nombres de campos internos, ni nombres de enums.
    // ============================================================

    /// <summary>Cadenas tecnicas que NUNCA deben aparecer en un mensaje mostrado al usuario.</summary>
    private static readonly string[] ForbiddenLeakTokens =
    {
        "cancellations.classify_agency_penalty",
        "CreditNoteInvoiceId",
        "DebitNoteInvoiceId",
        "PenaltyStatus",
        "DebitNoteStatus",
        "bc.Status",
        "Estado actual",
    };

    private static void AssertNoTechnicalLeak(string message)
    {
        foreach (var token in ForbiddenLeakTokens)
            Assert.DoesNotContain(token, message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Waive_WithoutPermission_MessageHasNoSlug()
    {
        var h = BuildService();
        var (bcId, _, _, _) = await SeedPostNcAsync(h.Ctx);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.WaiveOperatorPenaltyAsync(
                bcId, "Sin multa.", "u", "U", default, userCanClassifyAgencyPenalty: false));
        AssertNoTechnicalLeak(ex.Message);
        Assert.Contains("permiso", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Confirm_WithoutPermission_MessageHasNoSlug()
    {
        var h = BuildService();
        var (bcId, _, _, _) = await SeedPostNcAsync(h.Ctx);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ConfirmPenaltyAsync(
                bcId,
                new ConfirmPenaltyRequest(
                    ConceptKind: CancellationConceptKind.AgencyManagementFee,
                    ConfirmedPenaltyAmount: 30_000m,
                    OperatorConfirmationDate: DateTime.UtcNow.AddDays(-2),
                    SupportingDocumentReference: "https://docs/mail.pdf"),
                "u", "U", false, default, userCanClassifyAgencyPenalty: false));
        Assert.Equal("INV-ADR014-PERM", ex.InvariantCode);
        AssertNoTechnicalLeak(ex.Message);
    }

    [Fact]
    public async Task Waive_BeforeCreditNoteIssued_MessageHasNoInternalFields()
    {
        var h = BuildService();
        var (bcId, _, _, _) = await SeedPostNcAsync(
            h.Ctx, status: BookingCancellationStatus.Drafted, postNc: false);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.WaiveOperatorPenaltyAsync(
                bcId, "Sin multa.", "u", "U", default, userCanClassifyAgencyPenalty: true));
        AssertNoTechnicalLeak(ex.Message);
    }

    [Fact]
    public async Task Confirm_BeforeCreditNoteIssued_MessageHasNoInternalFields()
    {
        var h = BuildService();
        var (bcId, _, _, _) = await SeedPostNcAsync(
            h.Ctx, status: BookingCancellationStatus.Drafted, postNc: false);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ConfirmPenaltyAsync(
                bcId,
                new ConfirmPenaltyRequest(
                    ConceptKind: CancellationConceptKind.AgencyManagementFee,
                    ConfirmedPenaltyAmount: 30_000m,
                    OperatorConfirmationDate: DateTime.UtcNow.AddDays(-2),
                    SupportingDocumentReference: "https://docs/mail.pdf"),
                "u", "U", false, default, userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-ADR014-001", ex.InvariantCode);
        AssertNoTechnicalLeak(ex.Message);
    }

    [Fact]
    public async Task Waive_Twice_SecondMessageHasNoEnumNames()
    {
        var h = BuildService();
        var (bcId, _, _, _) = await SeedPostNcAsync(h.Ctx);

        await h.Service.WaiveOperatorPenaltyAsync(
            bcId, "Sin multa.", "u", "U", default, userCanClassifyAgencyPenalty: true);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.WaiveOperatorPenaltyAsync(
                bcId, "Otra vez.", "u", "U", default, userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-WAIVE-003", ex.InvariantCode);
        AssertNoTechnicalLeak(ex.Message);
    }

    [Fact]
    public async Task Confirm_AfterResolved_MessageHasNoEnumNames()
    {
        var h = BuildService();
        var (bcId, _, _, _) = await SeedPostNcAsync(h.Ctx);

        await h.Service.WaiveOperatorPenaltyAsync(
            bcId, "Sin multa.", "u", "U", default, userCanClassifyAgencyPenalty: true);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ConfirmPenaltyAsync(
                bcId,
                new ConfirmPenaltyRequest(
                    ConceptKind: CancellationConceptKind.AgencyManagementFee,
                    ConfirmedPenaltyAmount: 30_000m,
                    OperatorConfirmationDate: DateTime.UtcNow.AddDays(-2),
                    SupportingDocumentReference: "https://docs/mail.pdf"),
                "u", "U", false, default, userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-ADR014-003", ex.InvariantCode);
        AssertNoTechnicalLeak(ex.Message);
    }
}
