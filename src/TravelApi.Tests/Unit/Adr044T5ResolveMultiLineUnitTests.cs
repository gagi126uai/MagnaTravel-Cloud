using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Spec UX 2026-07-17 ("resolver devoluciones VIEJAS de servicios cancelados", varios pendientes a la vez):
/// tests UNIT (InMemory, sin Docker) del caso real que destapó Gastón — un <see cref="BookingCancellation"/>
/// con DOS servicios cancelados esperando devolución, del MISMO operador pero en monedas distintas (un hotel
/// en dólares y una excursión en pesos). Antes de esta tanda el guard <c>INV-T5-RESOLVE-STATE</c> exigía
/// "un único servicio pendiente" y bloqueaba este caso entero; y aunque se lo esquivara, la emisión de la
/// primera devolución dejaba a la segunda HUÉRFANA para siempre (<see cref="BookingCancellation.Status"/> nunca
/// volvía a <c>Drafted</c>). Estos tests cubren: resolver de a uno (el resto queda intacto), la moneda mandada
/// por la factura, los rechazos claros (renglón ambiguo/inexistente/ya emitido, líneas Full mezcladas), la
/// emisión independiente por factura, y los datos que expone el DTO nuevo (<c>PartialCreditNoteEmission.Lines</c>).
/// </summary>
public class Adr044T5ResolveMultiLineUnitTests
{
    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"adr044-t5-resolve-multiline-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private sealed record Harness(
        BookingCancellationService Service,
        AppDbContext Ctx,
        Mock<IInvoiceService> InvoiceMock,
        Mock<IAuditService> AuditMock);

    private static Harness BuildService(AppDbContext ctx)
    {
        var invoiceMock = new Mock<IInvoiceService>();
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableNewCancellationFlow = true,
                OperatorRefundTimeoutDays = 45,
                EnableCancellationDebitNote = false,
                IvaProrrateoMode = IvaProrrateoMode.ProportionalToNet,
                PartialCreditNoteRoundingTolerance = 0.02m,
            });
        var auditMock = new Mock<IAuditService>();

        var service = new BookingCancellationService(
            ctx,
            invoiceMock.Object,
            new Mock<IApprovalRequestService>().Object,
            auditMock.Object,
            NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object,
            new Mock<IFiscalLiquidationCalculator>().Object,
            new Mock<IAdminUserCountService>().Object);

        return new Harness(service, ctx, invoiceMock, auditMock);
    }

    /// <summary>
    /// Reproduce el caso real de Gastón (BC 16, reserva 48, 2026-07-16): un hotel en dólares (US$ 700) y una
    /// excursión en pesos ($ 720.000), MISMO operador, las DOS lineas Partial todavía SIN resolver (factura
    /// destino/monto en null — el estado "legacy" que la pantalla nueva tiene que poder resolver de a una).
    /// La reserva tiene una factura de venta VIVA en cada moneda.
    /// </summary>
    private static async Task<(
        Reserva Reserva,
        Invoice InvoiceUsd,
        Invoice InvoiceArs,
        BookingCancellation Bc,
        BookingCancellationLine LineHotelUsd,
        BookingCancellationLine LineExcursionArs,
        Supplier Supplier)>
        SeedTwoPendingPartialLinesAsync(AppDbContext ctx)
    {
        ctx.AfipSettings.Add(new AfipSettings { TaxCondition = "Monotributo", Cuit = 20111111111 });

        var customer = new Customer { FullName = "Cliente T5 Multi", IsActive = true, TaxCondition = "Consumidor Final" };
        var supplier = new Supplier { Name = "Turismo Cardozo", IsActive = true, TaxCondition = "IVA_RESP_INSCRIPTO" };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-T5-MULTI",
            Name = "Reserva T5 dos servicios pendientes",
            PayerId = customer.Id,
            Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var invoiceUsd = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 700, CAE = "cae-usd", Resultado = "A",
            MonId = "DOL", MonCotiz = 1000m, ImporteTotal = 700m, ImporteNeto = 700m, ImporteIva = 0m,
            ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None, CreatedAt = DateTime.UtcNow,
            // Factura en moneda extranjera: la emision hereda el TC/origen de ESTA factura (nunca recotiza),
            // asi que necesita su ExchangeRateSource cargado (misma exigencia que el resto del modulo).
            ExchangeRateSource = ExchangeRateSource.BNA_VendedorDivisa,
        };
        var invoiceArs = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 701, CAE = "cae-ars", Resultado = "A",
            MonId = "PES", MonCotiz = 1m, ImporteTotal = 720_000m, ImporteNeto = 720_000m, ImporteIva = 0m,
            ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None, CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.AddRange(invoiceUsd, invoiceArs);
        await ctx.SaveChangesAsync();
        ctx.Set<InvoiceItem>().AddRange(
            new InvoiceItem { InvoiceId = invoiceUsd.Id, Description = "Hotel", Quantity = 1, UnitPrice = 700m, Total = 700m, AlicuotaIvaId = 3 },
            new InvoiceItem { InvoiceId = invoiceArs.Id, Description = "Excursion", Quantity = 1, UnitPrice = 720_000m, Total = 720_000m, AlicuotaIvaId = 3 });
        await ctx.SaveChangesAsync();

        var hotel = new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            Currency = "USD",
            SalePrice = 700m,
            HotelName = "Maitei",
            City = "Posadas",
            Nights = 3,
            Status = WorkflowStatuses.Cancelado,
        };
        var excursion = new ServicioReserva
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            Currency = "ARS",
            SalePrice = 720_000m,
            Description = "Excursión Cataratas",
            Status = WorkflowStatuses.Cancelado,
        };
        ctx.HotelBookings.Add(hotel);
        ctx.Servicios.Add(excursion);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = invoiceUsd.Id,
            Status = BookingCancellationStatus.Drafted,
            Reason = "Cancelacion de dos servicios (hotel USD + excursion ARS)",
            DraftedAt = DateTime.UtcNow,
            DraftedByUserId = "vendedor-1",
            FiscalSnapshot = new FiscalSnapshot { Source = ExchangeRateSource.Unset, FetchedAt = default },
        };
        var lineHotelUsd = new BookingCancellationLine
        {
            SupplierId = supplier.Id,
            ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = hotel.Id,
            Scope = BookingCancellationLineScope.Partial,
            Currency = "USD",
            LineSaleAmount = 700m,
        };
        var lineExcursionArs = new BookingCancellationLine
        {
            SupplierId = supplier.Id,
            ServiceTable = CancellableServiceTable.Generic,
            ServiceId = excursion.Id,
            Scope = BookingCancellationLineScope.Partial,
            Currency = "ARS",
            LineSaleAmount = 720_000m,
        };
        bc.Lines.Add(lineHotelUsd);
        bc.Lines.Add(lineExcursionArs);
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        return (reserva, invoiceUsd, invoiceArs, bc, lineHotelUsd, lineExcursionArs, supplier);
    }

    /// <summary>
    /// Mockea <c>InvoiceService.CreateAsync</c> para que inserte una Invoice NC PENDING real en la BD InMemory,
    /// resolviendo la factura ORIGEN por su <c>PublicId</c> (a diferencia del helper de un solo renglón, este
    /// soporta emitir contra CUALQUIERA de las facturas del seed — necesario para probar la emisión
    /// independiente por factura).
    /// </summary>
    private static void SetupCreateEmitsCreditNoteForAnyInvoice(Harness h)
    {
        h.InvoiceMock
            .Setup(s => s.CreateAsync(
                It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateInvoiceRequest req, string? uid, string? uname, CancellationToken ct) =>
            {
                var originalInvoicePublicId = Guid.Parse(req.OriginalInvoiceId!);
                var originalInvoice = h.Ctx.Invoices.Single(i => i.PublicId == originalInvoicePublicId);
                var nc = new Invoice
                {
                    TipoComprobante = 13, // NC C (espeja la Factura C original)
                    PuntoDeVenta = 1,
                    NumeroComprobante = 900 + h.Ctx.Invoices.Count(),
                    Resultado = "PENDING",
                    MonId = req.MonId,
                    MonCotiz = req.MonCotiz,
                    ImporteTotal = req.TotalsOverride?.ImpTotal ?? req.Items.Sum(i => i.Total),
                    ImporteNeto = req.TotalsOverride?.ImpNeto ?? 0m,
                    ImporteIva = req.TotalsOverride?.ImpIVA ?? 0m,
                    ReservaId = originalInvoice.ReservaId,
                    OriginalInvoiceId = originalInvoice.Id,
                    CreatedAt = DateTime.UtcNow,
                };
                h.Ctx.Invoices.Add(nc);
                h.Ctx.SaveChanges();
                return new InvoiceDto { PublicId = nc.PublicId };
            });
    }

    /// <summary>
    /// Borde D1 (review 2026-07-17): dos servicios cancelados en la MISMA moneda (a diferencia del seed de
    /// arriba, que las usa distintas a propósito para no chocar con este guard), contra UNA sola factura de
    /// venta viva en esa moneda. Sirve para probar la mitigación: no se puede emitir la devolución de un
    /// renglón mientras quede OTRO sin resolver en la misma moneda (podría corresponder a esa misma factura).
    /// </summary>
    private static async Task<(
        Invoice Invoice, BookingCancellation Bc, BookingCancellationLine Line1, BookingCancellationLine Line2)>
        SeedTwoPendingPartialLinesSameCurrencyAsync(AppDbContext ctx, decimal invoiceTotal = 100_000m)
    {
        ctx.AfipSettings.Add(new AfipSettings { TaxCondition = "Monotributo", Cuit = 20111111111 });

        var customer = new Customer { FullName = "Cliente D1", IsActive = true, TaxCondition = "Consumidor Final" };
        var supplier = new Supplier { Name = "Operador D1", IsActive = true, TaxCondition = "IVA_RESP_INSCRIPTO" };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-T5-D1",
            Name = "Reserva D1 dos servicios misma moneda",
            PayerId = customer.Id,
            Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var invoice = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 800, CAE = "cae-d1", Resultado = "A",
            MonId = "PES", MonCotiz = 1m, ImporteTotal = invoiceTotal, ImporteNeto = invoiceTotal, ImporteIva = 0m,
            ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None, CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();
        ctx.Set<InvoiceItem>().Add(new InvoiceItem
        {
            InvoiceId = invoice.Id, Description = "Servicios", Quantity = 1,
            UnitPrice = invoiceTotal, Total = invoiceTotal, AlicuotaIvaId = 3,
        });
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id,
            Status = BookingCancellationStatus.Drafted,
            Reason = "Cancelacion de dos servicios en la misma moneda",
            DraftedAt = DateTime.UtcNow,
            DraftedByUserId = "vendedor-1",
            FiscalSnapshot = new FiscalSnapshot { Source = ExchangeRateSource.Unset, FetchedAt = default },
        };
        var line1 = new BookingCancellationLine
        {
            SupplierId = supplier.Id, ServiceTable = CancellableServiceTable.Hotel, ServiceId = 1,
            Scope = BookingCancellationLineScope.Partial, Currency = "ARS", LineSaleAmount = 30_000m,
        };
        var line2 = new BookingCancellationLine
        {
            SupplierId = supplier.Id, ServiceTable = CancellableServiceTable.Generic, ServiceId = 2,
            Scope = BookingCancellationLineScope.Partial, Currency = "ARS", LineSaleAmount = 40_000m,
        };
        bc.Lines.Add(line1);
        bc.Lines.Add(line2);
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        return (invoice, bc, line1, line2);
    }

    // =====================================================================================
    // Resolver de a uno — el guard aflojado (INV-T5-RESOLVE-STATE ya no exige "un unico servicio").
    // =====================================================================================

    [Fact]
    public async Task Resolve_TwoPending_WithoutLineId_RejectsAsAmbiguous()
    {
        using var ctx = NewDbContext();
        var (_, invoiceUsd, _, bc, _, _, _) = await SeedTwoPendingPartialLinesAsync(ctx);
        var h = BuildService(ctx);

        var request = new ResolvePartialCreditNoteRequest(invoiceUsd.PublicId, 700m, "Devolucion del hotel en dolares");

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ResolvePartialCreditNoteAsync(bc.PublicId, request, "u1", "Usuario Uno", CancellationToken.None));

        Assert.Equal("INV-T5-RESOLVE-AMBIGUOUS", ex.InvariantCode);
    }

    [Fact]
    public async Task Resolve_TwoPending_ResolvesOneByLineId_OtherLineStaysUntouched()
    {
        using var ctx = NewDbContext();
        var (_, invoiceUsd, _, bc, lineHotelUsd, lineExcursionArs, _) =
            await SeedTwoPendingPartialLinesAsync(ctx);
        var h = BuildService(ctx);

        var request = new ResolvePartialCreditNoteRequest(
            invoiceUsd.PublicId, 700m, "Devolucion del hotel en dolares",
            BookingCancellationLinePublicId: lineHotelUsd.PublicId);

        await h.Service.ResolvePartialCreditNoteAsync(bc.PublicId, request, "u1", "Usuario Uno", CancellationToken.None);

        await ctx.Entry(lineHotelUsd).ReloadAsync();
        await ctx.Entry(lineExcursionArs).ReloadAsync();
        Assert.Equal(invoiceUsd.Id, lineHotelUsd.TargetInvoiceId);
        Assert.Equal(700m, lineHotelUsd.ConfirmedGrossCreditAmount);
        // La excursion (la OTRA linea) queda EXACTAMENTE como estaba: nadie la tocó.
        Assert.Null(lineExcursionArs.TargetInvoiceId);
        Assert.Null(lineExcursionArs.ConfirmedGrossCreditAmount);
    }

    [Fact]
    public async Task Resolve_TwoPending_ResolvesSecondLineAfterFirst_BothEndUpResolved()
    {
        using var ctx = NewDbContext();
        var (_, invoiceUsd, invoiceArs, bc, lineHotelUsd, lineExcursionArs, _) =
            await SeedTwoPendingPartialLinesAsync(ctx);
        var h = BuildService(ctx);

        await h.Service.ResolvePartialCreditNoteAsync(
            bc.PublicId,
            new ResolvePartialCreditNoteRequest(invoiceUsd.PublicId, 700m, "Devolucion del hotel en dolares",
                BookingCancellationLinePublicId: lineHotelUsd.PublicId),
            "u1", "Usuario Uno", CancellationToken.None);

        await h.Service.ResolvePartialCreditNoteAsync(
            bc.PublicId,
            new ResolvePartialCreditNoteRequest(invoiceArs.PublicId, 720_000m, "Devolucion de la excursion en pesos",
                BookingCancellationLinePublicId: lineExcursionArs.PublicId),
            "u1", "Usuario Uno", CancellationToken.None);

        await ctx.Entry(lineHotelUsd).ReloadAsync();
        await ctx.Entry(lineExcursionArs).ReloadAsync();
        Assert.Equal(invoiceUsd.Id, lineHotelUsd.TargetInvoiceId);
        Assert.Equal(invoiceArs.Id, lineExcursionArs.TargetInvoiceId);
        Assert.Equal(720_000m, lineExcursionArs.ConfirmedGrossCreditAmount);
    }

    [Fact]
    public async Task Resolve_OnePending_NoLineIdGiven_StillResolvesTheSingleOne_BackwardCompat()
    {
        // Compatibilidad con el formulario viejo: con UN solo renglon pendiente, seguir sin exigir el Id.
        // Dejamos la excursion YA resuelta Y EMITIDA (Succeeded) — el escenario real de "ya se emitio una,
        // falta la otra" — para que quede UNA sola linea elegible de verdad.
        using var ctx = NewDbContext();
        var (_, invoiceUsd, invoiceArs, bc, lineHotelUsd, lineExcursionArs, _) =
            await SeedTwoPendingPartialLinesAsync(ctx);
        lineExcursionArs.TargetInvoiceId = invoiceArs.Id;
        lineExcursionArs.ConfirmedGrossCreditAmount = 720_000m;
        ctx.BookingCancellationCreditNotes.Add(new BookingCancellationCreditNote
        {
            BookingCancellationId = bc.Id,
            OriginatingInvoiceId = invoiceArs.Id,
            ArcaCurrency = "PES",
            Status = BookingCancellationCreditNoteStatus.Succeeded,
        });
        await ctx.SaveChangesAsync();

        var h = BuildService(ctx);
        var request = new ResolvePartialCreditNoteRequest(invoiceUsd.PublicId, 700m, "Devolucion del hotel en dolares");

        await h.Service.ResolvePartialCreditNoteAsync(bc.PublicId, request, "u1", "Usuario Uno", CancellationToken.None);

        await ctx.Entry(lineHotelUsd).ReloadAsync();
        Assert.Equal(invoiceUsd.Id, lineHotelUsd.TargetInvoiceId);
    }

    [Fact]
    public async Task Resolve_WrongCurrencyInvoice_RejectsWithCurrencyMismatch()
    {
        // La moneda la manda LA FACTURA: el hotel es en dolares, no se puede resolver contra la factura ARS.
        using var ctx = NewDbContext();
        var (_, _, invoiceArs, bc, lineHotelUsd, _, _) = await SeedTwoPendingPartialLinesAsync(ctx);
        var h = BuildService(ctx);

        var request = new ResolvePartialCreditNoteRequest(
            invoiceArs.PublicId, 700m, "Intento de resolver el hotel contra la factura en pesos",
            BookingCancellationLinePublicId: lineHotelUsd.PublicId);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ResolvePartialCreditNoteAsync(bc.PublicId, request, "u1", "Usuario Uno", CancellationToken.None));

        Assert.Equal("INV-T5-RESOLVE-CURRENCY", ex.InvariantCode);
    }

    [Fact]
    public async Task Resolve_LineNotBelongingToThisBc_RejectsClearly()
    {
        using var ctx = NewDbContext();
        var (_, invoiceUsd, _, bc, _, _, _) = await SeedTwoPendingPartialLinesAsync(ctx);
        var h = BuildService(ctx);

        var request = new ResolvePartialCreditNoteRequest(
            invoiceUsd.PublicId, 700m, "Renglon que no existe en esta cancelacion",
            BookingCancellationLinePublicId: Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ResolvePartialCreditNoteAsync(bc.PublicId, request, "u1", "Usuario Uno", CancellationToken.None));

        Assert.Equal("INV-T5-RESOLVE-LINE", ex.InvariantCode);
    }

    [Fact]
    public async Task Resolve_LineAlreadyEmitted_RejectsClearly()
    {
        using var ctx = NewDbContext();
        var (_, invoiceUsd, _, bc, lineHotelUsd, _, _) = await SeedTwoPendingPartialLinesAsync(ctx);
        lineHotelUsd.TargetInvoiceId = invoiceUsd.Id;
        lineHotelUsd.ConfirmedGrossCreditAmount = 700m;
        ctx.BookingCancellationCreditNotes.Add(new BookingCancellationCreditNote
        {
            BookingCancellationId = bc.Id,
            OriginatingInvoiceId = invoiceUsd.Id,
            ArcaCurrency = "DOL",
            Status = BookingCancellationCreditNoteStatus.Succeeded,
        });
        await ctx.SaveChangesAsync();
        var h = BuildService(ctx);

        var request = new ResolvePartialCreditNoteRequest(
            invoiceUsd.PublicId, 700m, "Intento de cambiar una devolucion ya emitida",
            BookingCancellationLinePublicId: lineHotelUsd.PublicId);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ResolvePartialCreditNoteAsync(bc.PublicId, request, "u1", "Usuario Uno", CancellationToken.None));

        Assert.Equal("INV-T5-RESOLVE-FISCAL", ex.InvariantCode);
    }

    [Fact]
    public async Task Resolve_BcHasFullLineMixed_RejectsOutOfScope()
    {
        using var ctx = NewDbContext();
        var (_, invoiceUsd, _, bc, lineHotelUsd, _, supplier) = await SeedTwoPendingPartialLinesAsync(ctx);
        bc.Lines.Add(new BookingCancellationLine
        {
            SupplierId = supplier.Id,
            ServiceTable = CancellableServiceTable.Generic,
            ServiceId = 0,
            Scope = BookingCancellationLineScope.Full,
            Currency = "ARS",
            LineSaleAmount = 1000m,
        });
        await ctx.SaveChangesAsync();
        var h = BuildService(ctx);

        var request = new ResolvePartialCreditNoteRequest(
            invoiceUsd.PublicId, 700m, "Cancelacion mezclada con anulacion total",
            BookingCancellationLinePublicId: lineHotelUsd.PublicId);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ResolvePartialCreditNoteAsync(bc.PublicId, request, "u1", "Usuario Uno", CancellationToken.None));

        Assert.Equal("INV-T5-RESOLVE-STATE", ex.InvariantCode);
    }

    // =====================================================================================
    // Emision independiente por factura — el fix de PartialCreditNoteT5Reconciliation (el BC se queda en
    // Drafted mientras queden OTRAS lineas sin su devolucion emitida; recien avanza a AwaitingOperatorRefund
    // cuando la ULTIMA queda Succeeded). Sin este fix, emitir la primera dejaba la segunda huerfana para siempre.
    // =====================================================================================

    [Fact]
    public async Task Emit_OneOfTwoResolvedLines_EmitsOnlyThatInvoice_OtherLineStaysPending()
    {
        using var ctx = NewDbContext();
        var (_, invoiceUsd, invoiceArs, bc, lineHotelUsd, lineExcursionArs, _) =
            await SeedTwoPendingPartialLinesAsync(ctx);
        lineHotelUsd.TargetInvoiceId = invoiceUsd.Id;
        lineHotelUsd.ConfirmedGrossCreditAmount = 700m;
        await ctx.SaveChangesAsync();
        // La excursion (ARS) sigue SIN resolver: antes de esta tanda, esto bloqueaba la emision ENTERA
        // (INV-T5-EMIT-UNRESOLVED exigia TODAS las lineas resueltas). Ahora alcanza con que haya UNA lista.

        var h = BuildService(ctx);
        SetupCreateEmitsCreditNoteForAnyInvoice(h);

        var dto = await h.Service.ConfirmPartialCancellationEmissionAsync(
            bc.PublicId, "u1", "U", CancellationToken.None,
            new EmitPartialCreditNoteRequest(invoiceUsd.PublicId));

        Assert.Equal("AwaitingFiscalConfirmation", dto.Status);
        var childrenAfterFirstEmit = await ctx.BookingCancellationCreditNotes.AsNoTracking()
            .Where(c => c.BookingCancellationId == bc.Id).ToListAsync();
        var child = Assert.Single(childrenAfterFirstEmit);
        Assert.Equal(invoiceUsd.Id, child.OriginatingInvoiceId);

        // La excursion sigue intacta (sin factura ni monto resueltos): nadie la toco al emitir el hotel.
        await ctx.Entry(lineExcursionArs).ReloadAsync();
        Assert.Null(lineExcursionArs.TargetInvoiceId);
    }

    [Fact]
    public async Task Emit_TwoResolvedLinesToDifferentInvoices_EmittedSeparately_BcOnlyAdvancesWhenBothAreDone()
    {
        using var ctx = NewDbContext();
        var (_, invoiceUsd, invoiceArs, bc, lineHotelUsd, lineExcursionArs, _) =
            await SeedTwoPendingPartialLinesAsync(ctx);
        lineHotelUsd.TargetInvoiceId = invoiceUsd.Id;
        lineHotelUsd.ConfirmedGrossCreditAmount = 700m;
        lineExcursionArs.TargetInvoiceId = invoiceArs.Id;
        lineExcursionArs.ConfirmedGrossCreditAmount = 720_000m;
        await ctx.SaveChangesAsync();

        var h = BuildService(ctx);
        SetupCreateEmitsCreditNoteForAnyInvoice(h);

        // Emitir la primera (hotel USD) y simular que ARCA la aprueba.
        await h.Service.ConfirmPartialCancellationEmissionAsync(
            bc.PublicId, "u1", "U", CancellationToken.None, new EmitPartialCreditNoteRequest(invoiceUsd.PublicId));
        var ncUsd = await ctx.Invoices.SingleAsync(i => i.OriginalInvoiceId == invoiceUsd.Id);
        ncUsd.Resultado = "A";
        ncUsd.CAE = "cae-nc-usd";
        await ctx.SaveChangesAsync();
        await PartialCreditNoteT5Reconciliation.TryReconcileAsync(
            ctx, ncUsd, auditService: null, NullLogger.Instance, CancellationToken.None);

        // CLAVE del fix: como la excursion (ARS) TODAVIA no tiene su devolucion emitida, el BC se queda en
        // Drafted — si no, quedaria huerfana para siempre (el guard de resolver/emitir exige Drafted).
        var bcAfterFirst = await ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.Drafted, bcAfterFirst.Status);

        // Emitir la segunda (excursion ARS) y simular su aprobacion.
        await h.Service.ConfirmPartialCancellationEmissionAsync(
            bc.PublicId, "u1", "U", CancellationToken.None, new EmitPartialCreditNoteRequest(invoiceArs.PublicId));
        var ncArs = await ctx.Invoices.SingleAsync(i => i.OriginalInvoiceId == invoiceArs.Id);
        ncArs.Resultado = "A";
        ncArs.CAE = "cae-nc-ars";
        await ctx.SaveChangesAsync();
        await PartialCreditNoteT5Reconciliation.TryReconcileAsync(
            ctx, ncArs, auditService: null, NullLogger.Instance, CancellationToken.None);

        // Ahora si: las DOS devoluciones estan emitidas, el BC avanza por su circuito.
        var bcAfterBoth = await ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, bcAfterBoth.Status);

        var children = await ctx.BookingCancellationCreditNotes.AsNoTracking()
            .Where(c => c.BookingCancellationId == bc.Id).ToListAsync();
        Assert.Equal(2, children.Count);
        Assert.All(children, c => Assert.Equal(BookingCancellationCreditNoteStatus.Succeeded, c.Status));
    }

    [Fact]
    public async Task Emit_TwoResolvedLines_WithoutIndicatingInvoice_RejectsAskingWhich()
    {
        // Compatibilidad: sin indicar la factura y con 2+ pendientes, el server NO adivina — pide precisar.
        using var ctx = NewDbContext();
        var (_, invoiceUsd, invoiceArs, bc, lineHotelUsd, lineExcursionArs, _) =
            await SeedTwoPendingPartialLinesAsync(ctx);
        lineHotelUsd.TargetInvoiceId = invoiceUsd.Id;
        lineHotelUsd.ConfirmedGrossCreditAmount = 700m;
        lineExcursionArs.TargetInvoiceId = invoiceArs.Id;
        lineExcursionArs.ConfirmedGrossCreditAmount = 720_000m;
        await ctx.SaveChangesAsync();

        var h = BuildService(ctx);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ConfirmPartialCancellationEmissionAsync(bc.PublicId, "u1", "U", CancellationToken.None));

        Assert.Equal("INV-T5-EMIT-MULTI-INVOICE", ex.InvariantCode);
    }

    // =====================================================================================
    // Datos que expone el DTO nuevo (PartialCreditNoteEmission.Lines) para la pantalla nueva.
    // =====================================================================================

    [Fact]
    public async Task Dto_ExposesOneLinePerPendingService_WithRealNamesAndSuggestedAmounts()
    {
        using var ctx = NewDbContext();
        var (_, invoiceUsd, _, bc, lineHotelUsd, lineExcursionArs, _) = await SeedTwoPendingPartialLinesAsync(ctx);
        // Resolvemos solo el hotel para verificar que la fila resuelta y la pendiente se distinguen bien.
        lineHotelUsd.TargetInvoiceId = invoiceUsd.Id;
        lineHotelUsd.ConfirmedGrossCreditAmount = 700m;
        await ctx.SaveChangesAsync();
        var h = BuildService(ctx);

        var dto = await h.Service.GetByPublicIdAsync(bc.PublicId, CancellationToken.None);

        Assert.NotNull(dto?.PartialCreditNoteEmission);
        var lines = dto!.PartialCreditNoteEmission!.Lines;
        Assert.Equal(2, lines.Count);

        var hotelRow = Assert.Single(lines, l => l.LinePublicId == lineHotelUsd.PublicId);
        Assert.Equal("Hotel Maitei (3 noches)", hotelRow.ServiceName);
        Assert.Equal("Turismo Cardozo", hotelRow.SupplierName);
        Assert.Equal("USD", hotelRow.Currency);
        Assert.Equal(700m, hotelRow.SuggestedAmount);
        Assert.True(hotelRow.IsResolved);
        Assert.Equal(invoiceUsd.PublicId, hotelRow.TargetInvoicePublicId);
        Assert.False(string.IsNullOrWhiteSpace(hotelRow.TargetInvoiceLabel));

        var excursionRow = Assert.Single(lines, l => l.LinePublicId == lineExcursionArs.PublicId);
        Assert.Equal("Excursión Cataratas", excursionRow.ServiceName);
        Assert.Equal("ARS", excursionRow.Currency);
        Assert.Equal(720_000m, excursionRow.SuggestedAmount);
        Assert.False(excursionRow.IsResolved);
        Assert.Null(excursionRow.TargetInvoicePublicId);
    }

    // =====================================================================================
    // T1 (review): emitir la linea 1 con la linea 2 SIN resolver — el reconciliador NO debe avanzar el BC
    // (sino la linea 2 queda huerfana para siempre, ver el fix de PartialCreditNoteT5Reconciliation).
    // =====================================================================================

    [Fact]
    public async Task Reconciler_FirstOfTwo_SecondLineStillUnresolved_BcStaysDrafted_DoesNotAdvance()
    {
        using var ctx = NewDbContext();
        var (_, invoiceUsd, _, bc, lineHotelUsd, lineExcursionArs, _) = await SeedTwoPendingPartialLinesAsync(ctx);
        lineHotelUsd.TargetInvoiceId = invoiceUsd.Id;
        lineHotelUsd.ConfirmedGrossCreditAmount = 700m;
        // lineExcursionArs queda SIN resolver (TargetInvoiceId null). Moneda distinta (ARS) para no chocar
        // con el guard D1 (que exige resolver primero lo que comparte moneda con la factura a emitir).
        await ctx.SaveChangesAsync();
        var h = BuildService(ctx);
        SetupCreateEmitsCreditNoteForAnyInvoice(h);

        await h.Service.ConfirmPartialCancellationEmissionAsync(
            bc.PublicId, "u1", "U", CancellationToken.None, new EmitPartialCreditNoteRequest(invoiceUsd.PublicId));
        var nc = await ctx.Invoices.SingleAsync(i => i.OriginalInvoiceId == invoiceUsd.Id);
        nc.Resultado = "A";
        nc.CAE = "cae-nc-usd";
        await ctx.SaveChangesAsync();

        var reconciled = await PartialCreditNoteT5Reconciliation.TryReconcileAsync(
            ctx, nc, auditService: null, NullLogger.Instance, CancellationToken.None);
        Assert.True(reconciled);

        var bcAfter = await ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.Drafted, bcAfter.Status);
        // No avanzo por su circuito: el plazo del reembolso del operador no se seteo todavia.
        Assert.Null(bcAfter.OperatorRefundDueBy);

        // La excursion (linea 2) sigue exactamente como estaba: sin resolver, intacta.
        await ctx.Entry(lineExcursionArs).ReloadAsync();
        Assert.Null(lineExcursionArs.TargetInvoiceId);
        Assert.Null(lineExcursionArs.ConfirmedGrossCreditAmount);
    }

    // =====================================================================================
    // T2 (review): rechazo de ARCA en un evento multi-linea — la hija 1 queda Failed, el BC vuelve a Drafted,
    // y la linea 2 (que nunca se toco) sigue intacta y resoluble desde cero.
    // =====================================================================================

    [Fact]
    public async Task Reconciler_FirstOfTwoRejectedByArca_BcBackToDrafted_SecondLineStillResolvable()
    {
        using var ctx = NewDbContext();
        var (_, invoiceUsd, invoiceArs, bc, lineHotelUsd, lineExcursionArs, _) =
            await SeedTwoPendingPartialLinesAsync(ctx);
        lineHotelUsd.TargetInvoiceId = invoiceUsd.Id;
        lineHotelUsd.ConfirmedGrossCreditAmount = 700m;
        await ctx.SaveChangesAsync();
        var h = BuildService(ctx);
        SetupCreateEmitsCreditNoteForAnyInvoice(h);

        await h.Service.ConfirmPartialCancellationEmissionAsync(
            bc.PublicId, "u1", "U", CancellationToken.None, new EmitPartialCreditNoteRequest(invoiceUsd.PublicId));
        var nc = await ctx.Invoices.SingleAsync(i => i.OriginalInvoiceId == invoiceUsd.Id);
        nc.Resultado = "R";
        nc.Observaciones = "CUIT invalido (simulado)";
        await ctx.SaveChangesAsync();

        var reconciled = await PartialCreditNoteT5Reconciliation.TryReconcileAsync(
            ctx, nc, auditService: null, NullLogger.Instance, CancellationToken.None);
        Assert.True(reconciled);

        var bcAfter = await ctx.BookingCancellations.Include(b => b.CreditNotes).AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.Drafted, bcAfter.Status);
        var child = Assert.Single(bcAfter.CreditNotes);
        Assert.Equal(BookingCancellationCreditNoteStatus.Failed, child.Status);

        // La excursion (linea 2) nunca se toco: sigue sin resolver.
        await ctx.Entry(lineExcursionArs).ReloadAsync();
        Assert.Null(lineExcursionArs.TargetInvoiceId);

        // Y se puede resolver ahora, desde cero (el rechazo de la 1 no la afecto).
        var resolveRequest = new ResolvePartialCreditNoteRequest(
            invoiceArs.PublicId, 720_000m, "Devolucion de la excursion en pesos",
            BookingCancellationLinePublicId: lineExcursionArs.PublicId);
        await h.Service.ResolvePartialCreditNoteAsync(bc.PublicId, resolveRequest, "u1", "Usuario Uno", CancellationToken.None);

        await ctx.Entry(lineExcursionArs).ReloadAsync();
        Assert.Equal(invoiceArs.Id, lineExcursionArs.TargetInvoiceId);
    }

    // =====================================================================================
    // Borde D1 (review 2026-07-17): no dejar emitir mientras quede un renglon SIN resolver de la MISMA
    // moneda que la factura — evita la huerfana "misma factura" (la hija es unica por factura+BC).
    // =====================================================================================

    [Fact]
    public async Task Emit_UnresolvedSiblingLineSameCurrency_RejectsWithSiblingUnresolved()
    {
        using var ctx = NewDbContext();
        var (invoice, bc, line1, line2) = await SeedTwoPendingPartialLinesSameCurrencyAsync(ctx);
        line1.TargetInvoiceId = invoice.Id;
        line1.ConfirmedGrossCreditAmount = 30_000m;
        // line2 (misma moneda, ARS) queda SIN resolver: podria terminar correspondiendo a esta MISMA factura.
        await ctx.SaveChangesAsync();
        var h = BuildService(ctx);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ConfirmPartialCancellationEmissionAsync(
                bc.PublicId, "u1", "U", CancellationToken.None, new EmitPartialCreditNoteRequest(invoice.PublicId)));

        Assert.Equal("INV-T5-EMIT-SIBLING-UNRESOLVED", ex.InvariantCode);
    }

    [Fact]
    public async Task Emit_AfterResolvingSiblingSameCurrencyLine_ProceedsNormally()
    {
        using var ctx = NewDbContext();
        var (invoice, bc, line1, line2) = await SeedTwoPendingPartialLinesSameCurrencyAsync(ctx);
        line1.TargetInvoiceId = invoice.Id;
        line1.ConfirmedGrossCreditAmount = 30_000m;
        line2.TargetInvoiceId = invoice.Id;
        line2.ConfirmedGrossCreditAmount = 40_000m;
        await ctx.SaveChangesAsync();
        var h = BuildService(ctx);
        SetupCreateEmitsCreditNoteForAnyInvoice(h);

        var dto = await h.Service.ConfirmPartialCancellationEmissionAsync(
            bc.PublicId, "u1", "U", CancellationToken.None, new EmitPartialCreditNoteRequest(invoice.PublicId));

        Assert.Equal("AwaitingFiscalConfirmation", dto.Status);
        // Las dos lineas resolvieron a la MISMA factura: se emiten JUNTAS, una sola hija.
        var child = await ctx.BookingCancellationCreditNotes.AsNoTracking()
            .SingleAsync(c => c.BookingCancellationId == bc.Id);
        Assert.Equal(invoice.Id, child.OriginatingInvoiceId);
    }

    // =====================================================================================
    // DECISIÓN DEL DUEÑO (2026-07-17): bloquear Abort una vez que ESTE BC ya emitio alguna devolucion
    // (hija NC Succeeded), aunque siga Drafted (caso T5 varios pendientes). El Abort normal (Drafted sin
    // ninguna emision) sigue funcionando igual que siempre.
    // =====================================================================================

    [Fact]
    public async Task Abort_BcWithSucceededChildNc_RejectsEvenIfStillDrafted()
    {
        using var ctx = NewDbContext();
        var (_, invoiceUsd, _, bc, lineHotelUsd, lineExcursionArs, _) = await SeedTwoPendingPartialLinesAsync(ctx);
        lineHotelUsd.TargetInvoiceId = invoiceUsd.Id;
        lineHotelUsd.ConfirmedGrossCreditAmount = 700m;
        await ctx.SaveChangesAsync();
        var h = BuildService(ctx);
        SetupCreateEmitsCreditNoteForAnyInvoice(h);

        await h.Service.ConfirmPartialCancellationEmissionAsync(
            bc.PublicId, "u1", "U", CancellationToken.None, new EmitPartialCreditNoteRequest(invoiceUsd.PublicId));
        var nc = await ctx.Invoices.SingleAsync(i => i.OriginalInvoiceId == invoiceUsd.Id);
        nc.Resultado = "A";
        nc.CAE = "cae-nc-usd";
        await ctx.SaveChangesAsync();
        await PartialCreditNoteT5Reconciliation.TryReconcileAsync(
            ctx, nc, auditService: null, NullLogger.Instance, CancellationToken.None);

        var bcAfterEmission = await ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.Drafted, bcAfterEmission.Status); // sigue Drafted, falta la excursion

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.AbortAsync(bc.PublicId, "Motivo de prueba", "u1", CancellationToken.None));

        Assert.Equal("INV-T5-ABORT-ALREADY-EMITTED", ex.InvariantCode);

        // El BC NO se toco: sigue Drafted, la excursion sigue sin resolver.
        var bcAfterAbortAttempt = await ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.Drafted, bcAfterAbortAttempt.Status);
        await ctx.Entry(lineExcursionArs).ReloadAsync();
        Assert.Null(lineExcursionArs.TargetInvoiceId);
    }

    [Fact]
    public async Task Abort_DraftedWithoutAnyEmission_StillWorksAsAlways()
    {
        using var ctx = NewDbContext();
        var (_, _, _, bc, _, _, _) = await SeedTwoPendingPartialLinesAsync(ctx);
        var h = BuildService(ctx);

        var dto = await h.Service.AbortAsync(bc.PublicId, "Se decidio no seguir con la devolucion", "u1", CancellationToken.None);

        Assert.Equal("Aborted", dto.Status);
    }
}
