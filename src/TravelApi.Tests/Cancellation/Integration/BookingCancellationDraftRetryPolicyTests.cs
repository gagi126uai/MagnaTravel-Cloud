using System;
using System.Collections.Generic;
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
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// B1 (2026-06-03): tests de la POLITICA DE REINTENTO de <c>DraftAsync</c> (INV-081).
/// Cubren los 4 caminos de resolucion de un BC preexistente + el indice UNIQUE parcial:
/// <list type="bullet">
///   <item>(a) draft puro -> reusa la misma fila (idempotente, mismo PublicId).</item>
///   <item>(b) Aborted preexistente -> crea BC nuevo sin colisionar.</item>
///   <item>(c) ArcaRejected SIN NC viva -> auto-aborta + crea BC nuevo (FIX 1).</item>
///       ArcaRejected CON NC viva -> rechaza INV-081 (blindaje fiscal).</item>
///   <item>(d) estados activos/fiscales -> rechazan INV-081.</item>
///   <item>colision del UNIQUE parcial sobre OriginatingInvoiceId, no solo ReservaId.</item>
/// </list>
///
/// <para>
/// <b>Por que Postgres real y no InMemory</b> (advertencia critica del reviewer): el
/// nucleo del fix es el INDICE UNIQUE PARCIAL con filtro <c>"Status" &lt;&gt; 6</c>.
/// EF InMemory NO respeta filtros de indice parcial: dejaria pasar INSERTs que en
/// Postgres real colisionan, dando falsos verdes justo en la logica que estamos
/// protegiendo. Por eso heredamos de <see cref="PostgresIntegrationFixture"/>.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class BookingCancellationDraftRetryPolicyTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public BookingCancellationDraftRetryPolicyTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // =========================================================================
    // Armado del service (mismo patron que BookingCancellationSupplierInferenceTests)
    // =========================================================================

    /// <summary>
    /// Arma el service contra Postgres real. Devolvemos tambien el mock de
    /// IAuditService para poder verificar que los caminos nuevos (reuse / auto-abort)
    /// dejan traza de auditoria de negocio.
    /// </summary>
    private (BookingCancellationService service, AppDbContext ctx, Mock<IAuditService> auditMock) BuildService()
    {
        var ctx = _fixture.CreateDbContext();

        var invoiceMock = new Mock<IInvoiceService>();
        invoiceMock
            .Setup(s => s.EnqueueAnnulmentAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<int?>()))
            .Returns(Task.CompletedTask);

        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableNewCancellationFlow = true,
                OnePerReservaInvoicePolicy = true,
                OperatorRefundTimeoutDays = 60,
            });

        var approvalSettings = new Mock<IOperationalFinanceSettingsService>();
        approvalSettings
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
        var approvalService = new ApprovalRequestService(ctx, approvalSettings.Object);

        var auditMock = new Mock<IAuditService>();

        var service = new BookingCancellationService(
            ctx,
            invoiceMock.Object,
            approvalService,
            auditMock.Object,
            NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object,
            new Mock<IFiscalLiquidationCalculator>().Object,
            new Mock<IAdminUserCountService>().Object);

        return (service, ctx, auditMock);
    }

    // =========================================================================
    // Seed
    // =========================================================================

    /// <summary>
    /// Crea Customer + Supplier + Reserva con UN servicio tipado (para que la
    /// inferencia de operador resuelva) + Invoice activa. Devuelve los ids/PublicId
    /// que necesitan los tests.
    /// </summary>
    private static async Task<SeedResult> SeedReservaAsync(AppDbContext ctx)
    {
        var customer = new Customer
        {
            FullName = "Cliente Test",
            TaxCondition = "Consumidor Final",
            IsActive = true,
        };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        var supplier = new Supplier
        {
            Name = $"Operador {Guid.NewGuid().ToString("N")[..6]}",
            IsActive = true,
            TaxCondition = "IVA_RESP_INSCRIPTO",
        };
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = $"F-RET-{Guid.NewGuid().ToString("N")[..8]}",
            Name = "Reserva retry policy",
            Status = EstadoReserva.Confirmed,
            PayerId = customer.Id,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        // Servicio tipado mono-operador para que InferSingleSupplierIdAsync resuelva.
        ctx.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            HotelName = "Hotel Test",
            City = "Bariloche",
            CheckIn = DateTime.UtcNow.Date,
            CheckOut = DateTime.UtcNow.Date.AddDays(2),
            Nights = 2,
        });

        var invoice = new Invoice
        {
            TipoComprobante = 1,
            PuntoDeVenta = 1,
            NumeroComprobante = 1,
            ImporteTotal = 1000m,
            ImporteNeto = 826.45m,
            ImporteIva = 173.55m,
            ReservaId = reserva.Id,
            // Factura EMITIDA: DraftAsync cuenta como "activa" solo las que tienen CAE (fix 7abb84f). Sin CAE
            // rebota con "no tiene factura activa". El valor es irrelevante (solo se chequea que no este vacio).
            CAE = "68000000000000",
            Resultado = "A",
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        return new SeedResult(reserva.Id, reserva.PublicId, customer.Id, supplier.Id, invoice.Id);
    }

    private sealed record SeedResult(
        int ReservaId, Guid ReservaPublicId, int CustomerId, int SupplierId, int InvoiceId);

    /// <summary>
    /// Inserta un BC en un estado dado, opcionalmente con NC viva. Va por SQL EF
    /// directo (no por el service) para poder fabricar estados intermedios que el
    /// flujo normal no expone facilmente en un test.
    /// </summary>
    private static async Task<BookingCancellation> SeedBcAsync(
        AppDbContext ctx,
        SeedResult seed,
        BookingCancellationStatus status,
        int? creditNoteInvoiceId = null)
    {
        var bc = new BookingCancellation
        {
            ReservaId = seed.ReservaId,
            CustomerId = seed.CustomerId,
            SupplierId = seed.SupplierId,
            OriginatingInvoiceId = seed.InvoiceId,
            Status = status,
            CreditNoteInvoiceId = creditNoteInvoiceId,
            Reason = "Seed BC para test de politica de reintento.",
            DraftedAt = DateTime.UtcNow,
            DraftedByUserId = "seed-user",
            AmountPaidAtCancellation = 1000m,
            EstimatedRefundAmount = 1000m,
            ReceivedRefundAmount = 0m,
            // El CHECK chk_BookingCancellations_fiscalsnapshot_consistent exige un snapshot
            // COHERENTE (Source != Unset, TC > 0, Currency != null) para cualquier Status
            // fuera de Drafted/Aborted. Estos tests siembran BCs en estados intermedios, asi
            // que el snapshot va completo (ARS, TC=1) para no chocar contra el CHECK.
            FiscalSnapshot = new FiscalSnapshot
            {
                Source = ExchangeRateSource.Manual,
                ExchangeRateAtOriginalInvoice = 1m,
                CurrencyAtEvent = "ARS",
                ManualJustification = "Seed retry policy",
                FetchedAt = DateTime.UtcNow,
                AgencyTaxConditionAtEvent = "MONOTRIBUTISTA",
                SupplierTaxConditionAtEvent = "IVA_RESP_INSCRIPTO",
                CustomerTaxConditionAtEvent = "CONSUMIDOR_FINAL",
            },
            IsLegacyPreCancellationModel = false,
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();
        return bc;
    }

    /// <summary>Crea una NC "viva" (CAE aprobado) asociada a la factura original.</summary>
    private static async Task<int> SeedLiveCreditNoteAsync(AppDbContext ctx, SeedResult seed)
    {
        var nc = new Invoice
        {
            TipoComprobante = 3, // NC A
            PuntoDeVenta = 1,
            NumeroComprobante = 99,
            ImporteTotal = 1000m,
            ImporteNeto = 826.45m,
            ImporteIva = 173.55m,
            ReservaId = seed.ReservaId,
            OriginalInvoiceId = seed.InvoiceId,
            Resultado = "A",
            CAE = "70000000000000",
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(nc);
        await ctx.SaveChangesAsync();
        return nc.Id;
    }

    private static Task<BookingCancellationDto> DraftAsync(
        BookingCancellationService service, Guid reservaPublicId)
        => service.DraftAsync(
            new DraftCancellationRequest(reservaPublicId, "Cliente cambio de plan"),
            "user-vendor", "Vendedor Test", CancellationToken.None);

    // =========================================================================
    // Caso (a): reuse de draft puro
    // =========================================================================

    [Fact]
    public async Task DraftAsync_SegundoDraftSobreDraftPuro_ReusaMismaFila()
    {
        var (service, ctx, auditMock) = BuildService();
        var seed = await SeedReservaAsync(ctx);

        var first = await DraftAsync(service, seed.ReservaPublicId);
        var second = await DraftAsync(service, seed.ReservaPublicId);

        // Mismo PublicId => misma fila (idempotente).
        Assert.Equal(first.PublicId, second.PublicId);

        // No se creo una segunda fila.
        var count = await ctx.BookingCancellations.CountAsync(b => b.ReservaId == seed.ReservaId);
        Assert.Equal(1, count);

        // FIX 3: el reuse deja traza de auditoria de negocio.
        auditMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.BookingCancellationDraftReused,
            AuditActions.BookingCancellationEntityName,
            It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // =========================================================================
    // Caso (b): Aborted preexistente -> crea BC nuevo
    // =========================================================================

    [Fact]
    public async Task DraftAsync_SobreReservaConBcAborted_CreaBcNuevo_NoColisiona()
    {
        var (service, ctx, _) = BuildService();
        var seed = await SeedReservaAsync(ctx);
        var aborted = await SeedBcAsync(ctx, seed, BookingCancellationStatus.Aborted);

        var nuevo = await DraftAsync(service, seed.ReservaPublicId);

        Assert.NotEqual(aborted.PublicId, nuevo.PublicId);

        // La fila Aborted queda intacta (rastro de auditoria) y hay 2 filas en total.
        var abortedReloaded = await ctx.BookingCancellations.AsNoTracking()
            .FirstAsync(b => b.PublicId == aborted.PublicId);
        Assert.Equal(BookingCancellationStatus.Aborted, abortedReloaded.Status);

        var total = await ctx.BookingCancellations.CountAsync(b => b.ReservaId == seed.ReservaId);
        Assert.Equal(2, total);
    }

    // =========================================================================
    // Caso (d): estados no liberables -> INV-081
    //
    // ADR-044 T5 Addendum, Decision C (2026-07-11): Closed SALE de esta lista — un BC Closed es un evento
    // fiscal TERMINADO (NC con CAE, reembolso consumido), no una cancelacion "en curso". Antes esta linea
    // rechazaba INV-081 (bug: dejaba una reserva con una cancelacion PARCIAL previa ya cerrada IMPOSIBLE de
    // volver a anular para siempre); ahora Closed se trata igual que Aborted (libera, abre BC nuevo). Ver el
    // test dedicado mas abajo (DraftAsync_SobreBcClosed_YaNoRechaza_AbreNuevo).
    // =========================================================================

    [Theory]
    [InlineData(BookingCancellationStatus.AwaitingFiscalConfirmation)]
    [InlineData(BookingCancellationStatus.AwaitingOperatorRefund)]
    [InlineData(BookingCancellationStatus.ClientCreditApplied)]
    [InlineData(BookingCancellationStatus.AbandonedByOperator)]
    [InlineData(BookingCancellationStatus.ManualReviewPending)]
    [InlineData(BookingCancellationStatus.ManualReviewRejected)]
    public async Task DraftAsync_SobreEstadoNoLiberable_RechazaINV081(BookingCancellationStatus status)
    {
        var (service, ctx, _) = BuildService();
        var seed = await SeedReservaAsync(ctx);
        await SeedBcAsync(ctx, seed, status);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(
            () => DraftAsync(service, seed.ReservaPublicId));
        Assert.Equal("INV-081", ex.InvariantCode);

        // No se creo un BC nuevo: sigue habiendo una sola fila.
        var count = await ctx.BookingCancellations.CountAsync(b => b.ReservaId == seed.ReservaId);
        Assert.Equal(1, count);
    }

    /// <summary>
    /// ADR-044 T5 Addendum, Decision C (2026-07-11, hallazgo nuevo del re-review): un BC Closed previo YA NO
    /// rechaza INV-081 — se abre un BC nuevo, igual que con Aborted. Contra Postgres real: valida que el
    /// indice UNICO parcial ensanchado (<c>"Status" NOT IN (4, 6)</c>) efectivamente deja pasar el INSERT.
    /// </summary>
    [Fact]
    public async Task DraftAsync_SobreBcClosed_YaNoRechaza_AbreNuevo()
    {
        var (service, ctx, _) = BuildService();
        var seed = await SeedReservaAsync(ctx);
        var closed = await SeedBcAsync(ctx, seed, BookingCancellationStatus.Closed);

        var nuevo = await DraftAsync(service, seed.ReservaPublicId);

        Assert.NotEqual(closed.PublicId, nuevo.PublicId);
        Assert.Equal("Drafted", nuevo.Status);

        var closedReloaded = await ctx.BookingCancellations.AsNoTracking()
            .FirstAsync(b => b.PublicId == closed.PublicId);
        Assert.Equal(BookingCancellationStatus.Closed, closedReloaded.Status); // intacto.

        var total = await ctx.BookingCancellations.CountAsync(b => b.ReservaId == seed.ReservaId);
        Assert.Equal(2, total);
    }

    // =========================================================================
    // Caso (c) FIX 1: ArcaRejected
    // =========================================================================

    [Fact]
    public async Task DraftAsync_SobreArcaRejectedSinNcViva_AutoAbortaYCreaBcNuevo()
    {
        var (service, ctx, auditMock) = BuildService();
        var seed = await SeedReservaAsync(ctx);
        var rejected = await SeedBcAsync(
            ctx, seed, BookingCancellationStatus.ArcaRejected, creditNoteInvoiceId: null);

        var nuevo = await DraftAsync(service, seed.ReservaPublicId);

        Assert.NotEqual(rejected.PublicId, nuevo.PublicId);

        // El viejo quedo Aborted (auto-abortado), el nuevo en Drafted.
        var rejectedReloaded = await ctx.BookingCancellations.AsNoTracking()
            .FirstAsync(b => b.PublicId == rejected.PublicId);
        Assert.Equal(BookingCancellationStatus.Aborted, rejectedReloaded.Status);
        Assert.NotNull(rejectedReloaded.ClosedAt);

        var nuevoReloaded = await ctx.BookingCancellations.AsNoTracking()
            .FirstAsync(b => b.PublicId == nuevo.PublicId);
        Assert.Equal(BookingCancellationStatus.Drafted, nuevoReloaded.Status);

        // FIX 1: el auto-abort deja traza de auditoria especifica.
        auditMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.BookingCancellationAutoAbortedArcaRejected,
            AuditActions.BookingCancellationEntityName,
            It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DraftAsync_SobreArcaRejectedConNcViva_NoLibera_RechazaINV081()
    {
        var (service, ctx, _) = BuildService();
        var seed = await SeedReservaAsync(ctx);
        var ncId = await SeedLiveCreditNoteAsync(ctx, seed);

        // La factura original queda ACTIVA (con CAE, no anulada): DraftAsync la cuenta como la unica factura
        // de venta viva y llega a TryResolveExistingBcAsync. La NC (ncId) NO cuenta como factura de venta
        // (fix 7773063: se excluyen NC/ND), asi que NO hay INV-100 por multi-comprobante. El caso que este
        // test verifica es el (d): un BC ArcaRejected con NC viva (CreditNoteInvoiceId seteado) NO se libera
        // -> rechazo INV-081. (Antes se anulaba la original creyendo que la NC contaba como factura; con la NC
        // ya excluida, anularla dejaba CERO facturas activas y DraftAsync rebotaba por "sin factura activa".)
        var rejected = await SeedBcAsync(
            ctx, seed, BookingCancellationStatus.ArcaRejected, creditNoteInvoiceId: ncId);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(
            () => DraftAsync(service, seed.ReservaPublicId));
        Assert.Equal("INV-081", ex.InvariantCode);

        // BLINDAJE FISCAL: el BC con NC viva NO se libero (sigue ArcaRejected) y no
        // se creo uno nuevo.
        var rejectedReloaded = await ctx.BookingCancellations.AsNoTracking()
            .FirstAsync(b => b.PublicId == rejected.PublicId);
        Assert.Equal(BookingCancellationStatus.ArcaRejected, rejectedReloaded.Status);

        var count = await ctx.BookingCancellations.CountAsync(b => b.ReservaId == seed.ReservaId);
        Assert.Equal(1, count);
    }

    // =========================================================================
    // Colision del UNIQUE parcial sobre OriginatingInvoiceId (no solo ReservaId)
    // =========================================================================

    /// <summary>
    /// El indice parcial es UNIQUE sobre (ReservaId) y sobre (OriginatingInvoiceId)
    /// por separado. Este test fabrica la colision por OriginatingInvoiceId: dos
    /// reservas DISTINTAS que apuntan a la MISMA factura original con un BC activo.
    /// El INSERT del segundo BC debe rebotar contra el UNIQUE parcial de
    /// OriginatingInvoiceId. Como ese segundo BC no comparte ReservaId, la politica
    /// no lo intercepta antes: el guard real es el indice de la BD. Verificamos que
    /// NO escapa como 500 crudo (DbUpdateException), sino que sale como INV-081.
    /// </summary>
    [Fact]
    public async Task DraftAsync_ColisionPorOriginatingInvoiceId_NoEscapaComo500()
    {
        var (service, ctx, _) = BuildService();
        var seed = await SeedReservaAsync(ctx);

        // BC activo de OTRA reserva apuntando a la MISMA factura original.
        // (Estado artificial: en el flujo real OnePerReservaInvoicePolicy evita una
        // factura compartida; aca probamos el backstop de la BD.)
        var otraReserva = new Reserva
        {
            NumeroReserva = $"F-RET-{Guid.NewGuid().ToString("N")[..8]}",
            Name = "Otra reserva misma factura",
            Status = EstadoReserva.Confirmed,
            PayerId = seed.CustomerId,
        };
        ctx.Reservas.Add(otraReserva);
        await ctx.SaveChangesAsync();

        ctx.BookingCancellations.Add(new BookingCancellation
        {
            ReservaId = otraReserva.Id,
            CustomerId = seed.CustomerId,
            SupplierId = seed.SupplierId,
            OriginatingInvoiceId = seed.InvoiceId, // misma factura original
            Status = BookingCancellationStatus.AwaitingFiscalConfirmation,
            Reason = "BC activo de otra reserva sobre la misma factura.",
            DraftedAt = DateTime.UtcNow,
            DraftedByUserId = "seed-user",
            AmountPaidAtCancellation = 1000m,
            EstimatedRefundAmount = 1000m,
            ReceivedRefundAmount = 0m,
            // AwaitingFiscalConfirmation esta fuera de Drafted/Aborted -> el CHECK
            // chk_BookingCancellations_fiscalsnapshot_consistent exige snapshot completo.
            FiscalSnapshot = new FiscalSnapshot
            {
                Source = ExchangeRateSource.Manual,
                ExchangeRateAtOriginalInvoice = 1m,
                CurrencyAtEvent = "ARS",
                ManualJustification = "Seed colision OriginatingInvoiceId",
                FetchedAt = DateTime.UtcNow,
                AgencyTaxConditionAtEvent = "MONOTRIBUTISTA",
                SupplierTaxConditionAtEvent = "IVA_RESP_INSCRIPTO",
                CustomerTaxConditionAtEvent = "CONSUMIDOR_FINAL",
            },
            IsLegacyPreCancellationModel = false,
        });
        await ctx.SaveChangesAsync();

        // DraftAsync sobre la reserva original: no hay BC con su ReservaId, pasa la
        // politica, intenta INSERT y choca el UNIQUE parcial de OriginatingInvoiceId.
        // Debe salir como BusinessInvariantViolationException, NO como DbUpdateException.
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(
            () => DraftAsync(service, seed.ReservaPublicId));
        Assert.Equal("INV-081", ex.InvariantCode);
    }
}
