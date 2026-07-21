using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// Reproduccion contra Postgres REAL del reporte de Gaston (2026-07-20): "la reserva F-2026-1051 (hotel
/// Confirmado, moneda USD) no aparece en la Cuenta corriente del proveedor ni se puede elegir para pagarle,
/// aunque SI aparece en Nueva factura". Hipotesis a probar: servicios VIEJOS del mismo proveedor con
/// <c>Currency</c> vacia/NULL (camino legacy pre-moneda) ensucian el agregado y hacen desaparecer o mezclar
/// mal las reservas SANAS que si tienen moneda explicita (USD).
///
/// <para><b>Que arma el seed</b>: UN proveedor reseller (InvoicingMode=TotalToCustomer, igual que el operador
/// 5 de PROD) con DOS reservas confirmadas: una reserva LEGACY con un hotel Confirmado sin moneda (like las
/// 4 reservas viejas de PROD) y una reserva SANA con un hotel Confirmado en USD (como F-2026-1051). Se
/// llaman los TRES metodos reales que arma <c>SupplierService</c> — extracto, deuda por reserva y overview —
/// para ver si la reserva sana aparece con su plata correcta en los tres.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class SupplierAccountEmptyCurrencyIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public SupplierAccountEmptyCurrencyIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Arma el seed comun a los tres tests: proveedor reseller + reserva legacy (moneda vacia) + reserva sana
    /// (USD explicito). <paramref name="legacyCurrency"/> permite probar tanto <c>null</c> como <c>""</c> (el
    /// enunciado del bug no aclara cual quedo grabado en las 4 reservas viejas de PROD).
    /// </summary>
    private async Task<(int SupplierId, Guid SanaReservaPublicId)> SeedSupplierWithLegacyAndSaneReservaAsync(
        string? legacyCurrency)
    {
        await using var ctx = _fixture.CreateDbContext();

        var supplier = new Supplier
        {
            Name = "SANTA CATALINA (test)",
            InvoicingMode = SupplierInvoicingMode.TotalToCustomer,
            IsActive = true,
        };
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        // Reserva LEGACY: hotel Confirmado SIN moneda (el camino viejo pre-ADR-021).
        var reservaLegacy = new Reserva
        {
            NumeroReserva = "F-2026-LEGACY",
            Name = "Reserva legacy sin moneda",
            Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reservaLegacy);
        await ctx.SaveChangesAsync();

        ctx.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reservaLegacy.Id,
            SupplierId = supplier.Id,
            HotelName = "Hotel legacy",
            City = "Ciudad",
            Status = "Confirmado",
            NetCost = 1000m,
            SalePrice = 1500m,
            Currency = legacyCurrency,
        });
        await ctx.SaveChangesAsync();

        // Reserva SANA: hotel Confirmado con moneda USD explicita, como F-2026-1051 en PROD.
        var reservaSana = new Reserva
        {
            NumeroReserva = "F-2026-SANA",
            Name = "Reserva sana en USD",
            Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reservaSana);
        await ctx.SaveChangesAsync();

        ctx.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reservaSana.Id,
            SupplierId = supplier.Id,
            HotelName = "Confirmado",
            City = "Ciudad",
            Status = "Confirmado",
            NetCost = 1800m,
            SalePrice = 2500m,
            Currency = "USD",
        });
        await ctx.SaveChangesAsync();

        return (supplier.Id, reservaSana.PublicId);
    }

    /// <summary>
    /// Arma un <see cref="SupplierService"/> con permiso <c>cobranzas.see_cost</c> concedido, para poder leer
    /// los montos reales (sin esto el service enmascara todo a 0 por fail-closed, ver
    /// <c>CostMasking.CanSeeCostAsync</c>).
    /// </summary>
    private static SupplierService BuildServiceWithCostVisibility(AppDbContext context)
    {
        const string userId = "test-admin";
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId) };
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")),
            },
        };

        var resolverMock = new Mock<IUserPermissionResolver>();
        resolverMock
            .Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlySet<string>)new HashSet<string>
            {
                Permissions.CobranzasSeeCost,
                Permissions.TesoreriaSupplierPayments,
            });

        return new SupplierService(
            context,
            auditService: null,
            httpContextAccessor: accessor,
            logger: null,
            permissionResolver: resolverMock.Object);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ExtractoDeLaCuentaCorriente_MuestraLaReservaSanaEnUSD_ConMonedaLegacyVacia(string? legacyCurrency)
    {
        var (supplierId, reservaSanaPublicId) = await SeedSupplierWithLegacyAndSaneReservaAsync(legacyCurrency);

        await using var ctx = _fixture.CreateDbContext();
        var service = BuildServiceWithCostVisibility(ctx);

        var statement = await service.GetSupplierAccountStatementAsync(supplierId, CancellationToken.None);

        Assert.True(statement.AmountsVisible);

        // Debe existir un bloque USD con la compra de 1800 de la reserva sana.
        var usdBlock = Assert.Single(statement.Currencies, block => block.Currency == "USD");
        Assert.Equal(1800m, usdBlock.CashClosingBalance);
        var usdPurchaseLine = Assert.Single(usdBlock.Lines, line => line.Charge == 1800m);
        Assert.Equal("F-2026-SANA", usdPurchaseLine.DocumentRef);

        // La reserva legacy (moneda vacia) debe caer en su propio bloque ARS, SIN mezclarse con el USD.
        var arsBlock = Assert.Single(statement.Currencies, block => block.Currency == "ARS");
        Assert.Equal(1000m, arsBlock.CashClosingBalance);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task DeudaPorReserva_IncluyeLaReservaSanaEnUSD_ConMonedaLegacyVacia(string? legacyCurrency)
    {
        var (supplierId, reservaSanaPublicId) = await SeedSupplierWithLegacyAndSaneReservaAsync(legacyCurrency);

        await using var ctx = _fixture.CreateDbContext();
        var service = BuildServiceWithCostVisibility(ctx);

        var debtByReserva = await service.GetSupplierDebtByReservaAsync(supplierId, CancellationToken.None);

        var reservaSanaLine = Assert.Single(
            debtByReserva.Reservas, r => r.ReservaPublicId == reservaSanaPublicId);
        var usdLine = Assert.Single(reservaSanaLine.Currencies, c => c.Currency == "USD");
        Assert.Equal(1800m, usdLine.ConfirmedPurchases);
        Assert.Equal(1800m, usdLine.Balance);

        // El total global en USD tiene que reflejar la deuda de la reserva sana.
        var globalUsd = Assert.Single(debtByReserva.GlobalTotals, g => g.Currency == "USD");
        Assert.Equal(1800m, globalUsd.Amount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Overview_BalancesByCurrency_IncluyeLaReservaSanaEnUSD_ConMonedaLegacyVacia(string? legacyCurrency)
    {
        var (supplierId, _) = await SeedSupplierWithLegacyAndSaneReservaAsync(legacyCurrency);

        // El snapshot materializado (SupplierBalanceByCurrency) lo escribe UpdateBalanceAsync — en produccion
        // corre automaticamente cada vez que se toca un servicio del proveedor (via SupplierDebtPersister).
        await using (var writeCtx = _fixture.CreateDbContext())
        {
            var writerService = BuildServiceWithCostVisibility(writeCtx);
            await writerService.UpdateBalanceAsync(supplierId, CancellationToken.None);
        }

        await using var readCtx = _fixture.CreateDbContext();
        var readerService = BuildServiceWithCostVisibility(readCtx);

        var overview = await readerService.GetSupplierAccountOverviewAsync(supplierId, CancellationToken.None);

        var usdBalance = Assert.Single(overview.BalancesByCurrency, b => b.Currency == "USD");
        Assert.Equal(1800m, usdBalance.ConfirmedPurchases);
        Assert.Equal(1800m, usdBalance.Balance);

        var arsBalance = Assert.Single(overview.BalancesByCurrency, b => b.Currency == "ARS");
        Assert.Equal(1000m, arsBalance.ConfirmedPurchases);
    }
}
