using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

/// <summary>
/// Hallazgo de review (2026-07-27, condicion B3, bloqueante backend+security): el ownerFilter de
/// <see cref="ReportService.GetDashboardAsync"/> (obra 5 "Ventas personales" + su cierre de hueco) agrega
/// varios LEFT JOIN nuevos (Payments-&gt;Reservas, SupplierPayments-&gt;Reservas) que el proveedor
/// InMemory de la suite unit NUNCA traduce a SQL de verdad — los ejecuta como C# directo. La misma
/// leccion del hotfix del buscador global (commit 48b15347, "Translation of method 'object.ToString'
/// failed") exige correr esta consulta contra Postgres REAL al menos una vez: si alguna expresion no es
/// traducible, <c>ToListAsync</c>/<c>SumAsync</c> explotan aca, aunque las tablas esten vacias.
///
/// <para><b>OJO — "Posibles clientes activos" (Leads) NO usa ownerFilter</b>: un hallazgo de review del
/// mismo dia habia acotado <see cref="ReportService.GetDashboardAsync"/> para filtrar
/// <see cref="Lead.AssignedToUserId"/> por vendedor, pero Gaston firmo (adenda 2026-07-27 tarde,
/// docs/ux/guia-ux-gaston.md) que ese conteo muestra la agencia ENTERA para cualquier usuario (los leads
/// son compartidos, un conteo no expone plata). El test de vendedor de este archivo siembra un lead de
/// OTRO vendedor a proposito y assert <c>ActivePotentialCustomers == 2</c> — blinda esa excepcion firmada
/// contra Postgres real, no solo contra el InMemory de la suite unit.</para>
///
/// <para>Sembramos usuarios reales en <c>AspNetUsers</c> (patron de
/// <see cref="SearchServiceSqlTranslationIntegrationTests"/>) porque <c>Reserva.ResponsibleUserId</c> tiene
/// FK real a esa tabla en Postgres (InMemory no la valida, por eso el patron de seed hace falta aca).</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReportServiceDashboardSqlTranslationIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public ReportServiceDashboardSqlTranslationIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Mismo patron que SearchServiceSqlTranslationIntegrationTests: fila minima en AspNetUsers
    /// para satisfacer la FK real de Reserva.ResponsibleUserId contra Postgres.</summary>
    private static async Task SeedAspNetUserAsync(TravelApi.Infrastructure.Persistence.AppDbContext ctx, string userId)
    {
        await ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AspNetUsers"
              ("Id", "UserName", "NormalizedUserName", "Email", "NormalizedEmail",
               "EmailConfirmed", "PasswordHash", "SecurityStamp", "ConcurrencyStamp",
               "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled",
               "AccessFailedCount", "FullName", "IsActive")
            VALUES
              ({userId}, {userId}, {userId.ToUpperInvariant()},
               {userId + "@test.local"}, {(userId + "@test.local").ToUpperInvariant()},
               true, 'test-hash', {Guid.NewGuid().ToString()}, {Guid.NewGuid().ToString()},
               false, false, false,
               0, {"Test User " + userId}, true)
            ON CONFLICT ("Id") DO NOTHING;
            """);
    }

    private static Mock<IBnaExchangeRateService> BuildBnaMock()
    {
        var mock = new Mock<IBnaExchangeRateService>();
        // La cotizacion BNA es informativa (ver GetDashboardBnaRateAsync): con null alcanza, no
        // necesitamos que el mock dispare una llamada de red real.
        mock.Setup(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((BnaUsdSellerRateDto?)null);
        return mock;
    }

    [Fact]
    public async Task GetDashboardAsync_ComoVendedorSinViewAll_TraduceLosJoinsNuevosASqlSinExplotar()
    {
        await using var ctx = _fixture.CreateDbContext();

        // "vendedor-A" es quien consulta el dashboard (sin reservas.view_all -> ownerFilter activo).
        // "vendedor-B" es el dueno de una reserva AJENA, para ejercitar el LEFT JOIN + filtro de owner
        // con datos reales de los dos lados (no solo tablas vacias).
        await SeedAspNetUserAsync(ctx, "vendedor-A");
        await SeedAspNetUserAsync(ctx, "vendedor-B");

        var reservaPropia = new Reserva
        {
            NumeroReserva = $"F-DASH-{Guid.NewGuid():N}"[..14],
            Name = "Reserva propia del vendedor A",
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "vendedor-A",
            TotalSale = 1000m,
            TotalCost = 600m,
            Balance = 300m,
            CreatedAt = DateTime.UtcNow,
        };
        var reservaAjena = new Reserva
        {
            NumeroReserva = $"F-DASH-{Guid.NewGuid():N}"[..14],
            Name = "Reserva ajena del vendedor B",
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "vendedor-B",
            TotalSale = 2000m,
            TotalCost = 1200m,
            Balance = 800m,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Reservas.AddRange(reservaPropia, reservaAjena);
        await ctx.SaveChangesAsync();

        // Cobro y pago a proveedor atados por FK real a cada reserva: ejercitan el LEFT JOIN
        // Payments/SupplierPayments -> Reservas -> ResponsibleUserId.
        ctx.Payments.Add(new Payment
        {
            ReservaId = reservaPropia.Id, Amount = 300m, Currency = Monedas.ARS,
            PaidAt = DateTime.UtcNow, AffectsCash = true,
        });
        ctx.Payments.Add(new Payment
        {
            ReservaId = reservaAjena.Id, Amount = 800m, Currency = Monedas.ARS,
            PaidAt = DateTime.UtcNow, AffectsCash = true,
        });
        var supplier = new Supplier { Name = "Operador Test Dashboard" };
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();
        ctx.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplier.Id, ReservaId = reservaPropia.Id, Amount = 100m,
            Currency = Monedas.ARS, PaidAt = DateTime.UtcNow,
        });

        // Firma de Gaston (adenda 2026-07-27 tarde): "Posibles clientes activos" es agencia-completa, SIN
        // ownerFilter. Sembramos un lead de CADA vendedor a proposito: si el filtro por AssignedToUserId
        // se reintrodujera por error, el assert de mas abajo (==2, no 1) lo agarraria contra Postgres real.
        ctx.Leads.AddRange(
            new Lead { FullName = "Cliente potencial de A", Status = LeadStatus.New, AssignedToUserId = "vendedor-A" },
            new Lead { FullName = "Cliente potencial de B", Status = LeadStatus.Contacted, AssignedToUserId = "vendedor-B" });
        await ctx.SaveChangesAsync();

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "vendedor-A"),
                    new Claim(ClaimTypes.Role, "Vendedor"),
                },
                authenticationType: "Test")),
        };
        var accessor = new HttpContextAccessor { HttpContext = httpContext };

        // Sin reservas.view_all (ownerFilter activo), CON cobranzas.see_cost (para ejercitar tambien
        // las ramas de costo/margen/cuentas por pagar acotadas).
        var resolverMock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> permisos = new HashSet<string> { Permissions.ReportesView, Permissions.CobranzasSeeCost };
        resolverMock.Setup(r => r.GetPermissionsAsync("vendedor-A", It.IsAny<CancellationToken>()))
            .ReturnsAsync(permisos);

        var service = new ReportService(ctx, BuildBnaMock().Object, resolverMock.Object, accessor);

        // Si alguna expresion nueva (LEFT JOIN, Lead.AssignedToUserId, etc.) no fuera traducible,
        // esto explota aca con InvalidOperationException — el mismo bug de PROD del buscador global.
        var dto = await service.GetDashboardAsync(CancellationToken.None);

        Assert.NotNull(dto);
        // Scope real: solo ve SU cartera de ventas/cobros/pagos/cuentas por pagar, no la agencia entera.
        Assert.Equal(1000m, dto.VentasDelMes);
        Assert.Equal(300m, dto.CobrosDelMes);
        Assert.Equal(100m, dto.PagosProveedores);
        Assert.Empty(dto.PorMoneda!.CuentasPorPagar);
        // Excepcion firmada (Gaston, adenda 2026-07-27 tarde): "Posibles clientes activos" NO se acota por
        // vendedor. Ve los DOS leads (el suyo y el de vendedor-B), no solo el suyo.
        Assert.Equal(2, dto.ActivePotentialCustomers);
    }

    [Fact]
    public async Task GetDashboardAsync_ComoAdmin_TraduceTodoElDashboardASqlSinExplotar()
    {
        await using var ctx = _fixture.CreateDbContext();

        await SeedAspNetUserAsync(ctx, "admin-integration-test");

        var reserva = new Reserva
        {
            NumeroReserva = $"F-DASH-{Guid.NewGuid():N}"[..14],
            Name = "Reserva de la agencia",
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "admin-integration-test",
            TotalSale = 500m,
            TotalCost = 200m,
            Balance = 100m,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "admin-integration-test"),
                    new Claim(ClaimTypes.Role, "Admin"),
                },
                authenticationType: "Test")),
        };
        var accessor = new HttpContextAccessor { HttpContext = httpContext };

        // Admin bypassea todos los permisos (isAdmin=true): ownerFilter queda null y las consultas
        // corren SIN el LEFT JOIN de owner-scope. Igual queremos la red de traduccion para ese camino.
        var service = new ReportService(ctx, BuildBnaMock().Object, permissionResolver: null, httpContextAccessor: accessor);

        var dto = await service.GetDashboardAsync(CancellationToken.None);

        Assert.NotNull(dto);
        Assert.Equal(500m, dto.VentasDelMes);
    }

    /// <summary>
    /// Hallazgo de review (2026-08-20, bloqueante backend+security): la obra "informes por moneda" agrega
    /// ownerFilter + proyecciones nuevas a <see cref="ReportService.GetSellerRankingAsync"/>,
    /// <see cref="ReportService.GetDestinationAnalyticsAsync"/> (JOIN nuevo contra Reservas desde
    /// Hotel/Package/Flight) y <see cref="ReportService.GetYearOverYearAsync"/> (proyeccion a los records
    /// <c>ReservaYearRow</c>/<c>ReservaCurrencyMoneyRow</c>, nunca ejercitada contra un provider SQL real
    /// hasta este test). Mismo riesgo que el resto de este archivo: el InMemory de la suite unit no
    /// traduce nada a SQL, asi que una expresion no traducible recien explota aca.
    /// </summary>
    [Fact]
    public async Task SellersDestinationsYoy_ComoVendedorSinViewAll_TraducenLosJoinsNuevosASqlSinExplotar()
    {
        await using var ctx = _fixture.CreateDbContext();

        await SeedAspNetUserAsync(ctx, "vendedor-bi-A");
        await SeedAspNetUserAsync(ctx, "vendedor-bi-B");

        var reservaPropia = new Reserva
        {
            NumeroReserva = $"F-BI-{Guid.NewGuid():N}"[..14],
            Name = "Reserva propia del vendedor A",
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "vendedor-bi-A",
            ResponsibleUserName = "Vendedor A",
            ConfirmedSale = 1000m,
            TotalSale = 1000m,
            TotalCost = 600m,
            CreatedAt = DateTime.UtcNow,
        };
        var reservaAjena = new Reserva
        {
            NumeroReserva = $"F-BI-{Guid.NewGuid():N}"[..14],
            Name = "Reserva ajena del vendedor B",
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "vendedor-bi-B",
            ResponsibleUserName = "Vendedor B",
            ConfirmedSale = 2000m,
            TotalSale = 2000m,
            TotalCost = 1200m,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Reservas.AddRange(reservaPropia, reservaAjena);
        await ctx.SaveChangesAsync();

        // Desglose por moneda (ReservaMoneyByCurrency) de cada reserva: ejercita la proyeccion nueva a
        // ReservaCurrencyMoneyRow (YoY) y el join por vendedor (sellers).
        ctx.ReservaMoneyByCurrency.AddRange(
            new ReservaMoneyByCurrency { ReservaId = reservaPropia.Id, Currency = Monedas.ARS, ConfirmedSale = 700m, TotalSale = 700m, TotalCost = 400m },
            new ReservaMoneyByCurrency { ReservaId = reservaPropia.Id, Currency = Monedas.USD, ConfirmedSale = 300m, TotalSale = 300m, TotalCost = 200m },
            new ReservaMoneyByCurrency { ReservaId = reservaAjena.Id, Currency = Monedas.ARS, ConfirmedSale = 2000m, TotalSale = 2000m, TotalCost = 1200m });

        // Hoteles atados a cada reserva por FK real: ejercita el JOIN nuevo de GetDestinationAnalyticsAsync.
        ctx.HotelBookings.AddRange(
            new HotelBooking { ReservaId = reservaPropia.Id, City = "Bariloche", Currency = Monedas.ARS, SalePrice = 700m, NetCost = 400m, Adults = 2, CreatedAt = DateTime.UtcNow },
            new HotelBooking { ReservaId = reservaAjena.Id, City = "Cancun", Currency = Monedas.ARS, SalePrice = 2000m, NetCost = 1200m, Adults = 2, CreatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "vendedor-bi-A"),
                    new Claim(ClaimTypes.Role, "Vendedor"),
                },
                authenticationType: "Test")),
        };
        var accessor = new HttpContextAccessor { HttpContext = httpContext };

        var resolverMock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> permisos = new HashSet<string> { Permissions.ReportesView, Permissions.CobranzasSeeCost };
        resolverMock.Setup(r => r.GetPermissionsAsync("vendedor-bi-A", It.IsAny<CancellationToken>()))
            .ReturnsAsync(permisos);

        var service = new ReportService(ctx, BuildBnaMock().Object, resolverMock.Object, accessor);

        // Si el JOIN nuevo o la proyeccion a record no fueran traducibles, esto explota aca (no en el
        // InMemory de la suite unit, que ejecuta todo como C# directo sin validar SQL real).
        var sellers = await service.GetSellerRankingAsync(null, null, CancellationToken.None);
        var destinations = await service.GetDestinationAnalyticsAsync(null, null, CancellationToken.None);
        var yoy = await service.GetYearOverYearAsync(CancellationToken.None);

        // Scope real: vendedor-bi-A solo ve SU fila/destino/interanual, nunca los de vendedor-bi-B.
        var sellerRow = Assert.Single(sellers);
        Assert.Equal("vendedor-bi-A", sellerRow.UserId);
        Assert.Equal(1000m, sellerRow.TotalSales);

        var destinationRow = Assert.Single(destinations);
        Assert.Equal("BARILOCHE", destinationRow.Destination);

        var currentMonth = yoy.CurrentYear.Single(m => m.MonthNumber == DateTime.UtcNow.Month);
        Assert.Equal(1000m, currentMonth.Sales);
    }
}
