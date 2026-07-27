using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// B1.15 Fase 2a (FIX 4): el dashboard enmascara costos sin cobranzas.see_cost
/// y filtra ReservasPendientes / ProximosViajes sin reservas.view_all.
/// </summary>
public class ReportServiceDashboardScopingTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly Mock<IBnaExchangeRateService> _bnaMock;

    public ReportServiceDashboardScopingTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _bnaMock = new Mock<IBnaExchangeRateService>();
        _bnaMock.Setup(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>())).ReturnsAsync((BnaUsdSellerRateDto?)null);
    }

    private static IHttpContextAccessor BuildContextAccessor(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        foreach (var r in roles) claims.Add(new Claim(ClaimTypes.Role, r));
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        return new HttpContextAccessor { HttpContext = ctx };
    }

    private static IUserPermissionResolver BuildResolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(set);
        return mock.Object;
    }

    private static async Task SeedAsync(AppDbContext context)
    {
        var startMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        // 2 reservas: una mia (vendedor-A), una ajena (vendedor-B). Ambas con balance > 0.
        context.Reservas.AddRange(
            new Reserva
            {
                Id = 1,
                NumeroReserva = "F-DASH-0001",
                Name = "Reserva mia",
                Status = EstadoReserva.Confirmed,
                ResponsibleUserId = "vendedor-A",
                CreatedAt = startMonth.AddDays(2),
                TotalSale = 1000m,
                TotalCost = 600m,
                Balance = 300m,
                StartDate = DateTime.UtcNow.AddDays(3)
            },
            new Reserva
            {
                Id = 2,
                NumeroReserva = "F-DASH-0002",
                Name = "Reserva ajena",
                Status = EstadoReserva.Confirmed,
                ResponsibleUserId = "vendedor-B",
                CreatedAt = startMonth.AddDays(2),
                TotalSale = 2000m,
                TotalCost = 1200m,
                Balance = 800m,
                StartDate = DateTime.UtcNow.AddDays(4)
            });
        await context.SaveChangesAsync();

        // ADR-021 Capa 6: el top-N de deudoras se lee de la tabla hija ReservaMoneyByCurrency (no del
        // escalar surrogate). En produccion el persister la mantiene sincronizada; aca la sembramos a
        // mano espejando el saldo ARS de cada reserva (ambas mono-ARS).
        context.ReservaMoneyByCurrency.AddRange(
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = Monedas.ARS, TotalSale = 1000m, ConfirmedSale = 1000m, TotalCost = 600m, TotalPaid = 700m, Balance = 300m },
            new ReservaMoneyByCurrency { ReservaId = 2, Currency = Monedas.ARS, TotalSale = 2000m, ConfirmedSale = 2000m, TotalCost = 1200m, TotalPaid = 1200m, Balance = 800m });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Dashboard_WithoutSeeCost_MasksCostosAndMargen()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var accessor = BuildContextAccessor("vendedor-A", "Vendedor");
        var resolver = BuildResolver("vendedor-A", Permissions.ReportesView);

        var service = new ReportService(context, _bnaMock.Object, resolver, accessor);
        var dto = await service.GetDashboardAsync(CancellationToken.None);

        Assert.Equal(0m, dto.CostosDelMes);
        Assert.Equal(0m, dto.MargenBruto);
        Assert.Equal(0m, dto.PagosProveedores);
        // Trend tambien debe enmascarar costs y profit.
        Assert.All(dto.TendenciaHistorica, m =>
        {
            Assert.Equal(0m, m.Costs);
            Assert.Equal(0m, m.Profit);
        });
    }

    [Fact]
    public async Task Dashboard_WithSeeCost_ReturnsCostosAndMargen()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var accessor = BuildContextAccessor("colaborador-1", "Colaborador");
        var resolver = BuildResolver("colaborador-1",
            Permissions.ReportesView, Permissions.CobranzasSeeCost, Permissions.ReservasViewAll);

        var service = new ReportService(context, _bnaMock.Object, resolver, accessor);
        var dto = await service.GetDashboardAsync(CancellationToken.None);

        // Cost = 600 + 1200 = 1800 (ambas reservas del mes).
        Assert.Equal(1800m, dto.CostosDelMes);
        // Margen = 3000 - 1800 = 1200.
        Assert.Equal(1200m, dto.MargenBruto);
    }

    [Fact]
    public async Task Dashboard_VendedorWithoutViewAll_FiltersPendingToOwn()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var accessor = BuildContextAccessor("vendedor-A", "Vendedor");
        var resolver = BuildResolver("vendedor-A", Permissions.ReportesView);

        var service = new ReportService(context, _bnaMock.Object, resolver, accessor);
        var dto = await service.GetDashboardAsync(CancellationToken.None);

        // Solo mi reserva con balance > 0.
        Assert.Single(dto.ReservasPendientes);
        Assert.Equal("F-DASH-0001", dto.ReservasPendientes[0].NumeroReserva);

        // Solo mi proximo viaje.
        Assert.Single(dto.ProximosViajes);
        Assert.Equal("F-DASH-0001", dto.ProximosViajes[0].NumeroReserva);
    }

    [Fact]
    public async Task Dashboard_AdminBypass_SeesAllPendingAndCosts()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var accessor = BuildContextAccessor("admin-1", "Admin");
        var resolver = BuildResolver("admin-1");

        var service = new ReportService(context, _bnaMock.Object, resolver, accessor);
        var dto = await service.GetDashboardAsync(CancellationToken.None);

        Assert.Equal(2, dto.ReservasPendientes.Count);
        Assert.Equal(1800m, dto.CostosDelMes);
    }

    // ============================================================================================
    // H15 (barrido E2E 2026-07-25): el widget "Cobros Pendientes" mostraba una reserva YA SALDADA (o
    // sobre-cobrada) como si tuviera plata pendiente. Fix: el filtro ahora respeta
    // Reserva.DerivedCollectionStatus (el eje de cobranza YA calculado, ADR-048 T5) en vez de confiar
    // solo en el residuo crudo de ReservaMoneyByCurrency.Balance.
    // ============================================================================================

    [Fact]
    public async Task Dashboard_ReservaConDerivedCollectionStatusSaldado_NoApareceEnPendientes_AunqueElBalanceResidualSeaMayorACero()
    {
        await using var context = new AppDbContext(_dbOptions);
        var startMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        // Reserva marcada "Saldado" por el motor (DerivedCollectionStatus), con un RESIDUO de centavos
        // en la tabla hija (Balance = 0.004 > 0) — exactamente el caso que antes colaba en el widget
        // aunque el eje de cobranza YA supiera que estaba saldada.
        //
        // Fix B2 (review 2026-07-27, bloqueante): el seed original usaba Balance = 0.30, un estado
        // INALCANZABLE en produccion. El motor real que calcula DerivedCollectionStatus
        // (ReservaCollectionStatus.Derive, ver TravelApi.Application.DTOs.ReservaCollectionStatus)
        // considera "deuda" a cualquier balance mayor al Epsilon de 0.005: con 0.30 el resultado real
        // SIEMPRE seria "ConDeuda", nunca "Saldado". El test forzaba una combinacion que el sistema real
        // jamas produce. 0.004 SI es un residuo alcanzable (queda por debajo del Epsilon), asi que el
        // fix bajo prueba (confiar en el eje calculado en vez del Balance crudo) sigue teniendo sentido:
        // el chequeo crudo "Balance > 0" seguiria marcando esta fila como pendiente si no fuera por el
        // DerivedCollectionStatus.
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-DASH-SALDADA",
            Name = "Reserva saldada con residuo",
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "vendedor-A",
            CreatedAt = startMonth.AddDays(2),
            TotalSale = 1000m,
            TotalCost = 600m,
            Balance = 0.004m,
            DerivedCollectionStatus = ReservaCollectionStatus.Settled,
        });
        context.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency
        {
            ReservaId = 1, Currency = Monedas.ARS, TotalSale = 1000m, ConfirmedSale = 1000m,
            TotalCost = 600m, TotalPaid = 999.996m, Balance = 0.004m,
        });
        await context.SaveChangesAsync();

        var accessor = BuildContextAccessor("admin-1", "Admin");
        var resolver = BuildResolver("admin-1");
        var service = new ReportService(context, _bnaMock.Object, resolver, accessor);

        var dto = await service.GetDashboardAsync(CancellationToken.None);

        Assert.Empty(dto.ReservasPendientes);
    }

    [Fact]
    public async Task Dashboard_ReservaConDerivedCollectionStatusConDeuda_SiApareceEnPendientes()
    {
        await using var context = new AppDbContext(_dbOptions);
        var startMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        context.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-DASH-CONDEUDA",
            Name = "Reserva con deuda real",
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "vendedor-A",
            CreatedAt = startMonth.AddDays(2),
            TotalSale = 1000m,
            TotalCost = 600m,
            Balance = 400m,
            DerivedCollectionStatus = ReservaCollectionStatus.WithDebt,
        });
        context.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency
        {
            ReservaId = 1, Currency = Monedas.ARS, TotalSale = 1000m, ConfirmedSale = 1000m,
            TotalCost = 600m, TotalPaid = 600m, Balance = 400m,
        });
        await context.SaveChangesAsync();

        var accessor = BuildContextAccessor("admin-1", "Admin");
        var resolver = BuildResolver("admin-1");
        var service = new ReportService(context, _bnaMock.Object, resolver, accessor);

        var dto = await service.GetDashboardAsync(CancellationToken.None);

        var match = Assert.Single(dto.ReservasPendientes);
        Assert.Equal("F-DASH-CONDEUDA", match.NumeroReserva);
    }

    [Fact]
    public async Task Dashboard_ReservaLegacySinDerivedCollectionStatus_CaeAlChequeoCrudoDeBalance()
    {
        // Reserva vieja, nunca backfileada (DerivedCollectionStatus null): el fix NO debe esconder
        // deuda real de un dato legacy que el sistema todavia no clasifico.
        await using var context = new AppDbContext(_dbOptions);
        var startMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        context.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-DASH-LEGACY",
            Name = "Reserva legacy sin eje calculado",
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "vendedor-A",
            CreatedAt = startMonth.AddDays(2),
            TotalSale = 1000m,
            TotalCost = 600m,
            Balance = 400m,
            DerivedCollectionStatus = null,
        });
        context.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency
        {
            ReservaId = 1, Currency = Monedas.ARS, TotalSale = 1000m, ConfirmedSale = 1000m,
            TotalCost = 600m, TotalPaid = 600m, Balance = 400m,
        });
        await context.SaveChangesAsync();

        var accessor = BuildContextAccessor("admin-1", "Admin");
        var resolver = BuildResolver("admin-1");
        var service = new ReportService(context, _bnaMock.Object, resolver, accessor);

        var dto = await service.GetDashboardAsync(CancellationToken.None);

        var match = Assert.Single(dto.ReservasPendientes);
        Assert.Equal("F-DASH-LEGACY", match.NumeroReserva);
    }

    /// <summary>
    /// Fix B2 (review 2026-07-27): caso multimoneda que faltaba cubrir. Una reserva puede deber en UNA
    /// moneda y tener saldo a favor en OTRA a la vez (ej. debe USD del servicio, pero tiene un sobrepago
    /// en ARS de un cobro anterior). El eje ReservaCollectionStatus.Derive hace ganar "ConDeuda" sobre
    /// "SaldoAFavor" cuando conviven ambas (ver docstring del metodo): la reserva SI debe aparecer en el
    /// widget de Cobros Pendientes, con la fila de la moneda que debe (USD), nunca con la de credito (ARS).
    /// </summary>
    [Fact]
    public async Task Dashboard_ReservaConDeudaEnUnaMonedaYCreditoEnOtra_ApareceEnPendientesConLaMonedaQueDebe()
    {
        await using var context = new AppDbContext(_dbOptions);
        var startMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        context.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-DASH-MULTIMONEDA-MIXTO",
            Name = "Reserva con deuda USD y credito ARS",
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "vendedor-A",
            CreatedAt = startMonth.AddDays(2),
            TotalSale = 1000m,
            TotalCost = 600m,
            Balance = 0m,
            // El motor real (ReservaCollectionStatus.Derive) hace ganar "ConDeuda" apenas ALGUNA moneda
            // tiene Balance > Epsilon, aunque otra moneda este en saldo a favor. Se simula ese resultado
            // ya calculado (mismo patron de seed que los tests hermanos de arriba).
            DerivedCollectionStatus = ReservaCollectionStatus.WithDebt,
        });
        context.ReservaMoneyByCurrency.AddRange(
            // USD: debe 200 (el operador todavia no cobro el saldo del servicio en dolares).
            new ReservaMoneyByCurrency
            {
                ReservaId = 1, Currency = Monedas.USD, TotalSale = 200m, ConfirmedSale = 200m,
                TotalCost = 150m, TotalPaid = 0m, Balance = 200m,
            },
            // ARS: saldo a favor de 500 (el cliente pago de mas en un cobro anterior en pesos).
            new ReservaMoneyByCurrency
            {
                ReservaId = 1, Currency = Monedas.ARS, TotalSale = 1000m, ConfirmedSale = 1000m,
                TotalCost = 600m, TotalPaid = 1500m, Balance = -500m,
            });
        await context.SaveChangesAsync();

        var accessor = BuildContextAccessor("admin-1", "Admin");
        var resolver = BuildResolver("admin-1");
        var service = new ReportService(context, _bnaMock.Object, resolver, accessor);

        var dto = await service.GetDashboardAsync(CancellationToken.None);

        // Aparece UNA sola fila (la de USD, que es la que realmente debe). La fila ARS con saldo a
        // favor (Balance < 0) no pasa el filtro "row.Balance > 0" del widget, como corresponde: un
        // saldo a favor no es plata pendiente de cobrar.
        var match = Assert.Single(dto.ReservasPendientes);
        Assert.Equal("F-DASH-MULTIMONEDA-MIXTO", match.NumeroReserva);
        Assert.Equal(Monedas.USD, match.Currency);
        Assert.Equal(200m, match.Balance);
    }

    /// <summary>
    /// Fix B2 (review 2026-07-27): cruce vendedor x "ConDeuda" que faltaba cubrir. Los tests de H15
    /// existentes corrian todos como Admin (bypass total de permisos); ninguno probaba que el filtro por
    /// DerivedCollectionStatus.WithDebt SIGUE respetando el recorte "solo mis reservas" cuando el usuario
    /// NO tiene reservas.view_all. Dos reservas "ConDeuda" de vendedores distintos: cada uno debe ver
    /// SOLO la suya en el widget.
    /// </summary>
    [Fact]
    public async Task Dashboard_VendedorSinViewAll_SoloVeSusPropiasReservasConDeudaEnPendientes()
    {
        await using var context = new AppDbContext(_dbOptions);
        var startMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        context.Reservas.AddRange(
            new Reserva
            {
                Id = 1,
                NumeroReserva = "F-DASH-DEUDA-MIA",
                Name = "Reserva con deuda del vendedor A",
                Status = EstadoReserva.Confirmed,
                ResponsibleUserId = "vendedor-A",
                CreatedAt = startMonth.AddDays(2),
                TotalSale = 1000m,
                TotalCost = 600m,
                Balance = 400m,
                DerivedCollectionStatus = ReservaCollectionStatus.WithDebt,
            },
            new Reserva
            {
                Id = 2,
                NumeroReserva = "F-DASH-DEUDA-AJENA",
                Name = "Reserva con deuda del vendedor B",
                Status = EstadoReserva.Confirmed,
                ResponsibleUserId = "vendedor-B",
                CreatedAt = startMonth.AddDays(2),
                TotalSale = 2000m,
                TotalCost = 1200m,
                Balance = 800m,
                DerivedCollectionStatus = ReservaCollectionStatus.WithDebt,
            });
        context.ReservaMoneyByCurrency.AddRange(
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = Monedas.ARS, TotalSale = 1000m, ConfirmedSale = 1000m, TotalCost = 600m, TotalPaid = 600m, Balance = 400m },
            new ReservaMoneyByCurrency { ReservaId = 2, Currency = Monedas.ARS, TotalSale = 2000m, ConfirmedSale = 2000m, TotalCost = 1200m, TotalPaid = 1200m, Balance = 800m });
        await context.SaveChangesAsync();

        var accessor = BuildContextAccessor("vendedor-A", "Vendedor");
        var resolver = BuildResolver("vendedor-A", Permissions.ReportesView); // sin reservas.view_all

        var service = new ReportService(context, _bnaMock.Object, resolver, accessor);
        var dto = await service.GetDashboardAsync(CancellationToken.None);

        // Solo la deuda propia; la reserva ajena con deuda queda afuera aunque tambien sea "ConDeuda".
        var match = Assert.Single(dto.ReservasPendientes);
        Assert.Equal("F-DASH-DEUDA-MIA", match.NumeroReserva);
    }

    // ============================================================================================
    // ADR-021 Capa 6 (multimoneda) — REGRESION de fuga de costo POR MONEDA en el dashboard.
    //
    // El gap que marcaron los reviewers: los escalares CostosDelMes/MargenBruto/PagosProveedores ya
    // estaban enmascarados, pero faltaba pinear que los desgloses POR MONEDA del dashboard
    // (PorMoneda.CostosDelMes / PagosProveedores / CuentasPorPagar) tambien queden VACIOS sin
    // cobranzas.see_cost. Sin esto, un usuario sin permiso podria ver el costo/deuda de proveedor de
    // una moneda aunque el escalar mostrara 0 — fuga critica.
    // ============================================================================================

    /// <summary>
    /// Siembra datos multimoneda del MES en curso para el dashboard: una reserva con servicio ARS y
    /// USD (tabla hija ReservaMoneyByCurrency, fuente de CostosDelMes/CuentasPorPagar por moneda), un
    /// pago a proveedor en cada moneda (SupplierPayments, fuente de PagosProveedores por moneda) y
    /// saldo de proveedor en cada moneda (SupplierBalanceByCurrency, fuente de CuentasPorPagar).
    /// </summary>
    private static async Task SeedMultiCurrencyAsync(AppDbContext context)
    {
        var startMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var thisMonth = startMonth.AddDays(2);

        context.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-DASH-MC-0001",
            Name = "Reserva multimoneda",
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "vendedor-A",
            CreatedAt = thisMonth,
            // Escalares surrogate (no relevantes para los desgloses por moneda).
            TotalSale = 1200m,
            TotalCost = 750m,
            Balance = 1200m
        });
        await context.SaveChangesAsync();

        // Tabla hija: venta/costo por moneda (CostosDelMes por moneda).
        context.ReservaMoneyByCurrency.AddRange(
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = Monedas.ARS, TotalSale = 1000m, ConfirmedSale = 1000m, TotalCost = 600m, TotalPaid = 0m, Balance = 1000m },
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = Monedas.USD, TotalSale = 200m, ConfirmedSale = 200m, TotalCost = 150m, TotalPaid = 0m, Balance = 200m });

        // Pagos a proveedor del mes en cada moneda real (PagosProveedores por moneda).
        context.SupplierPayments.AddRange(
            new SupplierPayment { Amount = 300m, Currency = Monedas.ARS, PaidAt = thisMonth },
            new SupplierPayment { Amount = 50m, Currency = Monedas.USD, PaidAt = thisMonth });

        // Deuda a proveedor por moneda (CuentasPorPagar por moneda).
        context.SupplierBalanceByCurrency.AddRange(
            new SupplierBalanceByCurrency { SupplierId = 1, Currency = Monedas.ARS, ConfirmedPurchases = 600m, TotalPaid = 300m, Balance = 300m },
            new SupplierBalanceByCurrency { SupplierId = 1, Currency = Monedas.USD, ConfirmedPurchases = 150m, TotalPaid = 50m, Balance = 100m });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Dashboard_ByCurrency_WithoutSeeCost_MasksCostAndPayablesPerCurrency()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedMultiCurrencyAsync(context);

        var accessor = BuildContextAccessor("vendedor-A", "Vendedor");
        var resolver = BuildResolver("vendedor-A", Permissions.ReportesView); // sin see_cost

        var service = new ReportService(context, _bnaMock.Object, resolver, accessor);
        var dto = await service.GetDashboardAsync(CancellationToken.None);

        // Sin permiso de costos: los desgloses POR MONEDA de costo/deuda quedan VACIOS (no filtran USD).
        Assert.Empty(dto.PorMoneda.CostosDelMes);
        Assert.Empty(dto.PorMoneda.PagosProveedores);
        Assert.Empty(dto.PorMoneda.CuentasPorPagar);
        // El margen bruto por moneda revelaria el costo indirectamente (venta - margen = costo), asi que
        // se enmascara igual que CostosDelMes.
        Assert.Empty(dto.PorMoneda.MargenBruto);

        // Lo que NO es costo sigue presente por moneda (cobros/ventas/saldo del cliente).
        Assert.NotEmpty(dto.PorMoneda.VentasDelMes);
        Assert.NotEmpty(dto.PorMoneda.SaldoPendiente);
    }

    [Fact]
    public async Task Dashboard_ByCurrency_WithSeeCost_ShowsCostAndPayablesPerCurrency()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedMultiCurrencyAsync(context);

        var accessor = BuildContextAccessor("colaborador-1", "Colaborador");
        var resolver = BuildResolver("colaborador-1",
            Permissions.ReportesView, Permissions.CobranzasSeeCost, Permissions.ReservasViewAll);

        var service = new ReportService(context, _bnaMock.Object, resolver, accessor);
        var dto = await service.GetDashboardAsync(CancellationToken.None);

        // Costos del mes por moneda.
        Assert.Equal(600m, dto.PorMoneda.CostosDelMes.Single(x => x.Currency == Monedas.ARS).Amount);
        Assert.Equal(150m, dto.PorMoneda.CostosDelMes.Single(x => x.Currency == Monedas.USD).Amount);

        // Pagos a proveedor por moneda.
        Assert.Equal(300m, dto.PorMoneda.PagosProveedores.Single(x => x.Currency == Monedas.ARS).Amount);
        Assert.Equal(50m, dto.PorMoneda.PagosProveedores.Single(x => x.Currency == Monedas.USD).Amount);

        // Cuentas por pagar por moneda.
        Assert.Equal(300m, dto.PorMoneda.CuentasPorPagar.Single(x => x.Currency == Monedas.ARS).Amount);
        Assert.Equal(100m, dto.PorMoneda.CuentasPorPagar.Single(x => x.Currency == Monedas.USD).Amount);

        // Margen bruto por moneda: venta menos costo, moneda por moneda (ARS: 1000-600, USD: 200-150).
        Assert.Equal(400m, dto.PorMoneda.MargenBruto.Single(x => x.Currency == Monedas.ARS).Amount);
        Assert.Equal(50m, dto.PorMoneda.MargenBruto.Single(x => x.Currency == Monedas.USD).Amount);
    }

    // ============================================================================================
    // MargenBruto por moneda — el front necesitaba un desglose por moneda del margen (ventas - costos)
    // porque el escalar MargenBruto mezcla ARS+USD en una sola resta sin sentido (bloqueado por review,
    // regla P-3 de la constitucion: nunca mezclar montos de distinta moneda en un solo numero).
    // ============================================================================================

    [Fact]
    public async Task Dashboard_ByCurrency_MargenBruto_UsesZeroCostForCurrencyWithoutCost()
    {
        // Ventas en ARS y USD, pero costo cargado SOLO en ARS (por ejemplo, el hotel ya se pago en ARS
        // pero el pasaje vendido en USD todavia no tiene costo cargado). El margen en USD tiene que ser
        // la venta COMPLETA (costo asumido 0), nunca cero ni restado contra el costo de otra moneda.
        await using var context = new AppDbContext(_dbOptions);

        var startMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var thisMonth = startMonth.AddDays(2);

        context.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-DASH-MARGEN-0001",
            Name = "Reserva margen mixto",
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "vendedor-A",
            CreatedAt = thisMonth,
            TotalSale = 1200m,
            TotalCost = 600m,
            Balance = 1200m
        });
        await context.SaveChangesAsync();

        context.ReservaMoneyByCurrency.AddRange(
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = Monedas.ARS, TotalSale = 1000m, ConfirmedSale = 1000m, TotalCost = 600m, TotalPaid = 0m, Balance = 1000m },
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = Monedas.USD, TotalSale = 200m, ConfirmedSale = 200m, TotalCost = 0m, TotalPaid = 0m, Balance = 200m });
        await context.SaveChangesAsync();

        var accessor = BuildContextAccessor("colaborador-1", "Colaborador");
        var resolver = BuildResolver("colaborador-1",
            Permissions.ReportesView, Permissions.CobranzasSeeCost, Permissions.ReservasViewAll);

        var service = new ReportService(context, _bnaMock.Object, resolver, accessor);
        var dto = await service.GetDashboardAsync(CancellationToken.None);

        Assert.Equal(400m, dto.PorMoneda.MargenBruto.Single(x => x.Currency == Monedas.ARS).Amount); // 1000 - 600
        Assert.Equal(200m, dto.PorMoneda.MargenBruto.Single(x => x.Currency == Monedas.USD).Amount); // 200 - 0
    }

    [Fact]
    public async Task Dashboard_ByCurrency_MargenBruto_IsNegativeWhenCurrencyOnlyHasCost()
    {
        // Una moneda que solo tiene costo cargado (sin venta todavia en esa moneda) tiene que dar
        // margen NEGATIVO: mejor mostrar la perdida potencial en el dashboard que esconderla asumiendo
        // que la venta faltante vale la misma plata que el costo.
        await using var context = new AppDbContext(_dbOptions);

        var startMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var thisMonth = startMonth.AddDays(2);

        context.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-DASH-MARGEN-0002",
            Name = "Reserva costo sin venta en esa moneda",
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "vendedor-A",
            CreatedAt = thisMonth,
            TotalSale = 1000m,
            TotalCost = 680m,
            Balance = 1000m
        });
        await context.SaveChangesAsync();

        context.ReservaMoneyByCurrency.AddRange(
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = Monedas.ARS, TotalSale = 1000m, ConfirmedSale = 1000m, TotalCost = 600m, TotalPaid = 0m, Balance = 1000m },
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = Monedas.USD, TotalSale = 0m, ConfirmedSale = 0m, TotalCost = 80m, TotalPaid = 0m, Balance = 0m });
        await context.SaveChangesAsync();

        var accessor = BuildContextAccessor("colaborador-1", "Colaborador");
        var resolver = BuildResolver("colaborador-1",
            Permissions.ReportesView, Permissions.CobranzasSeeCost, Permissions.ReservasViewAll);

        var service = new ReportService(context, _bnaMock.Object, resolver, accessor);
        var dto = await service.GetDashboardAsync(CancellationToken.None);

        Assert.Equal(-80m, dto.PorMoneda.MargenBruto.Single(x => x.Currency == Monedas.USD).Amount);
    }
}
