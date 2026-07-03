using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Application.Exceptions;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-042 (2026-07-01): tests UNIT (EF InMemory, sin Docker) del flujo de anular una reserva con VARIAS
/// facturas en distintas monedas: creacion de las filas hijas al confirmar, la reevaluacion de completitud en
/// los callbacks de ARCA (todas OK / parcial), el retry idempotente de las faltantes, el guard de liberacion
/// INV-081 con hija viva, el pre-flight de TC=1 y la no-regresion del caso mono-factura.
///
/// <para><b>Trade-off (igual que el resto del modulo)</b>: InMemory NO valida CHECK SQL, xmin, transacciones
/// ni el lock pesimista <c>FOR UPDATE</c> (eso corre en integracion Postgres). Aca se cubre la LOGICA:
/// creacion de hijas, conteo de completitud, transiciones y el reparto por-hija.</para>
/// </summary>
public class Adr042MultiInvoiceCancellationTests
{
    private static AppDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"adr042-tests-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private sealed record Harness(
        BookingCancellationService Service,
        AppDbContext Ctx,
        Mock<IInvoiceService> InvoiceMock,
        Mock<IApprovalRequestService> ApprovalMock,
        Mock<IAuditService> AuditMock);

    private static Harness BuildService(
        bool requireApprovalForInvoiceAnnulment = false,
        IApprovalPolicyService? approvalPolicy = null)
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
                // Por defecto OFF en el harness: el gate del InvoiceAnnulment lo cubre el mock de IInvoiceService.
                // Los tests de N10 lo prenden explicitamente para ejercer la resolucion pre-commit multi-factura.
                RequireApprovalForInvoiceAnnulment = requireApprovalForInvoiceAnnulment,
            });

        // Enqueue mocks: devuelven completado. La senal "job en vuelo" (AnnulmentStatus=Pending) la escribe
        // AHORA el propio service DENTRO del lock del retry (F1), no el mock; por eso el mock no necesita
        // simularla. El confirm usa EnqueueAnnulmentAsync; el retry usa EnqueueAnnulmentRetryAsync.
        invoiceMock
            .Setup(s => s.EnqueueAnnulmentAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<int?>()))
            .Returns(Task.CompletedTask);
        invoiceMock
            .Setup(s => s.EnqueueAnnulmentRetryAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = new BookingCancellationService(
            ctx, invoiceMock.Object, approvalMock.Object, auditMock.Object,
            NullLogger<BookingCancellationService>.Instance, settingsMock.Object,
            calculatorMock.Object, adminCountMock.Object,
            approvalPolicyService: approvalPolicy);

        return new Harness(service, ctx, invoiceMock, approvalMock, auditMock);
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

    /// <summary>Siembra reserva + hotel + operador. Devuelve (reserva, customer, supplier).</summary>
    private static async Task<(Reserva reserva, Customer customer, Supplier supplier)> SeedReservaAsync(Harness h)
    {
        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        var supplier = new Supplier { Name = "Operador Unico", IsActive = true };
        h.Ctx.Customers.Add(customer);
        h.Ctx.Suppliers.Add(supplier);
        await h.Ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-042",
            Name = "Reserva multi-factura",
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

        // (2026-07-03) Pago al operador imputado a esta reserva: le da al servicio un RefundCap > 0, o sea un
        // receivable "me tiene que devolver" real. Sin esto el cierre automatico por receivable $0 (nuevo) cerraria
        // la anulacion directo y estos tests no llegarian a AwaitingOperatorRefund, que es lo que quieren verificar.
        h.Ctx.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplier.Id,
            ReservaId = reserva.Id,
            Amount = 40_000m,
            Currency = "ARS",
            Method = "T",
        });
        await h.Ctx.SaveChangesAsync();

        return (reserva, customer, supplier);
    }

    /// <summary>Crea una factura de venta viva (con CAE) de la reserva.</summary>
    private static async Task<Invoice> AddSaleInvoiceAsync(
        Harness h, int reservaId, int tipo, long numero, string monId, decimal monCotiz, decimal total)
    {
        var inv = new Invoice
        {
            TipoComprobante = tipo,
            PuntoDeVenta = 1,
            NumeroComprobante = numero,
            CAE = "cae-" + numero,
            Resultado = "A",
            MonId = monId,
            MonCotiz = monCotiz,
            ImporteTotal = total,
            ImporteNeto = total,
            ImporteIva = 0m,
            ReservaId = reservaId,
            AnnulmentStatus = AnnulmentStatus.None,
        };
        h.Ctx.Invoices.Add(inv);
        await h.Ctx.SaveChangesAsync();
        return inv;
    }

    /// <summary>Crea una NC (simulando la que ARCA aprobo) para una factura origen.</summary>
    private static async Task<Invoice> AddCreditNoteAsync(
        Harness h, int reservaId, int originalInvoiceId, int tipoNc, long numero, string resultado = "A", string? cae = "cae-nc")
    {
        var nc = new Invoice
        {
            TipoComprobante = tipoNc,
            PuntoDeVenta = 1,
            NumeroComprobante = numero,
            CAE = cae,
            Resultado = resultado,
            ReservaId = reservaId,
            OriginalInvoiceId = originalInvoiceId,
        };
        h.Ctx.Invoices.Add(nc);
        await h.Ctx.SaveChangesAsync();
        return nc;
    }

    // ============================================================
    // Confirm multi-factura -> N hijas Pending + N enqueues
    // ============================================================

    [Fact]
    public async Task Confirm_DosFacturas_CreaDosHijasPending_YEncolaUnaPorFactura()
    {
        var h = BuildService();
        var (reserva, _, _) = await SeedReservaAsync(h);
        var usd = await AddSaleInvoiceAsync(h, reserva.Id, tipo: 6, numero: 1001, monId: "DOL", monCotiz: 1000m, total: 200m);
        var ars = await AddSaleInvoiceAsync(h, reserva.Id, tipo: 6, numero: 1002, monId: "PES", monCotiz: 1m, total: 150_000m);

        var draft = await h.Service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Cliente arrepentido, anula el viaje completo"),
            "vendedor-1", "Vendedor Uno", CancellationToken.None);

        await h.Service.ConfirmAsync(
            draft.PublicId, NewConfirmRequest(), "vendedor-1", "Vendedor Uno",
            requesterIsAdmin: false, ct: CancellationToken.None);

        // Dos hijas Pending, una por factura, cada una en su moneda ARCA.
        var children = await h.Ctx.BookingCancellationCreditNotes.AsNoTracking()
            .Where(c => c.OriginatingInvoiceId == usd.Id || c.OriginatingInvoiceId == ars.Id)
            .ToListAsync();
        Assert.Equal(2, children.Count);
        Assert.All(children, c => Assert.Equal(BookingCancellationCreditNoteStatus.Pending, c.Status));
        Assert.Contains(children, c => c.OriginatingInvoiceId == usd.Id && c.ArcaCurrency == "DOL");
        Assert.Contains(children, c => c.OriginatingInvoiceId == ars.Id && c.ArcaCurrency == "PES");

        // Una anulacion encolada por factura.
        h.InvoiceMock.Verify(s => s.EnqueueAnnulmentAsync(
            usd.Id, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<int?>()), Times.Once);
        h.InvoiceMock.Verify(s => s.EnqueueAnnulmentAsync(
            ars.Id, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<int?>()), Times.Once);
    }

    // ============================================================
    // Callbacks: ambas OK -> AwaitingOperatorRefund + puntero principal
    // ============================================================

    [Fact]
    public async Task Callbacks_AmbasNcOk_TransicionaCompletoConPunteroPrincipal()
    {
        var h = BuildService();
        var (reserva, _, _) = await SeedReservaAsync(h);
        var usd = await AddSaleInvoiceAsync(h, reserva.Id, 6, 2001, "DOL", 1000m, 200m);
        var ars = await AddSaleInvoiceAsync(h, reserva.Id, 6, 2002, "PES", 1m, 150_000m); // mas reciente -> principal

        var draft = await h.Service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Anulacion completa multimoneda"),
            "vendedor-1", "Vendedor Uno", CancellationToken.None);
        await h.Service.ConfirmAsync(draft.PublicId, NewConfirmRequest(), "vendedor-1", "Vendedor Uno", false, CancellationToken.None);

        var ncUsd = await AddCreditNoteAsync(h, reserva.Id, usd.Id, 8, 3001);
        var ncArs = await AddCreditNoteAsync(h, reserva.Id, ars.Id, 8, 3002);

        // Primer callback: sigue AwaitingFiscalConfirmation (falta una NC).
        await h.Service.OnArcaSucceededAsync(usd.Id, ncUsd.Id, CancellationToken.None);
        var afterFirst = await h.Ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == draft.PublicId);
        Assert.Equal(BookingCancellationStatus.AwaitingFiscalConfirmation, afterFirst.Status);
        Assert.Null(afterFirst.CreditNoteInvoiceId);

        // Segundo callback: completa -> AwaitingOperatorRefund + puntero principal = NC de la factura principal (ARS).
        await h.Service.OnArcaSucceededAsync(ars.Id, ncArs.Id, CancellationToken.None);
        var afterSecond = await h.Ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == draft.PublicId);
        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, afterSecond.Status);
        Assert.Equal(ncArs.Id, afterSecond.CreditNoteInvoiceId);

        var reloadedReserva = await h.Ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.PendingOperatorRefund, reloadedReserva.Status);
    }

    // ============================================================
    // Callbacks: una falla -> ArcaRejected + principal null (ND no dispara)
    // ============================================================

    [Fact]
    public async Task Callbacks_UnaNcFalla_QuedaArcaRejectedConPunteroNull()
    {
        var h = BuildService();
        var (reserva, _, _) = await SeedReservaAsync(h);
        var usd = await AddSaleInvoiceAsync(h, reserva.Id, 6, 4001, "DOL", 1000m, 200m);
        var ars = await AddSaleInvoiceAsync(h, reserva.Id, 6, 4002, "PES", 1m, 150_000m);

        var draft = await h.Service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Anulacion con falla parcial de AFIP"),
            "vendedor-1", "Vendedor Uno", CancellationToken.None);
        await h.Service.ConfirmAsync(draft.PublicId, NewConfirmRequest(), "vendedor-1", "Vendedor Uno", false, CancellationToken.None);

        var ncUsd = await AddCreditNoteAsync(h, reserva.Id, usd.Id, 8, 5001);

        await h.Service.OnArcaSucceededAsync(usd.Id, ncUsd.Id, CancellationToken.None);
        await h.Service.OnArcaFailedAsync(ars.Id, "CUIT del emisor sin habilitacion", CancellationToken.None);

        var bc = await h.Ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == draft.PublicId);
        Assert.Equal(BookingCancellationStatus.ArcaRejected, bc.Status);
        // Puntero principal NULL -> es lo que FRENA la ND (su gate exige CreditNoteInvoiceId != null).
        Assert.Null(bc.CreditNoteInvoiceId);

        var children = await h.Ctx.BookingCancellationCreditNotes.AsNoTracking()
            .Where(c => c.BookingCancellationId == bc.Id).ToListAsync();
        Assert.Equal(BookingCancellationCreditNoteStatus.Succeeded, children.First(c => c.OriginatingInvoiceId == usd.Id).Status);
        var failedChild = children.First(c => c.OriginatingInvoiceId == ars.Id);
        Assert.Equal(BookingCancellationCreditNoteStatus.Failed, failedChild.Status);
        Assert.Contains("CUIT", failedChild.ArcaErrorMessage);
    }

    // ============================================================
    // Retry: reemite SOLO la faltante y cierra
    // ============================================================

    [Fact]
    public async Task Retry_ReemiteSoloLaFaltante_YLuegoCierra()
    {
        var h = BuildService();
        var (reserva, _, _) = await SeedReservaAsync(h);
        var usd = await AddSaleInvoiceAsync(h, reserva.Id, 6, 6001, "DOL", 1000m, 200m);
        var ars = await AddSaleInvoiceAsync(h, reserva.Id, 6, 6002, "PES", 1m, 150_000m);

        var draft = await h.Service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Anulacion, reintento de la NC faltante"),
            "vendedor-1", "Vendedor Uno", CancellationToken.None);
        await h.Service.ConfirmAsync(draft.PublicId, NewConfirmRequest(), "vendedor-1", "Vendedor Uno", false, CancellationToken.None);

        var ncUsd = await AddCreditNoteAsync(h, reserva.Id, usd.Id, 8, 7001);
        await h.Service.OnArcaSucceededAsync(usd.Id, ncUsd.Id, CancellationToken.None);
        await h.Service.OnArcaFailedAsync(ars.Id, "Rechazo temporal", CancellationToken.None);

        h.InvoiceMock.Invocations.Clear(); // contamos solo lo del retry

        await h.Service.RetryCreditNotesAsync(draft.PublicId, "vendedor-1", "Vendedor Uno", CancellationToken.None);

        // Reintenta SOLO la ARS (la que fallo) via el metodo de retry (F1). La USD ya salio: NO se re-encola.
        h.InvoiceMock.Verify(s => s.EnqueueAnnulmentRetryAsync(
            ars.Id, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
        h.InvoiceMock.Verify(s => s.EnqueueAnnulmentRetryAsync(
            usd.Id, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);

        // El BC se reabrio a AwaitingFiscalConfirmation (la ARS quedo Pending de nuevo).
        var reopened = await h.Ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == draft.PublicId);
        Assert.Equal(BookingCancellationStatus.AwaitingFiscalConfirmation, reopened.Status);

        // Ahora la ARS sale bien -> cierra completo.
        var ncArs = await AddCreditNoteAsync(h, reserva.Id, ars.Id, 8, 7002);
        await h.Service.OnArcaSucceededAsync(ars.Id, ncArs.Id, CancellationToken.None);

        var closed = await h.Ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == draft.PublicId);
        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, closed.Status);
        Assert.Equal(ncArs.Id, closed.CreditNoteInvoiceId); // ARS es la principal
    }

    [Fact]
    public async Task Retry_SegundoConcurrente_VeHijaPending_NoReEncola()
    {
        // Simula el efecto del lock: tras el primer retry la hija ya esta Pending; un segundo retry inmediato
        // NO encuentra hijas Failed que re-emitir -> no encola de nuevo (no-op efectivo). En Postgres esto lo
        // garantiza el FOR UPDATE; en InMemory validamos la logica de "no re-encolar una Pending".
        var h = BuildService();
        var (reserva, _, _) = await SeedReservaAsync(h);
        var usd = await AddSaleInvoiceAsync(h, reserva.Id, 6, 8001, "DOL", 1000m, 200m);
        var ars = await AddSaleInvoiceAsync(h, reserva.Id, 6, 8002, "PES", 1m, 150_000m);

        var draft = await h.Service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Anulacion, doble retry"),
            "vendedor-1", "Vendedor Uno", CancellationToken.None);
        await h.Service.ConfirmAsync(draft.PublicId, NewConfirmRequest(), "vendedor-1", "Vendedor Uno", false, CancellationToken.None);
        var ncUsd = await AddCreditNoteAsync(h, reserva.Id, usd.Id, 8, 9001);
        await h.Service.OnArcaSucceededAsync(usd.Id, ncUsd.Id, CancellationToken.None);
        await h.Service.OnArcaFailedAsync(ars.Id, "Rechazo", CancellationToken.None);

        await h.Service.RetryCreditNotesAsync(draft.PublicId, "vendedor-1", "Vendedor Uno", CancellationToken.None);
        h.InvoiceMock.Invocations.Clear();

        // Segundo retry: la ARS ya esta Pending (AwaitingFiscalConfirmation), no hay Failed -> no re-encola.
        await h.Service.RetryCreditNotesAsync(draft.PublicId, "vendedor-1", "Vendedor Uno", CancellationToken.None);
        h.InvoiceMock.Verify(s => s.EnqueueAnnulmentRetryAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ============================================================
    // Guard INV-081: nunca liberar un BC con NC viva
    // ============================================================

    [Fact]
    public async Task Guard_ArcaRejected_ConHijaViva_NoLibera_TiraInv081()
    {
        var h = BuildService();
        var (reserva, customer, supplier) = await SeedReservaAsync(h);
        var sale = await AddSaleInvoiceAsync(h, reserva.Id, 6, 10001, "PES", 1m, 100_000m);
        var nc = await AddCreditNoteAsync(h, reserva.Id, sale.Id, 8, 10002);

        // BC ArcaRejected con puntero principal NULL pero con una hija Succeeded (NC viva).
        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = customer.Id, SupplierId = supplier.Id,
            OriginatingInvoiceId = sale.Id, CreditNoteInvoiceId = null,
            Status = BookingCancellationStatus.ArcaRejected,
            Reason = "Anulacion parcial con NC viva", DraftedByUserId = "vendedor-1",
        };
        h.Ctx.BookingCancellations.Add(bc);
        await h.Ctx.SaveChangesAsync();
        h.Ctx.BookingCancellationCreditNotes.Add(new BookingCancellationCreditNote
        {
            BookingCancellationId = bc.Id, OriginatingInvoiceId = sale.Id, CreditNoteInvoiceId = nc.Id,
            ArcaCurrency = "PES", Status = BookingCancellationCreditNoteStatus.Succeeded,
        });
        await h.Ctx.SaveChangesAsync();

        // Intentar re-cancelar la reserva: NO se libera el BC con NC viva -> INV-081.
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.DraftAsync(
                new DraftCancellationRequest(reserva.PublicId, "Intento re-cancelar sobre NC viva"),
                "vendedor-1", "Vendedor Uno", CancellationToken.None));
        Assert.Equal("INV-081", ex.InvariantCode);
    }

    [Fact]
    public async Task Guard_ArcaRejected_CeroHijas_ConPunteroPrincipal_NoLibera_TiraInv081()
    {
        // Caso legacy (sin hijas) con CreditNoteInvoiceId != null: hay NC viva via el puntero singular -> NO liberar.
        var h = BuildService();
        var (reserva, customer, supplier) = await SeedReservaAsync(h);
        var sale = await AddSaleInvoiceAsync(h, reserva.Id, 6, 11001, "PES", 1m, 100_000m);
        var nc = await AddCreditNoteAsync(h, reserva.Id, sale.Id, 8, 11002);

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = customer.Id, SupplierId = supplier.Id,
            OriginatingInvoiceId = sale.Id, CreditNoteInvoiceId = nc.Id,
            Status = BookingCancellationStatus.ArcaRejected,
            Reason = "Legacy con NC viva via puntero", DraftedByUserId = "vendedor-1",
        };
        h.Ctx.BookingCancellations.Add(bc);
        await h.Ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.DraftAsync(
                new DraftCancellationRequest(reserva.PublicId, "Intento re-cancelar legacy con NC viva"),
                "vendedor-1", "Vendedor Uno", CancellationToken.None));
        Assert.Equal("INV-081", ex.InvariantCode);
    }

    // ============================================================
    // Pre-flight TC=1 -> no se emite NINGUNA NC (todo-o-nada al frente)
    // ============================================================

    [Fact]
    public async Task Preflight_FacturaExtranjeraTc1_NoEmiteNada_TiraInv156()
    {
        var h = BuildService();
        var (reserva, _, _) = await SeedReservaAsync(h);
        // Factura USD con cotizacion sospechosa (==1): pre-flight la rechaza.
        await AddSaleInvoiceAsync(h, reserva.Id, 6, 12001, "DOL", 1m, 200m);

        var draft = await h.Service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Anulacion con factura USD sin cotizacion"),
            "vendedor-1", "Vendedor Uno", CancellationToken.None);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ConfirmAsync(draft.PublicId, NewConfirmRequest(), "vendedor-1", "Vendedor Uno", false, CancellationToken.None));
        Assert.Equal("INV-156", ex.InvariantCode);

        // NO se creo ninguna hija ni se encolo nada.
        Assert.Empty(await h.Ctx.BookingCancellationCreditNotes.AsNoTracking().ToListAsync());
        h.InvoiceMock.Verify(s => s.EnqueueAnnulmentAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<int?>()), Times.Never);
    }

    // ============================================================
    // No-regresion: mono-factura byte-equivalente (1 hija, 1 enqueue, cierra en el unico callback)
    // ============================================================

    [Fact]
    public async Task MonoFactura_UnaHija_UnEnqueue_CierraEnElUnicoCallback()
    {
        var h = BuildService();
        var (reserva, _, _) = await SeedReservaAsync(h);
        var sale = await AddSaleInvoiceAsync(h, reserva.Id, 6, 13001, "PES", 1m, 100_000m);

        var draft = await h.Service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Anulacion mono-factura clasica"),
            "vendedor-1", "Vendedor Uno", CancellationToken.None);
        await h.Service.ConfirmAsync(draft.PublicId, NewConfirmRequest(), "vendedor-1", "Vendedor Uno", false, CancellationToken.None);

        var children = await h.Ctx.BookingCancellationCreditNotes.AsNoTracking()
            .Where(c => c.OriginatingInvoiceId == sale.Id).ToListAsync();
        Assert.Single(children);
        h.InvoiceMock.Verify(s => s.EnqueueAnnulmentAsync(
            sale.Id, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<int?>()), Times.Once);

        var nc = await AddCreditNoteAsync(h, reserva.Id, sale.Id, 8, 13002);
        await h.Service.OnArcaSucceededAsync(sale.Id, nc.Id, CancellationToken.None);

        var bc = await h.Ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == draft.PublicId);
        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, bc.Status);
        Assert.Equal(nc.Id, bc.CreditNoteInvoiceId);
    }

    // ============================================================
    // N10: approval del InvoiceAnnulment resuelto UNA vez PRE-COMMIT (multi-factura)
    // ============================================================

    [Fact]
    public async Task N10_MultiFactura_ApprovalRequeridoSinAprobacion_TiraPreCommit_NoTocaNada()
    {
        // RequireApprovalForInvoiceAnnulment ON + vendedor no-admin + sin approval: la resolucion PRE-COMMIT
        // (N10) tira ApprovalRequiredException ANTES de tocar nada. Nunca una excepcion de approval a mitad
        // del loop post-commit: el BC queda Drafted, sin hijas y sin encolar.
        var h = BuildService(requireApprovalForInvoiceAnnulment: true);
        var (reserva, _, _) = await SeedReservaAsync(h);
        await AddSaleInvoiceAsync(h, reserva.Id, 6, 14001, "DOL", 1000m, 200m);
        await AddSaleInvoiceAsync(h, reserva.Id, 6, 14002, "PES", 1m, 150_000m);
        // approvalMock.FindActiveApprovedAsync devuelve null por defecto -> no hay autorizacion.

        var draft = await h.Service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Multi-factura sin approval de anulacion"),
            "vendedor-1", "Vendedor Uno", CancellationToken.None);

        await Assert.ThrowsAsync<ApprovalRequiredException>(() =>
            h.Service.ConfirmAsync(draft.PublicId, NewConfirmRequest(), "vendedor-1", "Vendedor Uno", false, CancellationToken.None));

        // Pre-commit: el BC sigue Drafted, no se crearon hijas ni se encolo nada.
        var bc = await h.Ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == draft.PublicId);
        Assert.Equal(BookingCancellationStatus.Drafted, bc.Status);
        Assert.Empty(await h.Ctx.BookingCancellationCreditNotes.AsNoTracking().ToListAsync());
        h.InvoiceMock.Verify(s => s.EnqueueAnnulmentAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task N10_MultiFactura_ConAprobacionDeReserva_EncolaTodasBypasseando()
    {
        // RequireApprovalForInvoiceAnnulment ON + una autorizacion valida (que cubre la anulacion de la
        // reserva): el confirm procede y encola TODAS las facturas con requesterIsAdmin: true (una sola
        // autorizacion cubre las N facturas; no se pide un approval por comprobante).
        var h = BuildService(requireApprovalForInvoiceAnnulment: true);
        h.ApprovalMock
            .Setup(s => s.FindActiveApprovedAsync(
                ApprovalRequestType.InvoiceAnnulment, "Invoice", It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApprovalRequest { Id = 777 });

        var (reserva, _, _) = await SeedReservaAsync(h);
        var usd = await AddSaleInvoiceAsync(h, reserva.Id, 6, 15001, "DOL", 1000m, 200m);
        var ars = await AddSaleInvoiceAsync(h, reserva.Id, 6, 15002, "PES", 1m, 150_000m);

        var draft = await h.Service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Multi-factura con approval de anulacion"),
            "vendedor-1", "Vendedor Uno", CancellationToken.None);
        await h.Service.ConfirmAsync(draft.PublicId, NewConfirmRequest(), "vendedor-1", "Vendedor Uno", false, CancellationToken.None);

        // Ambas facturas encoladas con requesterIsAdmin: true (bypass del re-check por-factura) y el id del
        // approval como cross-reference.
        h.InvoiceMock.Verify(s => s.EnqueueAnnulmentAsync(
            usd.Id, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            true, It.IsAny<CancellationToken>(), 777), Times.Once);
        h.InvoiceMock.Verify(s => s.EnqueueAnnulmentAsync(
            ars.Id, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            true, It.IsAny<CancellationToken>(), 777), Times.Once);
    }

    [Fact]
    public async Task N10_MultiFactura_PolicyDiceNoRequiere_AunConSettingLegacyTrue_NoExigeApproval_Encola()
    {
        // BUG de Gaston (2026-07-02): el setting legacy RequireApprovalForInvoiceAnnulment quedo en TRUE, PERO la
        // ApprovalPolicy configurable (la que usa InvoiceService) dice que NO requiere approval — por eso el
        // mono-factura siempre le funciono. El pre-check N10 debe resolver via la POLICY (equivalencia mono<->multi),
        // NO el setting crudo -> NO tira ApprovalRequiredException y encola las N facturas.
        var policyMock = new Mock<IApprovalPolicyService>();
        policyMock
            .Setup(p => p.RequiresApprovalAsync(
                ApprovalRequestType.InvoiceAnnulment, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var h = BuildService(requireApprovalForInvoiceAnnulment: true, approvalPolicy: policyMock.Object);
        var (reserva, _, _) = await SeedReservaAsync(h);
        var usd = await AddSaleInvoiceAsync(h, reserva.Id, 6, 21001, "DOL", 1000m, 200m);
        var ars = await AddSaleInvoiceAsync(h, reserva.Id, 6, 21002, "PES", 1m, 150_000m);

        var draft = await h.Service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Multi-factura, la policy no requiere approval"),
            "admin-1", "Admin Uno", CancellationToken.None);

        // NO debe tirar (antes tiraba ApprovalRequiredException por leer el setting legacy crudo).
        await h.Service.ConfirmAsync(draft.PublicId, NewConfirmRequest(), "admin-1", "Admin Uno", false, CancellationToken.None);

        // Se crearon las 2 hijas y se encolo una anulacion por factura.
        var children = await h.Ctx.BookingCancellationCreditNotes.AsNoTracking()
            .Where(c => c.OriginatingInvoiceId == usd.Id || c.OriginatingInvoiceId == ars.Id).ToListAsync();
        Assert.Equal(2, children.Count);
        h.InvoiceMock.Verify(s => s.EnqueueAnnulmentAsync(
            usd.Id, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<int?>()), Times.Once);
        h.InvoiceMock.Verify(s => s.EnqueueAnnulmentAsync(
            ars.Id, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<int?>()), Times.Once);
        // Y NO se busco ningun approval (la policy ya dijo que no hace falta).
        h.ApprovalMock.Verify(a => a.FindActiveApprovedAsync(
            It.IsAny<ApprovalRequestType>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ============================================================
    // S1: el retry deja rastro auditable del actor humano
    // ============================================================

    [Fact]
    public async Task Retry_EscribeAuditLogDelActor()
    {
        var h = BuildService();
        var (reserva, _, _) = await SeedReservaAsync(h);
        var usd = await AddSaleInvoiceAsync(h, reserva.Id, 6, 16001, "DOL", 1000m, 200m);
        var ars = await AddSaleInvoiceAsync(h, reserva.Id, 6, 16002, "PES", 1m, 150_000m);

        var draft = await h.Service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Anulacion, auditoria del retry"),
            "vendedor-1", "Vendedor Uno", CancellationToken.None);
        await h.Service.ConfirmAsync(draft.PublicId, NewConfirmRequest(), "vendedor-1", "Vendedor Uno", false, CancellationToken.None);
        var ncUsd = await AddCreditNoteAsync(h, reserva.Id, usd.Id, 8, 16003);
        await h.Service.OnArcaSucceededAsync(usd.Id, ncUsd.Id, CancellationToken.None);
        await h.Service.OnArcaFailedAsync(ars.Id, "Rechazo", CancellationToken.None);

        await h.Service.RetryCreditNotesAsync(draft.PublicId, "cajero-9", "Cajero Nueve", CancellationToken.None);

        // Rastro fiscal del actor humano que disparo el retry.
        h.AuditMock.Verify(a => a.LogBusinessEventAsync(
            TravelApi.Application.Constants.AuditActions.BookingCancellationCreditNotesRetried,
            TravelApi.Application.Constants.AuditActions.BookingCancellationEntityName,
            It.IsAny<string>(), It.IsAny<string>(), "cajero-9", "Cajero Nueve", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ============================================================
    // S4: el retry NO re-exige approval (el del confirm cubre el retry) -> requesterIsAdmin: true
    // ============================================================

    [Fact]
    public async Task Retry_ConApprovalDeAnulacionON_CompletaSinReExigirApproval()
    {
        // Aunque RequireApprovalForInvoiceAnnulment este ON, el retry re-encola con requesterIsAdmin: true:
        // la anulacion YA fue autorizada al confirmar; reintentar es COMPLETAR esa accion, no una nueva. Ambos
        // reviewers lo juzgaron correcto. El re-encolado no vuelve a pedir approval.
        var h = BuildService(requireApprovalForInvoiceAnnulment: true);
        h.ApprovalMock
            .Setup(s => s.FindActiveApprovedAsync(
                ApprovalRequestType.InvoiceAnnulment, "Invoice", It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApprovalRequest { Id = 888 });

        var (reserva, _, _) = await SeedReservaAsync(h);
        var usd = await AddSaleInvoiceAsync(h, reserva.Id, 6, 17001, "DOL", 1000m, 200m);
        var ars = await AddSaleInvoiceAsync(h, reserva.Id, 6, 17002, "PES", 1m, 150_000m);

        var draft = await h.Service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Anulacion con approval, retry sin re-exigir"),
            "vendedor-1", "Vendedor Uno", CancellationToken.None);
        await h.Service.ConfirmAsync(draft.PublicId, NewConfirmRequest(), "vendedor-1", "Vendedor Uno", false, CancellationToken.None);
        var ncUsd = await AddCreditNoteAsync(h, reserva.Id, usd.Id, 8, 17003);
        await h.Service.OnArcaSucceededAsync(usd.Id, ncUsd.Id, CancellationToken.None);
        await h.Service.OnArcaFailedAsync(ars.Id, "Rechazo temporal", CancellationToken.None);

        h.InvoiceMock.Invocations.Clear();
        await h.Service.RetryCreditNotesAsync(draft.PublicId, "vendedor-1", "Vendedor Uno", CancellationToken.None);

        // Re-encola la faltante via EnqueueAnnulmentRetryAsync (F1): no re-exige approval (la anulacion ya se
        // autorizo al confirmar) y no re-aplica el guard "Pending" (la señal la puso el service bajo el lock).
        h.InvoiceMock.Verify(s => s.EnqueueAnnulmentRetryAsync(
            ars.Id, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ============================================================
    // B2 (unit): Force de UNA sola NC en multi-factura NO cierra la anulacion
    // ============================================================

    [Fact]
    public async Task Force_UnaSolaNcEnMultiFactura_NoCierra_QuedaAwaitingFiscalConfirmation()
    {
        // Con B2, Force opera por-hija bajo lock y reevalua con conteo fresco: forzar UNA NC de dos deja el BC
        // en AwaitingFiscalConfirmation (todo-o-nada), NO lo cierra ni transiciona la reserva ni dispara la ND.
        var h = BuildService();
        var (reserva, _, _) = await SeedReservaAsync(h);
        var usd = await AddSaleInvoiceAsync(h, reserva.Id, 6, 18001, "DOL", 1000m, 200m);
        var ars = await AddSaleInvoiceAsync(h, reserva.Id, 6, 18002, "PES", 1m, 150_000m);

        var draft = await h.Service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Anulacion, force parcial de una NC"),
            "admin-1", "Admin Uno", CancellationToken.None);
        await h.Service.ConfirmAsync(draft.PublicId, NewConfirmRequest(), "admin-1", "Admin Uno", false, CancellationToken.None);

        var bcId = (await h.Ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == draft.PublicId)).Id;

        // NC de la factura USD emitida fuera de banda (Resultado=A + CAE).
        var ncUsd = await AddCreditNoteAsync(h, reserva.Id, usd.Id, 8, 18003);

        // Approval InvariantOverride aprobado, scoped a este BC.
        var approval = new ApprovalRequest
        {
            RequestType = ApprovalRequestType.InvariantOverride,
            EntityType = "BookingCancellation",
            EntityId = bcId,
            RequestedByUserId = "admin-1",
            RequestedAt = DateTime.UtcNow,
            Status = ApprovalStatus.Approved,
            ResolvedByUserId = "admin-1",
            ResolvedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            Reason = "Force override para BC multi-factura",
        };
        h.Ctx.ApprovalRequests.Add(approval);
        await h.Ctx.SaveChangesAsync();

        var forceRequest = new ForceArcaConfirmationRequest(
            CreditNoteInvoicePublicId: ncUsd.PublicId,
            ApprovalRequestPublicId: approval.PublicId,
            Reason: "Forzar la NC en USD que salio fuera de banda, minimo veinte caracteres");

        await h.Service.ForceArcaConfirmationAsync(draft.PublicId, forceRequest, "admin-1", "Admin Uno", CancellationToken.None);

        // El BC NO se cerro: sigue AwaitingFiscalConfirmation (falta la NC en ARS). Reserva sin transicionar a
        // PendingOperatorRefund por el cierre (se hizo en el confirm, pero el BC no avanza a AwaitingOperatorRefund).
        var bc = await h.Ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bcId);
        Assert.Equal(BookingCancellationStatus.AwaitingFiscalConfirmation, bc.Status);
        Assert.Null(bc.CreditNoteInvoiceId); // puntero principal NO seteado -> la ND no dispara

        var children = await h.Ctx.BookingCancellationCreditNotes.AsNoTracking()
            .Where(c => c.BookingCancellationId == bcId).ToListAsync();
        Assert.Equal(BookingCancellationCreditNoteStatus.Succeeded, children.First(c => c.OriginatingInvoiceId == usd.Id).Status);
        Assert.Equal(BookingCancellationCreditNoteStatus.Pending, children.First(c => c.OriginatingInvoiceId == ars.Id).Status);
    }

    // ============================================================
    // F2: reuso de draft "puro" con motivo EDITADO -> se actualiza el Reason
    // ============================================================

    [Fact]
    public async Task Draft_ReusePureDraft_ConMotivoEditado_ActualizaElReason()
    {
        // El vendedor crea el draft, toca "Volver", edita el motivo y vuelve a "Anular". DraftAsync reusa la
        // MISMA fila Drafted, pero ahora ACTUALIZA el motivo (antes quedaba auditado con el motivo viejo).
        var h = BuildService();
        var (reserva, _, _) = await SeedReservaAsync(h);
        await AddSaleInvoiceAsync(h, reserva.Id, 6, 20001, "PES", 1m, 100_000m);

        var first = await h.Service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Motivo original de la anulacion"),
            "vendedor-1", "Vendedor Uno", CancellationToken.None);

        var second = await h.Service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Motivo EDITADO por el vendedor tras Volver"),
            "vendedor-1", "Vendedor Uno", CancellationToken.None);

        // Es la MISMA cancelacion (reuso idempotente), pero con el motivo nuevo.
        Assert.Equal(first.PublicId, second.PublicId);
        var bc = await h.Ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == first.PublicId);
        Assert.Equal("Motivo EDITADO por el vendedor tras Volver", bc.Reason);
    }
}
