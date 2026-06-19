using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-035 (2026-06-19): la worklist de cobranza expone la MONEDA de cada reserva (MonedaPrincipal + PorMoneda)
/// para que el modal de cobro NO mande el pago sin moneda. Estos tests verifican que el campo nuevo llega bien
/// poblado: reserva en USD -> "USD", en ARS -> "ARS", multimoneda -> la moneda de mayor saldo pendiente.
///
/// <para>La moneda real por reserva vive en la tabla materializada ReservaMoneyByCurrency (proyeccion de
/// ADR-021); aca la sembramos directo, que es lo que la worklist lee en batch.</para>
/// </summary>
public class PaymentServiceWorklistCurrencyTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsServiceMock;

    public PaymentServiceWorklistCurrencyTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _mapper = new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();

        _settingsServiceMock = new Mock<IOperationalFinanceSettingsService>();
        _settingsServiceMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
    }

    private static IHttpContextAccessor BuildAdminAccessor()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "admin-1"),
            new(ClaimTypes.Role, "Admin")
        };
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        return new HttpContextAccessor { HttpContext = ctx };
    }

    private static IUserPermissionResolver BuildAdminResolver()
    {
        // Admin: el resolver puede devolver vacio, el bypass por rol lo cubre el OwnerScope.
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>();
        mock.Setup(r => r.GetPermissionsAsync("admin-1", It.IsAny<CancellationToken>())).ReturnsAsync(set);
        return mock.Object;
    }

    private PaymentService BuildService(AppDbContext context)
        => new(context, new EntityReferenceResolver(context), _mapper, _settingsServiceMock.Object,
               NullLogger<PaymentService>.Instance, BuildAdminResolver(), BuildAdminAccessor());

    /// <summary>
    /// Crea una reserva con deuda cobrable + sus filas de saldo por moneda (la proyeccion ADR-021 que la
    /// worklist lee). El Balance escalar de la reserva refleja la deuda total (semaforo > 0 para que entre).
    /// </summary>
    private static void SeedReservaConMoneda(
        AppDbContext context, int id, string numero, decimal balanceEscalar,
        params (string Currency, decimal ConfirmedSale, decimal TotalPaid)[] lineas)
    {
        context.Reservas.Add(new Reserva
        {
            Id = id,
            NumeroReserva = numero,
            Name = numero,
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "vendedor-A",
            TotalSale = lineas.Sum(l => l.ConfirmedSale),
            TotalPaid = lineas.Sum(l => l.TotalPaid),
            Balance = balanceEscalar,
            StartDate = DateTime.UtcNow.AddDays(10)
        });

        foreach (var linea in lineas)
        {
            context.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency
            {
                ReservaId = id,
                Currency = linea.Currency,
                TotalSale = linea.ConfirmedSale,
                ConfirmedSale = linea.ConfirmedSale,
                TotalCost = 0m,
                TotalPaid = linea.TotalPaid,
                Balance = linea.ConfirmedSale - linea.TotalPaid
            });
        }
    }

    [Fact]
    public async Task Worklist_ReservaEnUsd_MonedaPrincipalEsUsd()
    {
        await using var context = new AppDbContext(_dbOptions);
        SeedReservaConMoneda(context, id: 1, numero: "F-USD-0001", balanceEscalar: 500m,
            ("USD", ConfirmedSale: 800m, TotalPaid: 300m));
        await context.SaveChangesAsync();

        var page = await BuildService(context)
            .GetCollectionsWorklistAsync(new CollectionWorklistQuery(), CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal("USD", item.MonedaPrincipal);
        var linea = Assert.Single(item.PorMoneda);
        Assert.Equal("USD", linea.Currency);
        Assert.Equal(500m, linea.Balance);
    }

    [Fact]
    public async Task Worklist_ReservaEnArs_MonedaPrincipalEsArs()
    {
        await using var context = new AppDbContext(_dbOptions);
        SeedReservaConMoneda(context, id: 1, numero: "F-ARS-0001", balanceEscalar: 900m,
            ("ARS", ConfirmedSale: 1000m, TotalPaid: 100m));
        await context.SaveChangesAsync();

        var page = await BuildService(context)
            .GetCollectionsWorklistAsync(new CollectionWorklistQuery(), CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal("ARS", item.MonedaPrincipal);
    }

    [Fact]
    public async Task Worklist_Multimoneda_MonedaPrincipalEsLaDeMayorSaldoPendiente()
    {
        await using var context = new AppDbContext(_dbOptions);
        // ARS debe 200, USD debe 700 -> principal = USD (mayor deuda), aunque "ARS" venga primero alfabetico.
        SeedReservaConMoneda(context, id: 1, numero: "F-MIX-0001", balanceEscalar: 900m,
            ("ARS", ConfirmedSale: 500m, TotalPaid: 300m),
            ("USD", ConfirmedSale: 1000m, TotalPaid: 300m));
        await context.SaveChangesAsync();

        var page = await BuildService(context)
            .GetCollectionsWorklistAsync(new CollectionWorklistQuery(), CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal("USD", item.MonedaPrincipal);
        Assert.Equal(2, item.PorMoneda.Count);
        // PorMoneda ordenado alfabeticamente: ARS primero, USD segundo.
        Assert.Equal("ARS", item.PorMoneda[0].Currency);
        Assert.Equal("USD", item.PorMoneda[1].Currency);
    }

    [Fact]
    public async Task Worklist_ReservaSinDetallePorMoneda_MonedaPrincipalNullYNoRompe()
    {
        // Reserva legacy sin filas en ReservaMoneyByCurrency: no debe inventar moneda ni tirar.
        await using var context = new AppDbContext(_dbOptions);
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-LEG-0001",
            Name = "Legacy",
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "vendedor-A",
            TotalSale = 1000m,
            TotalPaid = 100m,
            Balance = 900m,
            StartDate = DateTime.UtcNow.AddDays(10)
        });
        await context.SaveChangesAsync();

        var page = await BuildService(context)
            .GetCollectionsWorklistAsync(new CollectionWorklistQuery(), CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Null(item.MonedaPrincipal);
        Assert.Empty(item.PorMoneda);
    }
}
