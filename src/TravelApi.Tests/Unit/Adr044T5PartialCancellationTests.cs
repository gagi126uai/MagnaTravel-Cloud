using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-044 T5 Addendum (2026-07-11): los tests obligatorios de la anulacion PARCIAL (cancelar UN servicio,
/// no toda la reserva) — Decision A (compuerta de 3 salidas que reemplaza el bloqueo binario SEC-B1),
/// Decision B (campo manual + cap acumulativo contra el remanente de la factura) y Decision C (cada evento
/// de cancelacion abre su propio <see cref="BookingCancellation"/>; <c>Closed</c> se excluye del unico, igual
/// que <c>Aborted</c>), mas los fixes B1(a)/B1(b)/B2 de la Revision 2 (2 agujeros de plata que la Decision C
/// abria).
///
/// <para><b>Alcance real de esta entrega (decision de seguro fiscal, ver el reporte de la tanda)</b>: cancelar
/// un servicio con factura viva SIEMPRE resuelve la linea (factura destino + monto, cuando no hay ambiguedad)
/// pero NO dispara la emision fiscal real (Nota de Credito) en la MISMA llamada — eso exige un
/// <c>FiscalSnapshot</c> completo (TC/condiciones fiscales) que <see cref="CancelServiceRequest"/> no trae y
/// que este backend no puede inventar sin confirmacion del usuario (mismo criterio INV-118/INV-120 que ya
/// rige <c>ConfirmAsync</c>). La emision real queda para un paso de confirmacion aparte (mismo patron
/// Draft/Confirm que ya usa el resto del modulo), fuera de esta tanda (pantalla + wiring de emision real).</para>
/// </summary>
public class Adr044T5PartialCancellationTests
{
    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"adr044-t5-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static BookingCancellationService BuildService(
        AppDbContext ctx,
        IFiscalLiquidationCalculator? calculator = null,
        bool enablePartialCreditNotes = false)
    {
        var invoiceMock = new Mock<IInvoiceService>();
        invoiceMock
            .Setup(s => s.EnqueueAnnulmentAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<int?>()))
            .Returns(Task.CompletedTask);

        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableNewCancellationFlow = true,
                EnablePartialCreditNotes = enablePartialCreditNotes,
                OperatorRefundTimeoutDays = 60,
                RequireApprovalForInvoiceAnnulment = false,
            });

        return new BookingCancellationService(
            ctx,
            invoiceMock.Object,
            new Mock<IApprovalRequestService>().Object,
            new Mock<IAuditService>().Object,
            NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object,
            calculator ?? new Mock<IFiscalLiquidationCalculator>().Object,
            new Mock<IAdminUserCountService>().Object);
    }

    /// <summary>Siembra una reserva con un hotel confirmado (con Payer) de un operador dado. Sin factura.</summary>
    private static async Task<(Reserva Reserva, Supplier Supplier, HotelBooking Hotel)> SeedReservaWithHotelAsync(
        AppDbContext ctx, decimal hotelSalePrice = 30_000m, decimal hotelNetCost = 15_000m)
    {
        var customer = new Customer { FullName = "Cliente T5", IsActive = true };
        var supplier = new Supplier { Name = "Operador T5", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-T5",
            Name = "Reserva ADR-044 T5",
            PayerId = customer.Id,
            Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var hotel = new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            Status = "Confirmado",
            NetCost = hotelNetCost,
            SalePrice = hotelSalePrice,
            Currency = "ARS",
        };
        ctx.HotelBookings.Add(hotel);
        await ctx.SaveChangesAsync();

        return (reserva, supplier, hotel);
    }

    private static Invoice NewLiveInvoice(
        int reservaId, decimal importeTotal, int numeroComprobante, string cae = "cae-viva")
        => new()
        {
            TipoComprobante = 11, // Factura C, no es NC ni ND
            PuntoDeVenta = 1,
            NumeroComprobante = numeroComprobante,
            CAE = cae,
            Resultado = "A",
            ImporteTotal = importeTotal,
            ReservaId = reservaId,
            AnnulmentStatus = AnnulmentStatus.None,
            CreatedAt = DateTime.UtcNow,
        };

    // =====================================================================================
    // Test 1 — Regresion caso simple: reserva de 1 servicio sigue por DraftAsync/ConfirmAsync
    // total, byte-identico; CancelServiceAsync ni se invoca.
    // =====================================================================================

    [Fact]
    public async Task SimpleCase_OneServiceReserva_TotalCancellationViaDraftAsync_Unaffected()
    {
        using var ctx = NewDbContext();
        var (reserva, supplier, hotel) = await SeedReservaWithHotelAsync(ctx, hotelSalePrice: 1000m, hotelNetCost: 500m);
        var invoice = NewLiveInvoice(reserva.Id, 1000m, 1);
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);

        var dto = await service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "El cliente decidio no viajar"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        Assert.Equal("Drafted", dto.Status);
        var bc = await ctx.BookingCancellations.Include(b => b.Lines).AsNoTracking().SingleAsync();
        var line = Assert.Single(bc.Lines);
        Assert.Equal(BookingCancellationLineScope.Full, line.Scope);
        Assert.Equal(hotel.Id, line.ServiceId);
        // Los campos NUEVOS de T5 (TargetInvoiceId/ConfirmedGrossCreditAmount) son exclusivos del camino
        // CancelServiceAsync (Scope=Partial); el camino total (Scope=Full) nunca los toca.
        Assert.Null(line.TargetInvoiceId);
        Assert.Null(line.ConfirmedGrossCreditAmount);
    }

    // =====================================================================================
    // Test 2 — Bug BC Closed (camino PARCIAL): reserva con BC previo Closed -> cancelar OTRO
    // servicio abre un BC NUEVO (no le agrega una linea al Closed); el Closed queda intacto.
    // =====================================================================================

    [Fact]
    public async Task ClosedBc_PartialPath_DoesNotReattachLine_OpensNewBc()
    {
        using var ctx = NewDbContext();
        var (reserva, supplier, hotel1) = await SeedReservaWithHotelAsync(ctx, hotelSalePrice: 30_000m);
        var invoice = NewLiveInvoice(reserva.Id, 80_000m, 2);
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var hotel2 = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
            NetCost = 10_000m, SalePrice = 20_000m, Currency = "ARS",
        };
        ctx.HotelBookings.Add(hotel2);
        await ctx.SaveChangesAsync();

        // BC previo YA CERRADO (un evento fiscal terminado: NC con CAE, reembolso consumido) con la linea
        // de hotel1.
        var closedBc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = reserva.PayerId!.Value,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id,
            Status = BookingCancellationStatus.Closed,
            Reason = "Cancelacion parcial previa ya cerrada",
            DraftedAt = DateTime.UtcNow,
            DraftedByUserId = "tester",
            FiscalSnapshot = new FiscalSnapshot
            {
                Source = ExchangeRateSource.Manual, ExchangeRateAtOriginalInvoice = 1m,
                CurrencyAtEvent = "ARS", FetchedAt = DateTime.UtcNow,
            },
        };
        closedBc.Lines.Add(new BookingCancellationLine
        {
            SupplierId = supplier.Id, ServiceTable = CancellableServiceTable.Hotel, ServiceId = hotel1.Id,
            Scope = BookingCancellationLineScope.Partial, Currency = "ARS", LineSaleAmount = 30_000m,
        });
        ctx.BookingCancellations.Add(closedBc);
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);

        await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel2.PublicId, "Cancelo el segundo hotel"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var allBcs = await ctx.BookingCancellations.Include(b => b.Lines).AsNoTracking().ToListAsync();
        Assert.Equal(2, allBcs.Count);

        var closedReloaded = allBcs.Single(b => b.Status == BookingCancellationStatus.Closed);
        var closedLine = Assert.Single(closedReloaded.Lines);
        Assert.Equal(hotel1.Id, closedLine.ServiceId); // intacto, sin lineas nuevas.

        var newBc = allBcs.Single(b => b.Id != closedReloaded.Id);
        Assert.Equal(BookingCancellationStatus.Drafted, newBc.Status);
        var newLine = Assert.Single(newBc.Lines);
        Assert.Equal(hotel2.Id, newLine.ServiceId);
    }

    // =====================================================================================
    // Test 3 — Bug BC Closed (camino TOTAL, hallazgo nuevo del re-review): reserva con BC
    // previo Closed -> DraftAsync (anular el resto) NO rechaza INV-081; abre un BC nuevo.
    // =====================================================================================

    [Fact]
    public async Task ClosedBc_TotalPath_DraftAsync_DoesNotRejectInv081_OpensNewBc()
    {
        using var ctx = NewDbContext();
        var (reserva, supplier, hotel) = await SeedReservaWithHotelAsync(ctx);
        var invoice = NewLiveInvoice(reserva.Id, 80_000m, 3);
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var closedBc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = reserva.PayerId!.Value,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id,
            Status = BookingCancellationStatus.Closed,
            Reason = "Cancelacion parcial previa ya cerrada",
            DraftedAt = DateTime.UtcNow,
            DraftedByUserId = "tester",
            FiscalSnapshot = new FiscalSnapshot
            {
                Source = ExchangeRateSource.Manual, ExchangeRateAtOriginalInvoice = 1m,
                CurrencyAtEvent = "ARS", FetchedAt = DateTime.UtcNow,
            },
        };
        ctx.BookingCancellations.Add(closedBc);
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);

        // ANTES de esta tanda: esto rechazaba con INV-081 (el caso (d) trataba Closed como "en curso").
        var dto = await service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Anular el resto de la reserva"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        Assert.Equal("Drafted", dto.Status);
        var allBcs = await ctx.BookingCancellations.AsNoTracking().ToListAsync();
        Assert.Equal(2, allBcs.Count);
        Assert.Contains(allBcs, b => b.Status == BookingCancellationStatus.Closed);
        Assert.Contains(allBcs, b => b.PublicId == dto.PublicId && b.Status == BookingCancellationStatus.Drafted);
    }

    // =====================================================================================
    // Test 4 — INV-081 sigue bloqueando lo que debe: un BC en AwaitingFiscalConfirmation (NO
    // Closed, NO Aborted) sigue rechazando un segundo DraftAsync sobre la misma reserva.
    // =====================================================================================

    [Fact]
    public async Task InFlightBc_DraftAsync_StillRejectsInv081()
    {
        using var ctx = NewDbContext();
        var (reserva, supplier, hotel) = await SeedReservaWithHotelAsync(ctx);
        var invoice = NewLiveInvoice(reserva.Id, 80_000m, 4);
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var inFlightBc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = reserva.PayerId!.Value,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id,
            Status = BookingCancellationStatus.AwaitingFiscalConfirmation,
            Reason = "Cancelacion en curso",
            DraftedAt = DateTime.UtcNow,
            DraftedByUserId = "tester",
            ConfirmedWithClientAt = DateTime.UtcNow,
            FiscalSnapshot = new FiscalSnapshot
            {
                Source = ExchangeRateSource.Manual, ExchangeRateAtOriginalInvoice = 1m,
                CurrencyAtEvent = "ARS", FetchedAt = DateTime.UtcNow,
            },
        };
        ctx.BookingCancellations.Add(inFlightBc);
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.DraftAsync(
                new DraftCancellationRequest(reserva.PublicId, "Segundo intento de anular"),
                "vendedor-1", "Vendedor", CancellationToken.None));
        Assert.Equal("INV-081", ex.InvariantCode);

        // Sigue habiendo una unica fila (el rechazo no creo nada nuevo).
        Assert.Single(await ctx.BookingCancellations.ToListAsync());
    }

    // =====================================================================================
    // Test 5 — Cap acumulativo: 2 servicios comparten factura. El primero consume parte del
    // remanente; el segundo ve el remanente REDUCIDO; un tercero que excede lo que queda
    // se rechaza (no se persiste un monto que supere lo que la factura vale).
    // =====================================================================================

    [Fact]
    public async Task CapAcumulativo_TwoServicesShareInvoice_ThirdExceedsRemainder_Rejected()
    {
        using var ctx = NewDbContext();
        var (reserva, supplier, hotel1) = await SeedReservaWithHotelAsync(ctx, hotelSalePrice: 30_000m);
        var invoice = NewLiveInvoice(reserva.Id, 50_000m, 5);
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var hotel2 = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
            NetCost = 5_000m, SalePrice = 20_000m, Currency = "ARS",
        };
        var hotel3 = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
            NetCost = 1_000m, SalePrice = 5_000m, Currency = "ARS",
        };
        ctx.HotelBookings.AddRange(hotel2, hotel3);
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);

        // Cancelar hotel1 (30k de 50k) -> remanente pasa a 20k.
        await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel1.PublicId, "Cancelo hotel1"),
            "v1", "V", CancellationToken.None);
        var line1 = await ctx.BookingCancellationLines.AsNoTracking().SingleAsync(l => l.ServiceId == hotel1.Id);
        Assert.Equal(30_000m, line1.ConfirmedGrossCreditAmount);
        Assert.Equal(20_000m, await service.ComputeInvoiceRemainingCreditableAmountAsync(invoice.Id, CancellationToken.None));

        // Cancelar hotel2 (20k de 20k restantes) -> == remanente, se confirma; remanente pasa a 0.
        await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel2.PublicId, "Cancelo hotel2"),
            "v1", "V", CancellationToken.None);
        var line2 = await ctx.BookingCancellationLines.AsNoTracking().SingleAsync(l => l.ServiceId == hotel2.Id);
        Assert.Equal(20_000m, line2.ConfirmedGrossCreditAmount);
        Assert.Equal(0m, await service.ComputeInvoiceRemainingCreditableAmountAsync(invoice.Id, CancellationToken.None));

        // Cancelar hotel3 (5k, pero remanente ya es 0) -> el servicio SE cancela, pero el monto NO se
        // persiste (excederia lo que la factura vale). La factura destino SI queda resuelta (1 sola activa).
        await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel3.PublicId, "Cancelo hotel3"),
            "v1", "V", CancellationToken.None);
        var line3 = await ctx.BookingCancellationLines.AsNoTracking().SingleAsync(l => l.ServiceId == hotel3.Id);
        Assert.Null(line3.ConfirmedGrossCreditAmount);
        Assert.Equal(invoice.Id, line3.TargetInvoiceId);

        var hotel3Reloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel3.Id);
        Assert.True(TravelApi.Domain.Reservations.ServiceResolutionRules.IsCancelled(hotel3Reloaded));

        // Suma de montos confirmados nunca excede lo que la factura vale.
        var totalConfirmed = (line1.ConfirmedGrossCreditAmount ?? 0m)
            + (line2.ConfirmedGrossCreditAmount ?? 0m)
            + (line3.ConfirmedGrossCreditAmount ?? 0m);
        Assert.True(totalConfirmed <= invoice.ImporteTotal);
    }

    // =====================================================================================
    // Test 6 — Derivacion total-vs-parcial por monto: == remanente -> confirma ese monto (NC
    // total de esa factura); < remanente -> confirma el monto pedido (NC parcial); > remanente
    // -> rechazado, no se persiste.
    // =====================================================================================

    [Fact]
    public Task DerivacionTotalVsParcial_PorMonto_IgualARemanente_NcTotal()
        => DerivacionTotalVsParcial_PorMontoAsync(requestedAmount: 50_000m, expectedConfirmedAmount: 50_000m);

    [Fact]
    public Task DerivacionTotalVsParcial_PorMonto_MenorARemanente_NcParcial()
        => DerivacionTotalVsParcial_PorMontoAsync(requestedAmount: 30_000m, expectedConfirmedAmount: 30_000m);

    [Fact]
    public Task DerivacionTotalVsParcial_PorMonto_MayorARemanente_Rechazado()
        => DerivacionTotalVsParcial_PorMontoAsync(requestedAmount: 60_000m, expectedConfirmedAmount: null);

    private async Task DerivacionTotalVsParcial_PorMontoAsync(decimal requestedAmount, decimal? expectedConfirmedAmount)
    {
        using var ctx = NewDbContext();
        var (reserva, supplier, hotel) = await SeedReservaWithHotelAsync(ctx, hotelSalePrice: 80_000m);
        var invoice = NewLiveInvoice(reserva.Id, 50_000m, 6);
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);

        await service.CancelServiceAsync(
            new CancelServiceRequest(
                reserva.PublicId, "Hotel", hotel.PublicId, "Cancelo con monto explicito",
                TargetInvoicePublicId: invoice.PublicId, ConfirmedGrossCreditAmount: requestedAmount),
            "v1", "V", CancellationToken.None);

        var line = await ctx.BookingCancellationLines.AsNoTracking().SingleAsync();
        Assert.Equal(expectedConfirmedAmount, line.ConfirmedGrossCreditAmount);
        Assert.Equal(invoice.Id, line.TargetInvoiceId);

        // El servicio SIEMPRE se cancela, incluso en el caso > remanente (monto rechazado): la compuerta
        // nunca bloquea la cancelacion del servicio en si, solo deja el credito pendiente de resolucion.
        var hotelReloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.True(TravelApi.Domain.Reservations.ServiceResolutionRules.IsCancelled(hotelReloaded));
    }

    // =====================================================================================
    // Test 7 — 2+ facturas activas sin TargetInvoicePublicId elegido -> queda sin resolver
    // (nunca una NC automatica contra la factura equivocada); con eleccion explicita, resuelve.
    // =====================================================================================

    [Fact]
    public async Task TwoLiveInvoices_NoSelection_LeavesTargetInvoiceUnresolved_ButStillCancelsService()
    {
        using var ctx = NewDbContext();
        var (reserva, supplier, hotel) = await SeedReservaWithHotelAsync(ctx);
        var invoice1 = NewLiveInvoice(reserva.Id, 80_000m, 7, "cae-1");
        invoice1.MonId = "PES";
        var invoice2 = NewLiveInvoice(reserva.Id, 100m, 8, "cae-2");
        invoice2.TipoComprobante = 1;
        invoice2.MonId = "DOL";
        invoice2.CreatedAt = DateTime.UtcNow.AddMinutes(1);
        ctx.Invoices.AddRange(invoice1, invoice2);
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);

        await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Cancelo sin elegir factura"),
            "v1", "V", CancellationToken.None);

        var line = await ctx.BookingCancellationLines.AsNoTracking().SingleAsync();
        Assert.Null(line.TargetInvoiceId);
        Assert.Null(line.ConfirmedGrossCreditAmount);

        var hotelReloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.True(TravelApi.Domain.Reservations.ServiceResolutionRules.IsCancelled(hotelReloaded));
    }

    [Fact]
    public async Task TwoLiveInvoices_WithExplicitSelection_ResolvesChosenInvoice()
    {
        using var ctx = NewDbContext();
        var (reserva, supplier, hotel) = await SeedReservaWithHotelAsync(ctx, hotelSalePrice: 100m);
        var invoice1 = NewLiveInvoice(reserva.Id, 80_000m, 9, "cae-1");
        var invoice2 = NewLiveInvoice(reserva.Id, 100m, 10, "cae-2");
        invoice2.TipoComprobante = 1;
        invoice2.CreatedAt = DateTime.UtcNow.AddMinutes(1);
        ctx.Invoices.AddRange(invoice1, invoice2);
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);

        await service.CancelServiceAsync(
            new CancelServiceRequest(
                reserva.PublicId, "Hotel", hotel.PublicId, "Cancelo eligiendo la segunda factura",
                TargetInvoicePublicId: invoice2.PublicId),
            "v1", "V", CancellationToken.None);

        var line = await ctx.BookingCancellationLines.AsNoTracking().SingleAsync();
        Assert.Equal(invoice2.Id, line.TargetInvoiceId);
        Assert.Equal(100m, line.ConfirmedGrossCreditAmount); // == remanente de esa factura (100)
    }

    // =====================================================================================
    // Test 8 — Voucher Issued sigue bloqueando (regresion): la Decision A no toco esa rama.
    // Bloquea incluso cuando TAMBIEN hay factura viva (antes bloqueaba por cualquiera de las 2).
    // =====================================================================================

    [Fact]
    public async Task IssuedVoucher_StillBlocks_EvenWithLiveInvoiceAlso()
    {
        using var ctx = NewDbContext();
        var (reserva, supplier, hotel) = await SeedReservaWithHotelAsync(ctx);
        var invoice = NewLiveInvoice(reserva.Id, 80_000m, 11);
        ctx.Invoices.Add(invoice);
        ctx.Vouchers.Add(new Voucher { ReservaId = reserva.Id, Status = VoucherStatuses.Issued });
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);

        // Tanda 7 "contrato pantalla-motor" (2026-07-20): el candado de voucher ahora tira
        // ServiceCancellationRejectedException (mismo InvalidOperationException + Code aditivo CANCEL_SERVICE_VOUCHER_LIVE).
        var ex = await Assert.ThrowsAsync<ServiceCancellationRejectedException>(() =>
            service.CancelServiceAsync(
                new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Intento con voucher emitido"),
                "v1", "V", CancellationToken.None));
        Assert.Contains("voucher", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ServiceCancellationRejectedException.Codes.VoucherLive, ex.Code);

        Assert.Empty(await ctx.BookingCancellationLines.ToListAsync());
        var hotelReloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.False(TravelApi.Domain.Reservations.ServiceResolutionRules.IsCancelled(hotelReloaded));
    }

    // =====================================================================================
    // Test 9 — PayerId null con factura viva -> 409 explicito ANTES de tocar nada (antes se
    // salteaba en silencio).
    // =====================================================================================

    [Fact]
    public async Task LiveInvoice_NoPayer_Returns409_BeforeTouchingAnything()
    {
        using var ctx = NewDbContext();
        var supplier = new Supplier { Name = "Operador Sin Payer", IsActive = true };
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-NOPAYER", Name = "Sin Payer", PayerId = null, Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var hotel = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
            NetCost = 500m, SalePrice = 1000m, Currency = "ARS",
        };
        ctx.HotelBookings.Add(hotel);
        var invoice = NewLiveInvoice(reserva.Id, 1000m, 12);
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);

        // Tanda 7 "contrato pantalla-motor" (2026-07-20): mismo InvalidOperationException de siempre + Code
        // aditivo CANCEL_SERVICE_NO_PAYER.
        var ex = await Assert.ThrowsAsync<ServiceCancellationRejectedException>(() =>
            service.CancelServiceAsync(
                new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Intento sin payer"),
                "v1", "V", CancellationToken.None));
        Assert.Equal(ServiceCancellationRejectedException.Codes.NoPayer, ex.Code);

        var hotelReloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.False(TravelApi.Domain.Reservations.ServiceResolutionRules.IsCancelled(hotelReloaded));
        Assert.Empty(await ctx.BookingCancellationLines.ToListAsync());
    }

    // =====================================================================================
    // Test B1(a) — el camino LEGACY (Anular toda la reserva) capea el CancellationAmount que
    // alimenta al clasificador contra el remanente REAL de la factura, no contra ImporteTotal.
    // =====================================================================================

    [Fact]
    public async Task LegacyConfirmAsync_CapsCancellationAmount_AgainstRemainingCreditableAmount()
    {
        using var ctx = NewDbContext();
        var (reserva, supplier, hotel) = await SeedReservaWithHotelAsync(ctx, hotelSalePrice: 1000m);
        var invoice = NewLiveInvoice(reserva.Id, 1000m, 13);
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        // Simula una NC PARCIAL previa ya SUCCEEDED contra la MISMA factura (monto 400 de 1000).
        var partialNcInvoice = new Invoice
        {
            TipoComprobante = 13, PuntoDeVenta = 1, NumeroComprobante = 500, CAE = "cae-parcial",
            Resultado = "A", ImporteTotal = 400m, ReservaId = reserva.Id, OriginalInvoiceId = invoice.Id,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(partialNcInvoice);
        await ctx.SaveChangesAsync();

        var priorBc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = reserva.PayerId!.Value, SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id, Status = BookingCancellationStatus.Closed,
            Reason = "NC parcial previa", DraftedAt = DateTime.UtcNow, DraftedByUserId = "tester",
            FiscalSnapshot = new FiscalSnapshot
            {
                Source = ExchangeRateSource.Manual, ExchangeRateAtOriginalInvoice = 1m,
                CurrencyAtEvent = "ARS", FetchedAt = DateTime.UtcNow,
            },
        };
        priorBc.CreditNotes.Add(new BookingCancellationCreditNote
        {
            OriginatingInvoiceId = invoice.Id, CreditNoteInvoiceId = partialNcInvoice.Id,
            Status = BookingCancellationCreditNoteStatus.Succeeded, ArcaCurrency = "PES",
        });
        ctx.BookingCancellations.Add(priorBc);
        await ctx.SaveChangesAsync();

        FiscalLiquidationInput? captured = null;
        var calculatorMock = new Mock<IFiscalLiquidationCalculator>();
        calculatorMock
            .Setup(c => c.Calculate(It.IsAny<FiscalLiquidationInput>(), It.IsAny<OperationalFinanceSettings>()))
            .Callback<FiscalLiquidationInput, OperationalFinanceSettings>((input, _) => captured = input)
            .Returns(new FiscalLiquidationDto(
                OriginalInvoiceAmount: 1000m, CancellationAmount: 600m, OperatorPenaltyAmount: 0m,
                NonRefundableItemsAmount: 0m, FiscalAmountToCredit: 600m, AmountToRefundCustomer: 600m,
                FinalNetInvoiced: 400m, Case: PartialCreditNoteCase.Case2_FullCancellationNoRetention,
                Kind: CreditNoteKind.PartialOnOriginal, ReviewRequiredReason: ReviewRequiredReason.None,
                Currency: "ARS", ClassificationExplanation: "test"));

        var service = BuildService(ctx, calculatorMock.Object, enablePartialCreditNotes: true);

        var draftDto = await service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Anular el resto de la reserva"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        // Tanda B (2026-07-16): ConfirmAsync resuelve las 3 condiciones fiscales SERVER-SIDE
        // (ResolveServerSideTaxIdentity), no del request.SnapshotData de abajo (que ahora se ignora).
        // Sin esta fila de AfipSettings + el TaxCondition del operador, ConfirmAsync rebotaria con
        // INV-118 antes de llegar al calculator (que es lo que este test quiere verificar).
        ctx.AfipSettings.Add(new AfipSettings { Cuit = 20111111112, TaxCondition = "Monotributo" });
        supplier.TaxCondition = "IVA_RESP_INSCRIPTO";
        await ctx.SaveChangesAsync();

        var confirmRequest = new ConfirmCancellationRequest(
            SnapshotData: new FiscalSnapshotData(
                CurrencyAtEvent: "ARS", ExchangeRateAtOriginalInvoice: 1m, Source: ExchangeRateSource.Manual,
                ManualJustification: "TC 1 a 1", AgencyTaxConditionAtEvent: "Monotributo",
                SupplierTaxConditionAtEvent: "IVA_RESP_INSCRIPTO", CustomerTaxConditionAtEvent: "Consumidor Final"),
            IsAdminOverride: false, OverrideReason: null, ApprovalRequestPublicId: null);

        await service.ConfirmAsync(
            draftDto.PublicId, confirmRequest, "vendedor-1", "Vendedor", requesterIsAdmin: false, CancellationToken.None);

        Assert.NotNull(captured);
        // ANTES del fix: CancellationAmount hubiera sido 1000 (ImporteTotal a secas). Con el fix, es el
        // remanente real (1000 - 400 ya acreditados = 600).
        Assert.Equal(600m, captured!.CancellationAmount);
        Assert.Equal(1000m, captured.OriginalInvoiceAmount); // la base fiscal del comprobante NO cambia.
    }

    // =====================================================================================
    // Test B1(b) — tras una cancelacion parcial, el build de la anulacion TOTAL siguiente NO
    // genera linea ni RefundCap para el servicio ya cancelado (excluido SOLO en scope Full).
    // =====================================================================================

    [Fact]
    public async Task PartialCancellation_ThenTotalDraft_ExcludesAlreadyCancelledService()
    {
        using var ctx = NewDbContext();
        var (reserva, supplier, hotel1) = await SeedReservaWithHotelAsync(ctx, hotelSalePrice: 30_000m);
        var invoice = NewLiveInvoice(reserva.Id, 80_000m, 14);
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var hotel2 = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
            NetCost = 10_000m, SalePrice = 20_000m, Currency = "ARS",
        };
        ctx.HotelBookings.Add(hotel2);
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);

        // Cancelacion PARCIAL de hotel1: queda Cancelado + con su propia linea Partial.
        await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel1.PublicId, "Cancelo hotel1 solo"),
            "v1", "V", CancellationToken.None);

        // El BC de la cancelacion parcial queda Drafted (sin emision fiscal en esta tanda). Para poder
        // ejercitar DraftAsync (que exige que NO haya un BC "en curso"), lo marcamos Aborted a mano — el
        // vendedor se arrepintio del enfoque parcial y decide anular TODO el resto por el camino total.
        var partialBc = await ctx.BookingCancellations.FirstAsync();
        partialBc.Status = BookingCancellationStatus.Aborted;
        await ctx.SaveChangesAsync();

        // Anular TODA la reserva (Scope=Full): el build NO debe generar linea para hotel1 (ya cancelado).
        var dto = await service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Anular el resto de la reserva"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var fullBc = await ctx.BookingCancellations
            .Include(b => b.Lines)
            .AsNoTracking()
            .SingleAsync(b => b.PublicId == dto.PublicId);

        var fullLine = Assert.Single(fullBc.Lines); // SOLO hotel2 (hotel1 ya cancelado, excluido).
        Assert.Equal(hotel2.Id, fullLine.ServiceId);
        Assert.DoesNotContain(fullBc.Lines, l => l.ServiceId == hotel1.Id);
    }

    // =====================================================================================
    // Test B2 — el cartel de multa con 2 BC con multa viva simultanea muestra la SUMA por
    // moneda, no una fila arbitraria. Ejercita la agregacion por moneda a traves del metodo
    // PUBLICO GetReservaByIdAsync (extremo a extremo del read-model), en 2 escenarios: misma
    // moneda (suma) y monedas distintas (desglose).
    // =====================================================================================

    [Fact]
    public async Task TwoLiveDebitNotes_SameCurrency_AggregatesSum()
    {
        using var ctx = NewDbContext();
        var reserva = new Reserva
        {
            NumeroReserva = "R-B2-1", Name = "Reserva con 2 multas", Status = EstadoReserva.Cancelled,
            // Balance > 0 es precondicion de ReservationDebtRules.DeriveForCancelled para el contexto
            // "MultaPorCobrar" (saldo positivo + respaldo Live); sin esto cae a "None" (nada pendiente).
            Balance = 3000m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        // Dos BC con multa VIVA (ND confirmada, ventana de emision diferida) sobre la MISMA reserva, misma
        // moneda. Mismo patron minimo que ReservaServiceCancelledMoneyContextTests: la ND VIVA por la rama 2
        // del predicado (Confirmed + monto > 0 + DebitNoteStatus Pending) no necesita factura ND real.
        AddLiveDebitNoteBc(ctx, reserva.Id, penaltyAmount: 1000m, penaltyCurrency: "PES");
        AddLiveDebitNoteBc(ctx, reserva.Id, penaltyAmount: 2000m, penaltyCurrency: "PES");
        await ctx.SaveChangesAsync();

        var reservaService = CreateReservaService(ctx);
        var dto = await reservaService.GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        Assert.NotNull(dto);
        Assert.Equal("MultaPorCobrar", dto!.CancelledMoneyContext);
        // Suma de ambas BC (1000 + 2000 = 3000), no el monto de una sola fila arbitraria.
        Assert.Equal(3000m, dto.CancelledPenaltyAmount);
        Assert.Equal("ARS", dto.CancelledPenaltyCurrency);
        var single = Assert.Single(dto.CancelledPenaltiesByCurrency);
        Assert.Equal("ARS", single.Currency);
        Assert.Equal(3000m, single.Amount);
    }

    [Fact]
    public async Task TwoLiveDebitNotes_DifferentCurrencies_ShowsBreakdown()
    {
        using var ctx = NewDbContext();
        var reserva = new Reserva
        {
            NumeroReserva = "R-B2-2", Name = "Reserva con 2 multas en 2 monedas", Status = EstadoReserva.Cancelled,
            Balance = 1000m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        AddLiveDebitNoteBc(ctx, reserva.Id, penaltyAmount: 1000m, penaltyCurrency: "PES");
        AddLiveDebitNoteBc(ctx, reserva.Id, penaltyAmount: 50m, penaltyCurrency: "DOL");
        await ctx.SaveChangesAsync();

        var reservaService = CreateReservaService(ctx);
        var dto = await reservaService.GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        Assert.NotNull(dto);
        Assert.Equal(2, dto!.CancelledPenaltiesByCurrency.Count);
        Assert.Contains(dto.CancelledPenaltiesByCurrency, p => p.Currency == "ARS" && p.Amount == 1000m);
        Assert.Contains(dto.CancelledPenaltiesByCurrency, p => p.Currency == "USD" && p.Amount == 50m);
    }

    /// <summary>
    /// Agrega (sin SaveChanges, el caller lo hace) un BC con multa VIVA por la rama 2 del predicado
    /// compartido (<c>PenaltyStatus.Confirmed</c> + monto &gt; 0 + <c>DebitNoteStatus.Pending</c>, ventana de
    /// emision diferida ADR-014) — no requiere ninguna factura ND real, mismo patron que
    /// <c>ReservaServiceCancelledMoneyContextTests.AddCancellationRawAsync</c>.
    /// </summary>
    private static void AddLiveDebitNoteBc(AppDbContext ctx, int reservaId, decimal penaltyAmount, string penaltyCurrency)
    {
        ctx.BookingCancellations.Add(new BookingCancellation
        {
            ReservaId = reservaId,
            Reason = "Anulacion con multa",
            DraftedByUserId = "tester",
            ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-5),
            PenaltyStatus = PenaltyStatus.Confirmed,
            DebitNoteStatus = DebitNoteStatus.Pending,
            PenaltyAmountAtEvent = penaltyAmount,
            PenaltyCurrencyAtEvent = penaltyCurrency,
        });
    }

    private static ReservaService CreateReservaService(AppDbContext ctx)
    {
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());

        return new ReservaService(
            ctx,
            new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper(),
            settingsMock.Object,
            BuildUserManager(),
            NullLogger<ReservaService>.Instance);
    }

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        store.Setup(s => s.FindByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((ApplicationUser?)null);
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    // =====================================================================================
    // Test 14 (Revision 2) — Pending -> Failed libera el remanente: reusa LITERALMENTE
    // ApplyChildResultAndReevaluateAsync (ADR-042, via OnArcaFailedAsync) para pasar una hija
    // Pending a Failed; el remanente vuelve a estar disponible para la siguiente NC parcial.
    // =====================================================================================

    [Fact]
    public async Task PendingCreditNote_FailsInArca_ReleasesRemainingAmount()
    {
        using var ctx = NewDbContext();
        var (reserva, supplier, hotel) = await SeedReservaWithHotelAsync(ctx, hotelSalePrice: 30_000m);
        var invoice = NewLiveInvoice(reserva.Id, 50_000m, 15);
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = reserva.PayerId!.Value, SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id, Status = BookingCancellationStatus.AwaitingFiscalConfirmation,
            Reason = "NC parcial en curso", DraftedAt = DateTime.UtcNow, DraftedByUserId = "tester",
            ConfirmedWithClientAt = DateTime.UtcNow,
            FiscalSnapshot = new FiscalSnapshot
            {
                Source = ExchangeRateSource.Manual, ExchangeRateAtOriginalInvoice = 1m,
                CurrencyAtEvent = "ARS", FetchedAt = DateTime.UtcNow,
            },
        };
        bc.CreditNotes.Add(new BookingCancellationCreditNote
        {
            OriginatingInvoiceId = invoice.Id, Status = BookingCancellationCreditNoteStatus.Pending,
            ArcaCurrency = "PES",
        });
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);

        // Antes del rechazo de ARCA: la hija Pending SIN linea T5 asociada representa una NC TOTAL en camino
        // (fallback legacy) -> remanente = 0.
        Assert.Equal(0m, await service.ComputeInvoiceRemainingCreditableAmountAsync(invoice.Id, CancellationToken.None));

        // ARCA rechaza la NC: la hija pasa a Failed (MISMO metodo que ADR-042 usa para todos los rechazos).
        await service.OnArcaFailedAsync(invoice.Id, "Rechazo de prueba", CancellationToken.None);

        // El remanente vuelve a estar disponible por completo (la hija Failed no cuenta).
        Assert.Equal(50_000m, await service.ComputeInvoiceRemainingCreditableAmountAsync(invoice.Id, CancellationToken.None));
    }

    // =====================================================================================
    // FRENTE A (fix fiscal-b) — guard de moneda: si la moneda de la linea no coincide con la
    // de la factura destino (normalizando ISO<->ARCA), o el TC de una factura extranjera es
    // incoherente (cotiz 1), NO se auto-acredita: la factura destino queda resuelta pero SIN
    // monto -> visible como pendiente de resolucion manual. El servicio igual se cancela.
    // =====================================================================================

    [Fact]
    public async Task FrenteA_CurrencyMismatch_ForeignInvoice_ArsLine_ResolvesTargetButNoAmount()
    {
        using var ctx = NewDbContext();
        var (reserva, supplier, hotel) = await SeedReservaWithHotelAsync(ctx); // hotel Currency = "ARS"
        // Factura en USD con cotizacion COHERENTE (100), para aislar el mismatch de MONEDA (no el de TC).
        var invoice = NewLiveInvoice(reserva.Id, 80_000m, 30);
        invoice.MonId = "DOL";
        invoice.MonCotiz = 100m;
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);

        await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Cancelo hotel ARS contra factura USD"),
            "v1", "V", CancellationToken.None);

        var line = await ctx.BookingCancellationLines.AsNoTracking().SingleAsync();
        Assert.Equal(invoice.Id, line.TargetInvoiceId); // factura resuelta (1 sola activa)
        Assert.Null(line.ConfirmedGrossCreditAmount);    // pero SIN monto (moneda no coincide)

        var hotelReloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.True(TravelApi.Domain.Reservations.ServiceResolutionRules.IsCancelled(hotelReloaded));
    }

    [Fact]
    public async Task FrenteA_ForeignInvoiceWithExchangeRateOne_Incoherent_ResolvesTargetButNoAmount()
    {
        using var ctx = NewDbContext();
        // Hotel en USD para que la MONEDA coincida (aislamos el fallo de coherencia del TC).
        var (reserva, supplier, hotel) = await SeedReservaWithHotelAsync(ctx);
        hotel.Currency = "USD";
        // Factura en USD pero con cotizacion 1 (dato corrupto para una extranjera).
        var invoice = NewLiveInvoice(reserva.Id, 80_000m, 31);
        invoice.MonId = "DOL";
        invoice.MonCotiz = 1m;
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);

        await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Cancelo hotel USD, factura USD cotiz 1"),
            "v1", "V", CancellationToken.None);

        var line = await ctx.BookingCancellationLines.AsNoTracking().SingleAsync();
        Assert.Equal(invoice.Id, line.TargetInvoiceId);
        Assert.Null(line.ConfirmedGrossCreditAmount); // TC incoherente -> no se inventa el credito
    }

    // =====================================================================================
    // FRENTE B (fix B2-backend) — el cap se reserva por la LINEA, NO por una hija Pending
    // fantasma: un auto-resuelto NO crea ninguna fila BookingCancellationCreditNote, pero
    // igual descuenta el remanente. Y dos lineas T5 sobre la misma factura se topean entre si.
    // =====================================================================================

    [Fact]
    public async Task FrenteB_AutoResolve_ReservesByLine_DoesNotCreatePhantomChild()
    {
        using var ctx = NewDbContext();
        var (reserva, supplier, hotel) = await SeedReservaWithHotelAsync(ctx, hotelSalePrice: 30_000m);
        var invoice = NewLiveInvoice(reserva.Id, 50_000m, 32);
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);

        await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Cancelo hotel"),
            "v1", "V", CancellationToken.None);

        var line = await ctx.BookingCancellationLines.AsNoTracking().SingleAsync();
        Assert.Equal(30_000m, line.ConfirmedGrossCreditAmount);

        // NO se creo ninguna hija Pending fantasma (la reserva del cap la lleva la LINEA).
        Assert.Empty(await ctx.BookingCancellationCreditNotes.AsNoTracking().ToListAsync());

        // Pero el remanente igual bajo (via la linea): 50k - 30k = 20k.
        Assert.Equal(20_000m, await service.ComputeInvoiceRemainingCreditableAmountAsync(invoice.Id, CancellationToken.None));
    }

    [Fact]
    public async Task FrenteB_TwoT5Lines_SameInvoice_SecondRejectedExceedsRemainder()
    {
        using var ctx = NewDbContext();
        var (reserva, supplier, hotel1) = await SeedReservaWithHotelAsync(ctx, hotelSalePrice: 60m);
        var invoice = NewLiveInvoice(reserva.Id, 100m, 33);
        ctx.Invoices.Add(invoice);
        var hotel2 = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
            NetCost = 20m, SalePrice = 60m, Currency = "ARS",
        };
        ctx.HotelBookings.Add(hotel2);
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);

        await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel1.PublicId, "Cancelo hotel1 (60)"),
            "v1", "V", CancellationToken.None);
        await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel2.PublicId, "Cancelo hotel2 (60 sobre 40 restantes)"),
            "v1", "V", CancellationToken.None);

        var line1 = await ctx.BookingCancellationLines.AsNoTracking().SingleAsync(l => l.ServiceId == hotel1.Id);
        var line2 = await ctx.BookingCancellationLines.AsNoTracking().SingleAsync(l => l.ServiceId == hotel2.Id);
        Assert.Equal(60m, line1.ConfirmedGrossCreditAmount);
        // 60 pedido pero solo quedaban 40 de remanente -> se rechaza (no se persiste monto que exceda).
        Assert.Null(line2.ConfirmedGrossCreditAmount);
        Assert.Equal(invoice.Id, line2.TargetInvoiceId);
    }

    [Fact]
    public async Task FrenteB_ExcludeBookingCancellationId_AntiDoubleCount()
    {
        using var ctx = NewDbContext();
        var (reserva, supplier, hotel) = await SeedReservaWithHotelAsync(ctx, hotelSalePrice: 60m);
        var invoice = NewLiveInvoice(reserva.Id, 100m, 34);
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);

        await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Cancelo hotel (60)"),
            "v1", "V", CancellationToken.None);

        var bc = await ctx.BookingCancellations.AsNoTracking().SingleAsync();

        // Sin exclusion: la reserva por-linea (60) descuenta -> remanente 40.
        Assert.Equal(40m, await service.ComputeInvoiceRemainingCreditableAmountAsync(invoice.Id, CancellationToken.None));
        // Excluyendo el PROPIO BC (el caso del anular-total que absorbe la parcial): la reserva por-linea NO
        // se resta contra la anulacion de su propio BC -> remanente pleno 100 (la parcial nunca emitio su NC).
        Assert.Equal(100m, await service.ComputeInvoiceRemainingCreditableAmountAsync(
            invoice.Id, CancellationToken.None, excludeBookingCancellationId: bc.Id));
    }

    // =====================================================================================
    // FRENTE D — bandeja "Comprobantes por resolver": incluye el Drafted T5 (con linea Partial)
    // con etiqueta de negocio + monto; NO incluye un Drafted de anulacion total (Scope=Full);
    // el Status proyectado NUNCA es el nombre crudo del enum.
    // =====================================================================================

    [Fact]
    public async Task FrenteD_Bandeja_IncludesDraftedT5_WithBusinessLabelAndAmount_ExcludesTotalDraft()
    {
        using var ctx = NewDbContext();
        var (reserva, supplier, hotel) = await SeedReservaWithHotelAsync(ctx);
        var invoice = NewLiveInvoice(reserva.Id, 50_000m, 35);
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        // BC T5: Drafted con una linea Partial resuelta (monto confirmado).
        var t5Bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = reserva.PayerId!.Value, SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id, Status = BookingCancellationStatus.Drafted,
            Reason = "Cancelacion parcial", DraftedAt = DateTime.UtcNow.AddHours(-2), DraftedByUserId = "t",
            FiscalSnapshot = new FiscalSnapshot { Source = ExchangeRateSource.Unset, FetchedAt = default },
        };
        t5Bc.Lines.Add(new BookingCancellationLine
        {
            SupplierId = supplier.Id, ServiceTable = CancellableServiceTable.Hotel, ServiceId = hotel.Id,
            Scope = BookingCancellationLineScope.Partial, Currency = "ARS",
            LineSaleAmount = 30_000m, TargetInvoiceId = invoice.Id, ConfirmedGrossCreditAmount = 30_000m,
        });

        // Segunda reserva con un Drafted de anulacion TOTAL (Scope=Full): NO debe aparecer en la bandeja.
        var (reserva2, supplier2, hotel2) = await SeedReservaWithHotelAsync(ctx);
        var totalBc = new BookingCancellation
        {
            ReservaId = reserva2.Id, CustomerId = reserva2.PayerId!.Value, SupplierId = supplier2.Id,
            OriginatingInvoiceId = invoice.Id, Status = BookingCancellationStatus.Drafted,
            Reason = "Anulacion total en curso", DraftedAt = DateTime.UtcNow, DraftedByUserId = "t",
            FiscalSnapshot = new FiscalSnapshot { Source = ExchangeRateSource.Unset, FetchedAt = default },
        };
        totalBc.Lines.Add(new BookingCancellationLine
        {
            SupplierId = supplier2.Id, ServiceTable = CancellableServiceTable.Hotel, ServiceId = hotel2.Id,
            Scope = BookingCancellationLineScope.Full, Currency = "ARS", LineSaleAmount = 30_000m,
        });

        ctx.BookingCancellations.AddRange(t5Bc, totalBc);
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);
        var rows = await service.GetCancellationsPendingCreditNoteReviewAsync(CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(t5Bc.PublicId, row.BookingCancellationPublicId);
        Assert.Equal("Pendiente de emisión", row.Status);
        Assert.Equal(30_000m, row.CreditNoteAmount);
        Assert.Equal("ARS", row.CreditNoteCurrency);

        // Data-exposure: el Status NUNCA es el nombre crudo del enum.
        Assert.DoesNotContain(rows, r => r.Status == "Drafted" || r.Status == "ManualReviewPending");
    }

    [Fact]
    public async Task FrenteD_Bandeja_ExcludesAbsorbedDraft_PartialPlusFull()
    {
        // FIX N1 (security): un Drafted que el anular-total ABSORBIO (tiene Partial + Full) YA es una anulacion
        // total en curso, no un pendiente de emision parcial: NO debe aparecer en la bandeja como
        // "Pendiente de emisión" (seria un display engañoso con el monto de las Partial).
        using var ctx = NewDbContext();
        var (reserva, supplier, hotel) = await SeedReservaWithHotelAsync(ctx);
        var invoice = NewLiveInvoice(reserva.Id, 50_000m, 38);
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var absorbedBc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = reserva.PayerId!.Value, SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id, Status = BookingCancellationStatus.Drafted,
            Reason = "Anulacion total que absorbio una parcial", DraftedAt = DateTime.UtcNow, DraftedByUserId = "t",
            FiscalSnapshot = new FiscalSnapshot { Source = ExchangeRateSource.Unset, FetchedAt = default },
        };
        absorbedBc.Lines.Add(new BookingCancellationLine
        {
            SupplierId = supplier.Id, ServiceTable = CancellableServiceTable.Hotel, ServiceId = hotel.Id,
            Scope = BookingCancellationLineScope.Partial, Currency = "ARS",
            LineSaleAmount = 30_000m, TargetInvoiceId = invoice.Id, ConfirmedGrossCreditAmount = 30_000m,
        });
        absorbedBc.Lines.Add(new BookingCancellationLine
        {
            SupplierId = supplier.Id, ServiceTable = CancellableServiceTable.Hotel, ServiceId = hotel.Id + 1,
            Scope = BookingCancellationLineScope.Full, Currency = "ARS", LineSaleAmount = 20_000m,
        });
        ctx.BookingCancellations.Add(absorbedBc);
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);
        var rows = await service.GetCancellationsPendingCreditNoteReviewAsync(CancellationToken.None);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task FrenteD_EditLiquidation_OnDraftedT5_RejectsCleanly()
    {
        using var ctx = NewDbContext();
        var (reserva, supplier, hotel) = await SeedReservaWithHotelAsync(ctx);
        var invoice = NewLiveInvoice(reserva.Id, 50_000m, 36);
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var t5Bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = reserva.PayerId!.Value, SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id, Status = BookingCancellationStatus.Drafted,
            Reason = "Cancelacion parcial", DraftedAt = DateTime.UtcNow, DraftedByUserId = "t",
            FiscalSnapshot = new FiscalSnapshot { Source = ExchangeRateSource.Unset, FetchedAt = default },
        };
        t5Bc.Lines.Add(new BookingCancellationLine
        {
            SupplierId = supplier.Id, ServiceTable = CancellableServiceTable.Hotel, ServiceId = hotel.Id,
            Scope = BookingCancellationLineScope.Partial, Currency = "ARS",
            LineSaleAmount = 30_000m, TargetInvoiceId = invoice.Id, ConfirmedGrossCreditAmount = 30_000m,
        });
        ctx.BookingCancellations.Add(t5Bc);
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);

        // Un Drafted T5 NO es editable-liquidable (solo se edita desde ManualReviewPending). Mensaje limpio.
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.EditLiquidationAsync(
                t5Bc.PublicId,
                new EditLiquidationRequest(
                    OperatorPenaltyAmountOverride: null,
                    NonRefundableItemsAmountOverride: null,
                    CreditNoteKindOverride: null,
                    Comment: "Intento de editar una cancelacion parcial pendiente de emision"),
                "admin", "Admin", CancellationToken.None));
        Assert.DoesNotContain("Drafted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // =====================================================================================
    // FRENTE E — el anular-total ABSORBE y completa el draft parcial (Drafted): reusa el MISMO
    // BC, preserva la(s) linea(s) Partial del servicio ya cancelado y agrega las lineas Full de
    // los servicios vivos restantes. Sin abortar a mano.
    // =====================================================================================

    [Fact]
    public async Task FrenteE_TotalDraft_AbsorbsDraftedPartial_KeepsPartialAddsFull()
    {
        using var ctx = NewDbContext();
        var (reserva, supplier, hotel1) = await SeedReservaWithHotelAsync(ctx, hotelSalePrice: 30_000m);
        var invoice = NewLiveInvoice(reserva.Id, 80_000m, 37);
        ctx.Invoices.Add(invoice);
        var hotel2 = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
            NetCost = 10_000m, SalePrice = 20_000m, Currency = "ARS",
        };
        ctx.HotelBookings.Add(hotel2);
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);

        // Cancelacion PARCIAL de hotel1 -> BC Drafted con 1 linea Partial (hotel1). NO se aborta a mano.
        await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel1.PublicId, "Cancelo hotel1 solo"),
            "v1", "V", CancellationToken.None);

        // Ahora el vendedor decide anular TODO el resto por el camino total: reusa el MISMO BC.
        var dto = await service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Anular todo el file"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var bc = await ctx.BookingCancellations
            .Include(b => b.Lines)
            .AsNoTracking()
            .SingleAsync(b => b.PublicId == dto.PublicId);

        // Un solo BC (no se abrio otro): tiene la Partial de hotel1 + la Full de hotel2.
        Assert.Equal(1, await ctx.BookingCancellations.AsNoTracking().CountAsync());
        Assert.Equal(2, bc.Lines.Count);
        Assert.Contains(bc.Lines, l => l.ServiceId == hotel1.Id && l.Scope == BookingCancellationLineScope.Partial);
        Assert.Contains(bc.Lines, l => l.ServiceId == hotel2.Id && l.Scope == BookingCancellationLineScope.Full);
        Assert.Equal(BookingCancellationStatus.Drafted, bc.Status);

        // La absorcion NO pisa la plata ya reservada: la linea Partial de hotel1 conserva su monto confirmado
        // (30k, resuelto al cancelar el servicio contra la unica factura viva de 80k).
        var partialLine = bc.Lines.Single(l => l.ServiceId == hotel1.Id);
        Assert.Equal(30_000m, partialLine.ConfirmedGrossCreditAmount);
        Assert.Equal(invoice.Id, partialLine.TargetInvoiceId);
    }

    [Fact]
    public async Task FrenteE_AbsorbPartial_NoLiveServicesLeft_StillRefreshesAmountPaid()
    {
        // FIX N2 (backend reviewer): si el anular-total absorbe una parcial pero NO quedan servicios vivos con
        // operador (todos cancelados), igual hay que refrescar AmountPaidAtCancellation/EstimatedRefundAmount
        // con los pagos activos — antes quedaban en 0 por salir temprano cuando no se agregaba ninguna linea Full.
        using var ctx = NewDbContext();
        var (reserva, supplier, hotel) = await SeedReservaWithHotelAsync(ctx, hotelSalePrice: 30_000m);
        var invoice = NewLiveInvoice(reserva.Id, 80_000m, 39);
        ctx.Invoices.Add(invoice);
        ctx.Payments.Add(new Payment { ReservaId = reserva.Id, Amount = 25_000m, Status = "Paid", IsDeleted = false });
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);

        // Cancelar el UNICO servicio -> BC Drafted parcial; ya no quedan servicios vivos con operador.
        await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Cancelo unico hotel"),
            "v1", "V", CancellationToken.None);

        // Anular TODO -> reusa el draft parcial; BuildFull no agrega lineas (no hay vivos) pero DEBE refrescar
        // el estimado de reembolso del file con el pago activo (25k).
        var dto = await service.DraftAsync(
            new DraftCancellationRequest(reserva.PublicId, "Anular todo el file"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var bc = await ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.PublicId == dto.PublicId);
        Assert.Equal(25_000m, bc.AmountPaidAtCancellation);
        Assert.Equal(25_000m, bc.EstimatedRefundAmount);
    }
}
