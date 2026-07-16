using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// 2026-06-24 — tests UNIT de los tres arreglos del flujo de ANULACION TOTAL de la reserva
/// (<see cref="BookingCancellationService.ConfirmAsync"/>):
///
/// <list type="number">
/// <item><b>ARREGLO 1</b>: al anular la reserva entera se recalcula EN EL MISMO request la deuda del/los
///   operador(es) y la plata del cliente + comision (antes quedaba inflada/colgada hasta el job de AFIP).</item>
/// <item><b>ARREGLO 2</b>: con MAS DE UN operador con multa confirmada, la ND automatica NO se emite y se
///   deriva a revision manual (la emision por linea de operador no existe todavia).</item>
/// <item><b>ARREGLO 3</b>: la confirmacion deja estado + servicios + recalculos consistentes (todos juntos).</item>
/// </list>
///
/// <para><b>Trade-off (igual que el resto de los tests del modulo)</b>: EF InMemory NO valida CHECK SQL ni
/// xmin ni transacciones; la atomicidad REAL se valida en integracion Postgres. Aca cubrimos la LOGICA:
/// que los persisters corran, que el estado/servicios queden coherentes y que el gating multi-operador
/// rutee a manual.</para>
/// </summary>
public class CancellationTotalAnnulmentRecalcTests
{
    private static AppDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"total-annulment-recalc-{Guid.NewGuid()}")
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

    private static Harness BuildService(
        bool enableCommissions = false,
        decimal commissionPercent = 0m,
        bool enableDebitNote = false)
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
            EnablePartialCreditNotes = false,
            EnableCancellationDebitNote = enableDebitNote,
            EnableSellerCommissions = enableCommissions,
            SellerCommissionPercent = commissionPercent,
            OperatorRefundTimeoutDays = 60,
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

    /// <summary>Request sano de confirmacion (snapshot fiscal valido, sin override, sin clasificacion de penalidad).</summary>
    private static ConfirmCancellationRequest NewConfirmRequest() =>
        new ConfirmCancellationRequest(
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

    // ============================================================
    // ARREGLO 1 — recalculo de deuda + comision al anular total
    // ============================================================

    [Fact]
    public async Task ConfirmAsync_SingleSupplier_DropsSupplierDebtAndZeroesCommission()
    {
        // Reserva con UN operador: un hotel confirmado (genera deuda con el operador) + comision devengada.
        var h = BuildService(enableCommissions: true, commissionPercent: 10m);

        // CommissionAccrualPersister lee las settings de la TABLA (no del mock IOperationalFinanceSettingsService),
        // asi que sembramos la fila real con la comision habilitada para que el tope-cero se ejecute.
        h.Ctx.OperationalFinanceSettings.Add(new OperationalFinanceSettings
        {
            EnableSellerCommissions = true,
            SellerCommissionPercent = 10m,
        });
        await h.Ctx.SaveChangesAsync();

        // Tanda B (2026-07-16): ConfirmAsync resuelve las 3 condiciones fiscales SERVER-SIDE
        // (ResolveServerSideTaxIdentity), no del request.SnapshotData de NewConfirmRequest() (ese
        // campo ahora se ignora). Sin esta fila de AfipSettings, ConfirmAsync rebotaria con INV-118.
        h.Ctx.AfipSettings.Add(new AfipSettings { Cuit = 20111111112, TaxCondition = "Monotributo" });

        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        var supplier = new Supplier { Name = "Operador Unico", IsActive = true, TaxCondition = "IVA_RESP_INSCRIPTO" };
        h.Ctx.Customers.Add(customer);
        h.Ctx.Suppliers.Add(supplier);
        await h.Ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-A1",
            Name = "Reserva un operador",
            PayerId = customer.Id,
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "vendedor-1",
            ResponsibleUserName = "Vendedor Uno",
            Balance = 0m,
        };
        h.Ctx.Reservas.Add(reserva);
        await h.Ctx.SaveChangesAsync();

        h.Ctx.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            Status = "Confirmado",
            NetCost = 50_000m,
            SalePrice = 80_000m,
            Commission = 30_000m,
            Currency = "ARS",
        });
        await h.Ctx.SaveChangesAsync();

        // Estado PREVIO (simulado): el operador tenia deuda viva y el vendedor una comision devengada.
        supplier.CurrentBalance = 50_000m;
        h.Ctx.CommissionAccruals.Add(new CommissionAccrual
        {
            ReservaId = reserva.Id,
            SellerUserId = "vendedor-1",
            SellerName = "Vendedor Uno",
            Currency = "ARS",
            Amount = 3_000m,
            RatePercent = 10m,
            Status = CommissionAccrualStatus.Devengada,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await h.Ctx.SaveChangesAsync();

        var invoice = new Invoice
        {
            TipoComprobante = 11, // C
            PuntoDeVenta = 1,
            NumeroComprobante = 100,
            CAE = "12345678",
            Resultado = "A",
            MonId = "PES",
            ImporteTotal = 80_000m,
            ImporteNeto = 80_000m,
            ImporteIva = 0m,
            ReservaId = reserva.Id,
            AnnulmentStatus = AnnulmentStatus.None,
        };
        h.Ctx.Invoices.Add(invoice);
        await h.Ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id,
            Status = BookingCancellationStatus.Drafted,
            Reason = "Cliente decidio anular el viaje completo",
            DraftedByUserId = "vendedor-1",
        };
        h.Ctx.BookingCancellations.Add(bc);
        await h.Ctx.SaveChangesAsync();

        await h.Service.ConfirmAsync(
            bc.PublicId, NewConfirmRequest(), "vendedor-1", "Vendedor Uno",
            requesterIsAdmin: false, ct: default);

        // Deuda del operador: la reserva quedo en PendingOperatorRefund (no es "viva" para la cuenta del
        // operador) y el hotel quedo cancelado -> la deuda baja a 0 (antes quedaba inflada en 50.000).
        var reloadedSupplier = await h.Ctx.Suppliers.AsNoTracking().FirstAsync(s => s.Id == supplier.Id);
        Assert.Equal(0m, reloadedSupplier.CurrentBalance);

        // Comision del vendedor: tope cero al anular -> 0 (antes quedaba colgada en 3.000).
        var reloadedAccrual = await h.Ctx.CommissionAccruals.AsNoTracking().FirstAsync(c => c.ReservaId == reserva.Id);
        Assert.Equal(0m, reloadedAccrual.Amount);

        // El servicio quedo Cancelado y la reserva en PendingOperatorRefund (estado coherente).
        var reloadedHotel = await h.Ctx.HotelBookings.AsNoTracking().FirstAsync(hb => hb.ReservaId == reserva.Id);
        Assert.Equal("Cancelado", reloadedHotel.Status);
        var reloadedReserva = await h.Ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.PendingOperatorRefund, reloadedReserva.Status);
    }

    [Fact]
    public async Task ConfirmAsync_TwoSuppliers_DropsDebtForBoth()
    {
        // Reserva con DOS operadores (hotel A + traslado B), ambos con deuda viva previa.
        var h = BuildService();

        // Tanda B (2026-07-16): ver comentario identico en ConfirmAsync_SingleSupplier_... arriba.
        h.Ctx.AfipSettings.Add(new AfipSettings { Cuit = 20111111112, TaxCondition = "Monotributo" });

        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        var supplierA = new Supplier { Name = "Operador A", IsActive = true, TaxCondition = "IVA_RESP_INSCRIPTO" };
        var supplierB = new Supplier { Name = "Operador B", IsActive = true, TaxCondition = "IVA_RESP_INSCRIPTO" };
        h.Ctx.Customers.Add(customer);
        h.Ctx.Suppliers.Add(supplierA);
        h.Ctx.Suppliers.Add(supplierB);
        await h.Ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-A1b",
            Name = "Reserva dos operadores",
            PayerId = customer.Id,
            Status = EstadoReserva.Confirmed,
            Balance = 0m,
        };
        h.Ctx.Reservas.Add(reserva);
        await h.Ctx.SaveChangesAsync();

        h.Ctx.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplierA.Id, Status = "Confirmado",
            NetCost = 40_000m, SalePrice = 60_000m, Currency = "ARS",
        });
        h.Ctx.TransferBookings.Add(new TransferBooking
        {
            ReservaId = reserva.Id, SupplierId = supplierB.Id, Status = "Confirmado",
            NetCost = 20_000m, SalePrice = 30_000m, Currency = "ARS",
        });
        await h.Ctx.SaveChangesAsync();

        supplierA.CurrentBalance = 40_000m;
        supplierB.CurrentBalance = 20_000m;
        await h.Ctx.SaveChangesAsync();

        var invoice = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 101, CAE = "999",
            Resultado = "A", MonId = "PES", ImporteTotal = 90_000m, ImporteNeto = 90_000m,
            ImporteIva = 0m, ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None,
        };
        h.Ctx.Invoices.Add(invoice);
        await h.Ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = customer.Id, SupplierId = supplierA.Id,
            OriginatingInvoiceId = invoice.Id, Status = BookingCancellationStatus.Drafted,
            Reason = "Anulacion total de reserva multi-operador", DraftedByUserId = "vendedor-1",
        };
        h.Ctx.BookingCancellations.Add(bc);
        await h.Ctx.SaveChangesAsync();

        await h.Service.ConfirmAsync(
            bc.PublicId, NewConfirmRequest(), "vendedor-1", "Vendedor Uno",
            requesterIsAdmin: false, ct: default);

        // AMBOS operadores quedan en 0: el recalculo recorre los SupplierId distintos de la reserva.
        var a = await h.Ctx.Suppliers.AsNoTracking().FirstAsync(s => s.Id == supplierA.Id);
        var b = await h.Ctx.Suppliers.AsNoTracking().FirstAsync(s => s.Id == supplierB.Id);
        Assert.Equal(0m, a.CurrentBalance);
        Assert.Equal(0m, b.CurrentBalance);
    }

    // ============================================================
    // ARREGLO 3 — confirmacion "todo o nada" (consistencia)
    // ============================================================

    [Fact]
    public async Task ConfirmAsync_LeavesStateServicesAndRecalcsConsistent()
    {
        var h = BuildService();

        // Tanda B (2026-07-16): ver comentario identico en ConfirmAsync_SingleSupplier_... arriba.
        h.Ctx.AfipSettings.Add(new AfipSettings { Cuit = 20111111112, TaxCondition = "Monotributo" });

        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        var supplier = new Supplier { Name = "Operador Unico", IsActive = true, TaxCondition = "IVA_RESP_INSCRIPTO" };
        h.Ctx.Customers.Add(customer);
        h.Ctx.Suppliers.Add(supplier);
        await h.Ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-A3", Name = "Reserva consistencia", PayerId = customer.Id,
            Status = EstadoReserva.Confirmed, Balance = 0m,
        };
        h.Ctx.Reservas.Add(reserva);
        await h.Ctx.SaveChangesAsync();

        // Dos servicios del mismo operador + un vuelo (cancela con codigo IATA "UN").
        h.Ctx.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
            NetCost = 50_000m, SalePrice = 80_000m, Currency = "ARS",
        });
        h.Ctx.FlightSegments.Add(new FlightSegment
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "HK",
            NetCost = 30_000m, SalePrice = 45_000m, Currency = "ARS",
        });
        await h.Ctx.SaveChangesAsync();

        var invoice = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 102, CAE = "abc",
            Resultado = "A", MonId = "PES", ImporteTotal = 125_000m, ImporteNeto = 125_000m,
            ImporteIva = 0m, ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None,
        };
        h.Ctx.Invoices.Add(invoice);
        await h.Ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = customer.Id, SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id, Status = BookingCancellationStatus.Drafted,
            Reason = "Anulacion total - verificar consistencia", DraftedByUserId = "vendedor-1",
        };
        h.Ctx.BookingCancellations.Add(bc);
        await h.Ctx.SaveChangesAsync();

        await h.Service.ConfirmAsync(
            bc.PublicId, NewConfirmRequest(), "vendedor-1", "Vendedor Uno",
            requesterIsAdmin: false, ct: default);

        // 1) Estado del BC y de la reserva avanzaron juntos.
        var reloadedBc = await h.Ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.AwaitingFiscalConfirmation, reloadedBc.Status);
        Assert.NotNull(reloadedBc.ConfirmedWithClientAt);

        var reloadedReserva = await h.Ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.PendingOperatorRefund, reloadedReserva.Status);

        // 2) TODOS los servicios quedaron cancelados (hotel literal, vuelo con codigo IATA "UN").
        var reloadedHotel = await h.Ctx.HotelBookings.AsNoTracking().FirstAsync(hb => hb.ReservaId == reserva.Id);
        var reloadedFlight = await h.Ctx.FlightSegments.AsNoTracking().FirstAsync(f => f.ReservaId == reserva.Id);
        Assert.Equal("Cancelado", reloadedHotel.Status);
        Assert.Equal("UN", reloadedFlight.Status);

        // 3) El recalculo del ARREGLO 1 corrio en el mismo request: deuda del operador a 0.
        var reloadedSupplier = await h.Ctx.Suppliers.AsNoTracking().FirstAsync(s => s.Id == supplier.Id);
        Assert.Equal(0m, reloadedSupplier.CurrentBalance);

        // 4) La NC se encolo (DESPUES del commit) exactamente una vez.
        h.InvoiceMock.Verify(s => s.EnqueueAnnulmentAsync(
            invoice.Id, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<int?>()), Times.Once);

        // 5) La auditoria de confirmacion se registro (via StageBusinessEvent, dentro del commit atomico).
        h.AuditMock.Verify(a => a.StageBusinessEvent(
            AuditActions.BookingCancellationConfirmed,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string>(), It.IsAny<string?>()), Times.Once);
    }

    // ============================================================
    // ARREGLO 2 — multi-operador con multa confirmada -> ND manual
    // ============================================================

    [Fact]
    public async Task DebitNote_TwoSuppliersWithConfirmedPenalty_RoutesToManualReview()
    {
        // Escenario post-NC: BC con DOS lineas de operadores DISTINTOS, ambas con penalidad Confirmed.
        // Al intentar emitir la ND automatica (via ConfirmPenaltyAsync), debe rutear a revision manual.
        var h = BuildService(enableDebitNote: true);

        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        var supplierA = new Supplier { Name = "Operador A", IsActive = true, PenaltyOwnership = PenaltyOwnership.Agency };
        var supplierB = new Supplier { Name = "Operador B", IsActive = true, PenaltyOwnership = PenaltyOwnership.Agency };
        h.Ctx.Customers.Add(customer);
        h.Ctx.Suppliers.Add(supplierA);
        h.Ctx.Suppliers.Add(supplierB);
        await h.Ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-A2", Name = "Reserva multi-multa", PayerId = customer.Id,
            Status = EstadoReserva.PendingOperatorRefund, Balance = 0m,
        };
        h.Ctx.Reservas.Add(reserva);
        await h.Ctx.SaveChangesAsync();

        var original = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 200, CAE = "cae-orig",
            Resultado = "A", MonId = "PES", ImporteTotal = 200_000m, ImporteNeto = 200_000m,
            ImporteIva = 0m, ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None,
        };
        var creditNote = new Invoice
        {
            TipoComprobante = 13, PuntoDeVenta = 1, NumeroComprobante = 201, CAE = "cae-nc",
            Resultado = "A", ReservaId = reserva.Id,
        };
        h.Ctx.Invoices.Add(original);
        h.Ctx.Invoices.Add(creditNote);
        await h.Ctx.SaveChangesAsync();
        creditNote.OriginalInvoiceId = original.Id;
        await h.Ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = customer.Id, SupplierId = supplierA.Id,
            OriginatingInvoiceId = original.Id, CreditNoteInvoiceId = creditNote.Id,
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            Reason = "Cancelacion multi-operador con dos multas",
            DraftedByUserId = "vendedor-1", ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-5),
        };
        h.Ctx.BookingCancellations.Add(bc);
        await h.Ctx.SaveChangesAsync();

        // Dos lineas, operadores distintos, ambas con multa CONFIRMADA.
        h.Ctx.BookingCancellationLines.Add(new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplierA.Id,
            ServiceTable = CancellableServiceTable.Hotel, ServiceId = 1,
            Scope = BookingCancellationLineScope.Full, Currency = "ARS",
            PenaltyStatus = PenaltyStatus.Confirmed, PenaltyAmount = 10_000m,
        });
        h.Ctx.BookingCancellationLines.Add(new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplierB.Id,
            ServiceTable = CancellableServiceTable.Transfer, ServiceId = 2,
            Scope = BookingCancellationLineScope.Full, Currency = "ARS",
            PenaltyStatus = PenaltyStatus.Confirmed, PenaltyAmount = 5_000m,
        });
        await h.Ctx.SaveChangesAsync();

        // ADR-044 T1 (2026-07-10): la cancelacion tiene lineas de DOS operadores distintos, asi que el service
        // exige especificar cual se esta confirmando (ResolveTargetSupplierId ya no adivina). Apuntamos al
        // operador PRINCIPAL del BC (supplierA) para reproducir el escenario original del test: su llamada de
        // confirm es la que dispara el intento de emision automatica de la ND, que el gate ARREGLO 2 debe frenar
        // porque YA hay dos operadores con multa confirmada (ambas lineas se sembraron Confirmed arriba).
        var request = new ConfirmPenaltyRequest(
            ConceptKind: CancellationConceptKind.AgencyManagementFee,
            ConfirmedPenaltyAmount: 10_000m,
            OperatorConfirmationDate: DateTime.UtcNow.AddDays(-1),
            DebitNotePurpose: null,
            SupportingDocumentReference: "https://docs/operador.pdf",
            SupplierPublicId: supplierA.PublicId);

        await h.Service.ConfirmPenaltyAsync(
            bc.PublicId, request, "admin-1", "Admin Uno",
            requesterIsAdmin: false, ct: default, userCanClassifyAgencyPenalty: true);

        // La ND NO se emitio automaticamente: NO se llamo a CreateAsync (la emision real de la ND).
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);

        // Se ruteo a revision manual (no quedo ND vinculada).
        var reloaded = await h.Ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(DebitNoteStatus.ManualReview, reloaded.DebitNoteStatus);
        Assert.Null(reloaded.DebitNoteInvoiceId);
    }

    [Fact]
    public async Task DebitNote_SingleSupplierWithConfirmedPenalty_StillEmits()
    {
        // Regresion del ARREGLO 2: con UN solo operador con multa confirmada, la ND SIGUE emitiendose
        // (no debe bloquearse). Es el camino feliz que el fix NO debe romper.
        var h = BuildService(enableDebitNote: true);

        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        var supplier = new Supplier { Name = "Operador Unico", IsActive = true, PenaltyOwnership = PenaltyOwnership.Agency };
        h.Ctx.Customers.Add(customer);
        h.Ctx.Suppliers.Add(supplier);
        await h.Ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-A2b", Name = "Reserva una multa", PayerId = customer.Id,
            Status = EstadoReserva.PendingOperatorRefund, Balance = 0m,
        };
        h.Ctx.Reservas.Add(reserva);
        await h.Ctx.SaveChangesAsync();

        var original = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 300, CAE = "cae-orig2",
            Resultado = "A", MonId = "PES", ImporteTotal = 100_000m, ImporteNeto = 100_000m,
            ImporteIva = 0m, ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None,
        };
        var creditNote = new Invoice
        {
            TipoComprobante = 13, PuntoDeVenta = 1, NumeroComprobante = 301, CAE = "cae-nc2",
            Resultado = "A", ReservaId = reserva.Id,
        };
        h.Ctx.Invoices.Add(original);
        h.Ctx.Invoices.Add(creditNote);
        await h.Ctx.SaveChangesAsync();
        creditNote.OriginalInvoiceId = original.Id;
        await h.Ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = customer.Id, SupplierId = supplier.Id,
            OriginatingInvoiceId = original.Id, CreditNoteInvoiceId = creditNote.Id,
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            Reason = "Cancelacion con una sola multa",
            DraftedByUserId = "vendedor-1", ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-5),
        };
        h.Ctx.BookingCancellations.Add(bc);
        await h.Ctx.SaveChangesAsync();

        // UNA sola linea con multa confirmada.
        h.Ctx.BookingCancellationLines.Add(new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplier.Id,
            ServiceTable = CancellableServiceTable.Hotel, ServiceId = 1,
            Scope = BookingCancellationLineScope.Full, Currency = "ARS",
            PenaltyStatus = PenaltyStatus.Confirmed, PenaltyAmount = 10_000m,
        });
        await h.Ctx.SaveChangesAsync();

        // CreateAsync inserta una ND y devuelve su DTO (para que se pueda vincular).
        h.InvoiceMock
            .Setup(s => s.CreateAsync(
                It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateInvoiceRequest req, string? uid, string? uname, CancellationToken ct) =>
            {
                var nd = new Invoice
                {
                    TipoComprobante = 12, PuntoDeVenta = 1, NumeroComprobante = 400,
                    Resultado = "PENDING", ReservaId = reserva.Id, OriginalInvoiceId = original.Id,
                };
                h.Ctx.Invoices.Add(nd);
                h.Ctx.SaveChanges();
                return new InvoiceDto { PublicId = nd.PublicId };
            });

        var request = new ConfirmPenaltyRequest(
            ConceptKind: CancellationConceptKind.AgencyManagementFee,
            ConfirmedPenaltyAmount: 10_000m,
            OperatorConfirmationDate: DateTime.UtcNow.AddDays(-1),
            DebitNotePurpose: null,
            SupportingDocumentReference: "https://docs/operador.pdf");

        await h.Service.ConfirmPenaltyAsync(
            bc.PublicId, request, "admin-1", "Admin Uno",
            requesterIsAdmin: false, ct: default, userCanClassifyAgencyPenalty: true);

        // La ND SI se emitio (camino feliz intacto): se vinculo y quedo Pending.
        var reloaded = await h.Ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.NotNull(reloaded.DebitNoteInvoiceId);
        Assert.Equal(DebitNoteStatus.Pending, reloaded.DebitNoteStatus);
    }
}
