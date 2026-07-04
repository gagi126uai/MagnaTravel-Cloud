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
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// FIX B (2026-07-04): tests UNIT (EF InMemory, sin Docker) de la RED DE SEGURIDAD para el aviso de AFIP perdido
/// en la NC TOTAL (<see cref="IBookingCancellationService.ReconcileStuckFiscalConfirmationsAsync"/>). Cuando la
/// NC ya tiene resultado final de AFIP (CAE "A" o rechazo "R") pero el callback del bridge murio, la cancelacion
/// queda trabada en <c>AwaitingFiscalConfirmation</c>; el metodo re-aplica el MISMO callback (idempotente).
///
/// <para>Reusa el flujo real Draft -&gt; Confirm (crea las hijas ADR-042 + el pago al operador) y simula que AFIP
/// ya emitio/rechazo la NC creando la <c>Invoice</c> de NC "a mano" (como haria <c>ProcessAnnulmentJob</c>), sin
/// disparar el callback. Despues corre la reconciliacion. InMemory NO valida el lock <c>FOR UPDATE</c> ni xmin
/// (eso corre en integracion); aca se cubre la LOGICA de deteccion + re-aplicacion + idempotencia.</para>
/// </summary>
public class TotalCreditNoteBridgeReconciliationTests
{
    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"total-nc-reconcile-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private sealed record Harness(BookingCancellationService Service, AppDbContext Ctx, Mock<IInvoiceService> InvoiceMock);

    private static Harness BuildService()
    {
        var ctx = NewDbContext();
        var invoiceMock = new Mock<IInvoiceService>();
        var approvalMock = new Mock<IApprovalRequestService>();
        var auditMock = new Mock<IAuditService>();
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        var calculatorMock = new Mock<IFiscalLiquidationCalculator>();
        var adminCountMock = new Mock<IAdminUserCountService>();

        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableNewCancellationFlow = true,
                EnablePartialCreditNotes = false,
                EnableCancellationDebitNote = false,
                OperatorRefundTimeoutDays = 60,
                BridgeReconciliationStalenessMinutes = 30,
            });

        invoiceMock
            .Setup(s => s.EnqueueAnnulmentAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<int?>()))
            .Returns(Task.CompletedTask);

        var service = new BookingCancellationService(
            ctx, invoiceMock.Object, approvalMock.Object, auditMock.Object,
            NullLogger<BookingCancellationService>.Instance, settingsMock.Object,
            calculatorMock.Object, adminCountMock.Object);

        return new Harness(service, ctx, invoiceMock);
    }

    private static ConfirmCancellationRequest NewConfirmRequest() =>
        new(
            SnapshotData: new FiscalSnapshotData(
                CurrencyAtEvent: "ARS",
                ExchangeRateAtOriginalInvoice: 1m,
                Source: ExchangeRateSource.BCRA_A3500,
                ManualJustification: null,
                AgencyTaxConditionAtEvent: "MONOTRIBUTISTA",
                SupplierTaxConditionAtEvent: "IVA_RESP_INSCRIPTO",
                CustomerTaxConditionAtEvent: "CONSUMIDOR_FINAL"),
            IsAdminOverride: false,
            OverrideReason: null,
            ApprovalRequestPublicId: null);

    private static async Task<(Reserva reserva, Supplier supplier)> SeedReservaAsync(Harness h, bool payOperator = true)
    {
        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        var supplier = new Supplier { Name = "Operador Unico", IsActive = true };
        h.Ctx.Customers.Add(customer);
        h.Ctx.Suppliers.Add(supplier);
        await h.Ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-FIXB",
            Name = "Reserva NC total",
            PayerId = customer.Id,
            Status = EstadoReserva.Confirmed,
            Balance = 0m,
        };
        h.Ctx.Reservas.Add(reserva);
        await h.Ctx.SaveChangesAsync();

        h.Ctx.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            Status = "Confirmado",
            NetCost = 40_000m,
            SalePrice = 60_000m,
            Currency = "ARS",
        });
        await h.Ctx.SaveChangesAsync();

        // Pago al operador -> receivable "me tiene que devolver" > 0, asi la anulacion va a AwaitingOperatorRefund
        // (no se auto-cierra). Los tests de auto-cierre lo omiten (payOperator: false).
        if (payOperator)
        {
            h.Ctx.SupplierPayments.Add(new SupplierPayment
            {
                SupplierId = supplier.Id,
                ReservaId = reserva.Id,
                Amount = 40_000m,
                Currency = "ARS",
                Method = "T",
            });
            await h.Ctx.SaveChangesAsync();
        }

        return (reserva, supplier);
    }

    private static async Task<Invoice> AddSaleInvoiceAsync(Harness h, int reservaId, long numero)
    {
        var inv = new Invoice
        {
            TipoComprobante = 6,
            PuntoDeVenta = 1,
            NumeroComprobante = numero,
            CAE = "cae-" + numero,
            Resultado = "A",
            MonId = "PES",
            MonCotiz = 1m,
            ImporteTotal = 60_000m,
            ImporteNeto = 60_000m,
            ImporteIva = 0m,
            ReservaId = reservaId,
            AnnulmentStatus = AnnulmentStatus.None,
        };
        h.Ctx.Invoices.Add(inv);
        await h.Ctx.SaveChangesAsync();
        return inv;
    }

    /// <summary>Simula la NC que ARCA ya emitio/rechazo (como haria ProcessAnnulmentJob), SIN disparar el callback.</summary>
    private static async Task AddCreditNoteAsync(
        Harness h, int reservaId, int originalInvoiceId, long numero, string resultado, string? cae)
    {
        h.Ctx.Invoices.Add(new Invoice
        {
            TipoComprobante = 8, // NC B
            PuntoDeVenta = 1,
            NumeroComprobante = numero,
            CAE = cae,
            Resultado = resultado,
            ReservaId = reservaId,
            OriginalInvoiceId = originalInvoiceId,
        });
        await h.Ctx.SaveChangesAsync();
    }

    /// <summary>Confirma la anulacion y backdatea ConfirmedWithClientAt para que sea candidata del barrido.</summary>
    private static async Task<BookingCancellation> ConfirmAndBackdateAsync(Harness h, Reserva reserva)
    {
        var draft = await h.Service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Cliente anula el viaje completo por sistema"),
            "vendedor-1", "Vendedor Uno", CancellationToken.None);
        await h.Service.ConfirmAsync(
            draft.PublicId, NewConfirmRequest(), "vendedor-1", "Vendedor Uno", false, CancellationToken.None);

        var bc = await h.Ctx.BookingCancellations.FirstAsync(b => b.PublicId == draft.PublicId);
        Assert.Equal(BookingCancellationStatus.AwaitingFiscalConfirmation, bc.Status);
        // Antiguedad: la BC entro a AwaitingFiscalConfirmation hace mas que el umbral (30 min).
        bc.ConfirmedWithClientAt = DateTime.UtcNow.AddHours(-1);
        await h.Ctx.SaveChangesAsync();
        return bc;
    }

    // =====================================================================================

    [Fact]
    public async Task Reconcile_ncApproved_transitionsToAwaitingOperatorRefund()
    {
        var h = BuildService();
        var (reserva, _) = await SeedReservaAsync(h);
        var inv = await AddSaleInvoiceAsync(h, reserva.Id, 7001);
        var bc = await ConfirmAndBackdateAsync(h, reserva);

        // AFIP ya aprobo la NC (CAE presente) pero el callback se perdio.
        await AddCreditNoteAsync(h, reserva.Id, inv.Id, 8001, resultado: "A", cae: "cae-nc-8001");

        var reconciled = await h.Service.ReconcileStuckFiscalConfirmationsAsync(CancellationToken.None);

        Assert.Equal(1, reconciled);
        var after = await h.Ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, after.Status);
    }

    [Fact]
    public async Task Reconcile_ncApprovedButNoOperatorMoney_autoClosesCancellation()
    {
        var h = BuildService();
        var (reserva, _) = await SeedReservaAsync(h, payOperator: false); // nunca se le pago al operador
        var inv = await AddSaleInvoiceAsync(h, reserva.Id, 7101);
        var bc = await ConfirmAndBackdateAsync(h, reserva);

        // Sin pago al operador, los caps de las lineas son 0 -> receivable $0. Al destrabar, se auto-cierra.
        await AddCreditNoteAsync(h, reserva.Id, inv.Id, 8101, resultado: "A", cae: "cae-nc-8101");

        var reconciled = await h.Service.ReconcileStuckFiscalConfirmationsAsync(CancellationToken.None);

        Assert.Equal(1, reconciled);
        var after = await h.Ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.Closed, after.Status);
        var reservaAfter = await h.Ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.Cancelled, reservaAfter.Status);
    }

    [Fact]
    public async Task Reconcile_ncRejected_transitionsToArcaRejected()
    {
        var h = BuildService();
        var (reserva, _) = await SeedReservaAsync(h);
        var inv = await AddSaleInvoiceAsync(h, reserva.Id, 7201);
        var bc = await ConfirmAndBackdateAsync(h, reserva);

        // AFIP rechazo la NC (sin CAE, Resultado "R").
        await AddCreditNoteAsync(h, reserva.Id, inv.Id, 8201, resultado: "R", cae: null);

        var reconciled = await h.Service.ReconcileStuckFiscalConfirmationsAsync(CancellationToken.None);

        Assert.Equal(1, reconciled);
        var after = await h.Ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.ArcaRejected, after.Status);
    }

    [Fact]
    public async Task Reconcile_ncStillPendingAtAfip_doesNotTouch()
    {
        var h = BuildService();
        var (reserva, _) = await SeedReservaAsync(h);
        await AddSaleInvoiceAsync(h, reserva.Id, 7301);
        var bc = await ConfirmAndBackdateAsync(h, reserva);

        // NO agregamos NC: AFIP todavia no respondio. El barrido no debe tocar nada.
        var reconciled = await h.Service.ReconcileStuckFiscalConfirmationsAsync(CancellationToken.None);

        Assert.Equal(0, reconciled);
        var after = await h.Ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.AwaitingFiscalConfirmation, after.Status);
    }

    [Fact]
    public async Task Reconcile_freshlyConfirmed_notYetStale_doesNotTouch()
    {
        var h = BuildService();
        var (reserva, _) = await SeedReservaAsync(h);
        var inv = await AddSaleInvoiceAsync(h, reserva.Id, 7401);

        // Confirmamos pero NO backdateamos: ConfirmedWithClientAt = ahora -> aun no supera el umbral de antiguedad.
        var draft = await h.Service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Cliente anula el viaje completo por sistema"),
            "vendedor-1", "Vendedor Uno", CancellationToken.None);
        await h.Service.ConfirmAsync(draft.PublicId, NewConfirmRequest(), "vendedor-1", "Vendedor Uno", false, CancellationToken.None);
        var bc = await h.Ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == draft.PublicId);
        await AddCreditNoteAsync(h, reserva.Id, inv.Id, 8401, resultado: "A", cae: "cae-nc-8401");

        var reconciled = await h.Service.ReconcileStuckFiscalConfirmationsAsync(CancellationToken.None);

        Assert.Equal(0, reconciled);
        var after = await h.Ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.AwaitingFiscalConfirmation, after.Status);
    }

    [Fact]
    public async Task Reconcile_runTwice_isIdempotent()
    {
        var h = BuildService();
        var (reserva, _) = await SeedReservaAsync(h);
        var inv = await AddSaleInvoiceAsync(h, reserva.Id, 7501);
        var bc = await ConfirmAndBackdateAsync(h, reserva);
        await AddCreditNoteAsync(h, reserva.Id, inv.Id, 8501, resultado: "A", cae: "cae-nc-8501");

        var first = await h.Service.ReconcileStuckFiscalConfirmationsAsync(CancellationToken.None);
        var second = await h.Service.ReconcileStuckFiscalConfirmationsAsync(CancellationToken.None);

        Assert.Equal(1, first);
        Assert.Equal(0, second); // ya salio de AwaitingFiscalConfirmation -> no vuelve a ser candidata
        var after = await h.Ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, after.Status);
    }

    [Fact]
    public async Task Reconcile_moduleDisabled_isNoOp()
    {
        var ctx = NewDbContext();
        var invoiceMock = new Mock<IInvoiceService>();
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableNewCancellationFlow = false });

        var service = new BookingCancellationService(
            ctx, invoiceMock.Object, new Mock<IApprovalRequestService>().Object, new Mock<IAuditService>().Object,
            NullLogger<BookingCancellationService>.Instance, settingsMock.Object,
            new Mock<IFiscalLiquidationCalculator>().Object, new Mock<IAdminUserCountService>().Object);

        var reconciled = await service.ReconcileStuckFiscalConfirmationsAsync(CancellationToken.None);
        Assert.Equal(0, reconciled);
    }
}
