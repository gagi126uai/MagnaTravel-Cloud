using System;
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
/// Fix ADR-044 (2026-08-20): antes <c>OperatorRefundReceived.ExchangeRateAtReceipt</c> se hardcodeaba en
/// 1 SIEMPRE, aunque el reembolso fuera en USD. Eso rompia <c>TreasuryFxAdjustmentEngine</c> (ADR-044
/// T3b), que usa ese campo como base para calcular la diferencia de cambio de un cargo del operador.
///
/// <para>Este archivo fija el comportamiento nuevo de <c>ResolveExchangeRateAtReceiptAsync</c> (privado,
/// se ejercita a traves de <c>RecordReceivedAsync</c>):
/// <list type="bullet">
///   <item>ARS: siempre 1, sin consultar el resolver.</item>
///   <item>USD con el resolver disponible: toma el TC que sugiere la libreta (<see cref="IExchangeRateResolver"/>).</item>
///   <item>USD con la libreta vacia (resolver devuelve null): degrada a 1, sin romper el alta del refund.</item>
/// </list>
/// </para>
/// </summary>
public class OperatorRefundExchangeRateAtReceiptTests
{
    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"refund-fx-at-receipt-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static OperatorRefundService BuildService(AppDbContext ctx, IExchangeRateResolver? resolver)
    {
        var bcServiceMock = new Mock<IBookingCancellationService>();
        var clientCreditMock = new Mock<IClientCreditService>();
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableNewCancellationFlow = true, OperatorRefundTimeoutDays = 60 });

        return new OperatorRefundService(
            ctx, bcServiceMock.Object, clientCreditMock.Object, new Mock<IAuditService>().Object,
            settingsMock.Object, NullLogger<OperatorRefundService>.Instance, resolver);
    }

    private static async Task<Supplier> SeedSupplierAsync(AppDbContext ctx)
    {
        var supplier = new Supplier { Name = "Operador FX", IsActive = true };
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();
        return supplier;
    }

    [Fact]
    public async Task Ars_NeverAsksTheResolver_ExchangeRateIsAlwaysOne()
    {
        await using var ctx = NewDbContext();
        var supplier = await SeedSupplierAsync(ctx);

        // Resolver mockeado SIN Setup: si el service lo llamara para ARS, Moq tira porque no hay
        // comportamiento configurado — este test falla si alguien rompe el atajo "ARS = 1 sin consultar".
        var resolverMock = new Mock<IExchangeRateResolver>(MockBehavior.Strict);
        var service = BuildService(ctx, resolverMock.Object);

        var refund = await service.RecordReceivedAsync(
            new RecordOperatorRefundRequest(supplier.PublicId, 1_000m, "ARS", DateTime.UtcNow, "Transferencia", "OP-ARS", null),
            "cajero-1", "Cajero", CancellationToken.None);

        var persisted = await ctx.OperatorRefundReceived.SingleAsync(r => r.PublicId == refund.PublicId);
        Assert.Equal(1m, persisted.ExchangeRateAtReceipt);
        resolverMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Usd_WithResolverAvailable_UsesTheQuoteFromTheLedger()
    {
        await using var ctx = NewDbContext();
        var supplier = await SeedSupplierAsync(ctx);

        var receivedAt = new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc);
        var resolverMock = new Mock<IExchangeRateResolver>();
        resolverMock
            .Setup(r => r.GetSuggestionAsync("USD", DateOnly.FromDateTime(receivedAt), It.IsAny<CancellationToken>(), true))
            .ReturnsAsync(new ExchangeRateSuggestion(
                Rate: 1350.50m,
                RateDate: DateOnly.FromDateTime(receivedAt),
                Source: ExchangeRateSource.BNA_Minorista,
                ProviderName: "BNA_Scraper",
                ArcaFchCotiz: null,
                IsStale: false,
                QuoteId: 1,
                FetchedAt: receivedAt,
                IsProductionSource: true));

        var service = BuildService(ctx, resolverMock.Object);

        var refund = await service.RecordReceivedAsync(
            new RecordOperatorRefundRequest(supplier.PublicId, 1_000m, "USD", receivedAt, "Transferencia", "OP-USD", null),
            "cajero-1", "Cajero", CancellationToken.None);

        var persisted = await ctx.OperatorRefundReceived.SingleAsync(r => r.PublicId == refund.PublicId);
        Assert.Equal(1350.50m, persisted.ExchangeRateAtReceipt);
    }

    [Fact]
    public async Task Usd_WithEmptyLedger_DegradesToOne_DoesNotBlockTheRefund()
    {
        await using var ctx = NewDbContext();
        var supplier = await SeedSupplierAsync(ctx);

        var resolverMock = new Mock<IExchangeRateResolver>();
        resolverMock
            .Setup(r => r.GetSuggestionAsync("USD", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>(), true))
            .ReturnsAsync((ExchangeRateSuggestion?)null);

        var service = BuildService(ctx, resolverMock.Object);

        var refund = await service.RecordReceivedAsync(
            new RecordOperatorRefundRequest(supplier.PublicId, 1_000m, "USD", DateTime.UtcNow, "Transferencia", "OP-USD-SINDATO", null),
            "cajero-1", "Cajero", CancellationToken.None);

        var persisted = await ctx.OperatorRefundReceived.SingleAsync(r => r.PublicId == refund.PublicId);
        Assert.Equal(1m, persisted.ExchangeRateAtReceipt);
    }

    [Fact]
    public async Task Usd_WithoutResolverInjected_DegradesToOne_SameAsBeforeTheFix()
    {
        // Ctor de 6 args (mismo patron que el resto de los tests de este service): sin resolver
        // inyectado, el comportamiento tiene que ser IDENTICO al de antes del fix (TC = 1).
        await using var ctx = NewDbContext();
        var supplier = await SeedSupplierAsync(ctx);
        var service = BuildService(ctx, resolver: null);

        var refund = await service.RecordReceivedAsync(
            new RecordOperatorRefundRequest(supplier.PublicId, 1_000m, "USD", DateTime.UtcNow, "Transferencia", "OP-USD-SINRESOLVER", null),
            "cajero-1", "Cajero", CancellationToken.None);

        var persisted = await ctx.OperatorRefundReceived.SingleAsync(r => r.PublicId == refund.PublicId);
        Assert.Equal(1m, persisted.ExchangeRateAtReceipt);
    }
}
