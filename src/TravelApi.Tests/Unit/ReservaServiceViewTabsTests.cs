using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// FIX #37/#38 (Tanda 3 del barrido de PROD, 2026-07-23): antes de este fix, una reserva en
/// <see cref="EstadoReserva.PendingOperatorRefund"/> ("Esperando reembolso") no caia en NINGUNA
/// pestaña del listado — <c>ApplyReservaView</c> tenia un <c>default</c> mudo que agrupaba
/// cualquier view no reconocido bajo "active". Este archivo cubre:
///  - el invariante "toda reserva, en cualquier estado, aparece en al menos una pestaña" (el
///    test que hubiera pintado el bug original antes de que llegara a produccion);
///  - que el contador del summary coincida con lo que trae la pestaña (contador==tabla) para
///    "closed"/"cancelled"/"archived", que es justo lo que se rompio;
///  - los alias legacy "reserved"/"operative" y la pestaña nueva "all".
/// </summary>
public class ReservaServiceViewTabsTests
{
    private readonly Mock<IMapper> _mapperMock = new();
    private readonly Mock<IOperationalFinanceSettingsService> _settingsServiceMock = new();

    public ReservaServiceViewTabsTests()
    {
        _settingsServiceMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
    }

    private static DbContextOptions<AppDbContext> NewDbOptions()
        => new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    // Siempre corremos como Admin: estos tests miden el filtro de pestaña (ApplyReservaView),
    // no el recorte por permisos (eso ya lo cubre ReservaServiceFilteringTests).
    private static IHttpContextAccessor BuildContextAccessor()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "admin-1"),
            new(ClaimTypes.Role, "Admin")
        };
        var identity = new ClaimsIdentity(claims, authenticationType: "Test");
        var principal = new ClaimsPrincipal(identity);
        var ctx = new DefaultHttpContext { User = principal };
        return new HttpContextAccessor { HttpContext = ctx };
    }

    private static IUserPermissionResolver BuildResolver()
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>();
        mock.Setup(r => r.GetPermissionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(set);
        return mock.Object;
    }

    private ReservaService BuildService(AppDbContext context)
        => new(context, _mapperMock.Object, _settingsServiceMock.Object, BuildUserManager(),
               NullLogger<ReservaService>.Instance, BuildResolver(), BuildContextAccessor());

    /// <summary>
    /// Todos los literales publicos de <see cref="EstadoReserva"/> (misma reflexion que
    /// <c>EstadoReservaCoverageTests</c>) mas el legacy "Archived" deben caer en AL MENOS una
    /// de las pestañas fijas del listado. Si alguien agrega un estado nuevo y se olvida de darle
    /// una rama en <c>ApplyReservaView</c>, este test lo detecta ANTES de que quede invisible en
    /// produccion (el bug #37/#38 original).
    /// </summary>
    [Fact]
    public async Task Every_known_status_appears_in_at_least_one_tab()
    {
        var statusValues = typeof(EstadoReserva)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)
            .Append("Archived")
            .Distinct()
            .ToArray();

        var tabViews = new[]
        {
            "quotation", "budget", "in-management", "confirmed", "traveling",
            "closed", "cancelled", "lost", "archived"
        };

        foreach (var status in statusValues)
        {
            var options = NewDbOptions();
            await using (var seedCtx = new AppDbContext(options))
            {
                seedCtx.Reservas.Add(new Reserva
                {
                    NumeroReserva = $"F-{status}",
                    Name = $"Reserva {status}",
                    Status = status
                });
                await seedCtx.SaveChangesAsync();
            }

            var foundInAnyTab = false;
            foreach (var view in tabViews)
            {
                await using var readCtx = new AppDbContext(options);
                var service = BuildService(readCtx);
                var page = await service.GetReservasAsync(new ReservaListQuery { View = view }, CancellationToken.None);
                if (page.TotalCount > 0)
                {
                    foundInAnyTab = true;
                    break;
                }
            }

            Assert.True(foundInAnyTab, $"El estado '{status}' no aparece en ninguna pestaña del listado.");
        }
    }

    [Fact]
    public async Task Cancelled_tab_includes_cancelled_and_pending_operator_refund()
    {
        var options = NewDbOptions();
        await using (var ctx = new AppDbContext(options))
        {
            ctx.Reservas.Add(new Reserva { NumeroReserva = "F-1", Name = "A", Status = EstadoReserva.Cancelled });
            ctx.Reservas.Add(new Reserva { NumeroReserva = "F-2", Name = "B", Status = EstadoReserva.PendingOperatorRefund });
            ctx.Reservas.Add(new Reserva { NumeroReserva = "F-3", Name = "C", Status = EstadoReserva.Closed });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = new AppDbContext(options);
        var service = BuildService(readCtx);
        var page = await service.GetReservasAsync(new ReservaListQuery { View = "cancelled" }, CancellationToken.None);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(2, page.Summary.CancelledCount);
        Assert.All(page.Items, r =>
            Assert.True(r.Status == EstadoReserva.Cancelled || r.Status == EstadoReserva.PendingOperatorRefund));
    }

    [Fact]
    public async Task Closed_tab_and_count_only_include_closed_not_cancelled_or_archived()
    {
        var options = NewDbOptions();
        await using (var ctx = new AppDbContext(options))
        {
            ctx.Reservas.Add(new Reserva { NumeroReserva = "F-1", Name = "A", Status = EstadoReserva.Closed });
            ctx.Reservas.Add(new Reserva { NumeroReserva = "F-2", Name = "B", Status = EstadoReserva.Cancelled });
            ctx.Reservas.Add(new Reserva { NumeroReserva = "F-3", Name = "C", Status = "Archived" });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = new AppDbContext(options);
        var service = BuildService(readCtx);
        var page = await service.GetReservasAsync(new ReservaListQuery { View = "closed" }, CancellationToken.None);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(1, page.Summary.ClosedCount);
        Assert.Equal(EstadoReserva.Closed, page.Items.Single().Status);
    }

    [Fact]
    public async Task Archived_tab_and_count_match()
    {
        var options = NewDbOptions();
        await using (var ctx = new AppDbContext(options))
        {
            ctx.Reservas.Add(new Reserva { NumeroReserva = "F-1", Name = "A", Status = "Archived" });
            ctx.Reservas.Add(new Reserva { NumeroReserva = "F-2", Name = "B", Status = EstadoReserva.Closed });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = new AppDbContext(options);
        var service = BuildService(readCtx);
        var page = await service.GetReservasAsync(new ReservaListQuery { View = "archived" }, CancellationToken.None);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(1, page.Summary.ArchivedCount);
    }

    [Theory]
    [InlineData("reserved", EstadoReserva.Confirmed)]
    [InlineData("operative", EstadoReserva.Traveling)]
    public async Task Legacy_view_aliases_map_to_the_new_status(string legacyView, string expectedStatus)
    {
        var options = NewDbOptions();
        await using (var ctx = new AppDbContext(options))
        {
            ctx.Reservas.Add(new Reserva { NumeroReserva = "F-1", Name = "A", Status = expectedStatus });
            ctx.Reservas.Add(new Reserva { NumeroReserva = "F-2", Name = "B", Status = EstadoReserva.Budget });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = new AppDbContext(options);
        var service = BuildService(readCtx);
        var page = await service.GetReservasAsync(new ReservaListQuery { View = legacyView }, CancellationToken.None);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(expectedStatus, page.Items.Single().Status);
    }

    [Fact]
    public async Task All_view_returns_every_status_without_filtering()
    {
        var options = NewDbOptions();
        await using (var ctx = new AppDbContext(options))
        {
            ctx.Reservas.Add(new Reserva { NumeroReserva = "F-1", Name = "A", Status = EstadoReserva.Budget });
            ctx.Reservas.Add(new Reserva { NumeroReserva = "F-2", Name = "B", Status = EstadoReserva.Closed });
            ctx.Reservas.Add(new Reserva { NumeroReserva = "F-3", Name = "C", Status = EstadoReserva.Cancelled });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = new AppDbContext(options);
        var service = BuildService(readCtx);
        var page = await service.GetReservasAsync(new ReservaListQuery { View = "all" }, CancellationToken.None);

        Assert.Equal(3, page.TotalCount);
    }

    [Fact]
    public async Task Unknown_view_falls_back_to_active_without_throwing()
    {
        var options = NewDbOptions();
        await using (var ctx = new AppDbContext(options))
        {
            ctx.Reservas.Add(new Reserva { NumeroReserva = "F-1", Name = "A", Status = EstadoReserva.InManagement });
            ctx.Reservas.Add(new Reserva { NumeroReserva = "F-2", Name = "B", Status = EstadoReserva.Budget });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = new AppDbContext(options);
        var service = BuildService(readCtx);
        var page = await service.GetReservasAsync(new ReservaListQuery { View = "to-settle" }, CancellationToken.None);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(EstadoReserva.InManagement, page.Items.Single().Status);
    }

    /// <summary>
    /// Barrido T5 (2026-07-24, item #7 + retoque del reviewer): "Pagadas" (settled) y "Con deuda
    /// vencida" (overdue) del listado de Cobranza y Facturacion no tenian rama propia en
    /// <c>ApplyReservaView</c> — el front ya mandaba esas claves (STATUS_FILTER_OPTIONS de
    /// PaymentsByReservaPage.jsx) pero caian en el <c>default</c> mudo y devolvian EXACTAMENTE lo
    /// mismo que "Activas" (filtro FANTASMA: el vendedor elegia "Pagadas" y veia la lista de siempre).
    /// Estos tests blindan que ahora "settled"/"overdue" filtran de verdad y DIFIEREN de "active".
    /// </summary>
    [Fact]
    public async Task Settled_view_returns_only_sale_firm_reservas_with_settled_collection_status()
    {
        var options = NewDbOptions();
        await using (var ctx = new AppDbContext(options))
        {
            // Venta firme (Confirmed) + Saldado -> DEBE aparecer en "Pagadas".
            ctx.Reservas.Add(new Reserva
            {
                NumeroReserva = "F-SETTLED-OK", Name = "Pagada de verdad", Status = EstadoReserva.Confirmed,
                DerivedCollectionStatus = "Saldado",
            });
            // Venta firme (Confirmed) pero CON deuda -> NO debe aparecer en "Pagadas".
            ctx.Reservas.Add(new Reserva
            {
                NumeroReserva = "F-SETTLED-DEBT", Name = "Con deuda", Status = EstadoReserva.Confirmed,
                DerivedCollectionStatus = "ConDeuda",
            });
            // Saldado pero En Viaje (Traveling NO es venta firme cobrable, ver EstadoReserva.SaleFirmStatuses)
            // -> NO debe aparecer en "Pagadas" aunque su eje de cobro diga Saldado.
            ctx.Reservas.Add(new Reserva
            {
                NumeroReserva = "F-SETTLED-TRAVELING", Name = "En viaje saldada", Status = EstadoReserva.Traveling,
                DerivedCollectionStatus = "Saldado",
            });
            // Saldado pero Anulada -> NO debe aparecer (la plata de una anulada se resuelve por el
            // circuito de cancelacion, no por "Pagadas").
            ctx.Reservas.Add(new Reserva
            {
                NumeroReserva = "F-SETTLED-CANCELLED", Name = "Anulada saldada", Status = EstadoReserva.Cancelled,
                DerivedCollectionStatus = "Saldado",
            });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = new AppDbContext(options);
        var service = BuildService(readCtx);

        var settledPage = await service.GetReservasAsync(new ReservaListQuery { View = "settled" }, CancellationToken.None);
        var activePage = await service.GetReservasAsync(new ReservaListQuery { View = "active" }, CancellationToken.None);

        Assert.Equal(1, settledPage.TotalCount);
        Assert.Equal("F-SETTLED-OK", settledPage.Items.Single().NumeroReserva);

        // La prueba central del fix: antes "settled" era un ALIAS FANTASMA de "active" (mismo resultado
        // siempre). Aca "active" trae las 3 reservas vivas (OK Confirmed-saldada + DEBT Confirmed-con-deuda
        // + TRAVELING En-viaje), "settled" trae SOLO la saldada de verdad (1) -> los sets tienen que ser
        // DISTINTOS.
        Assert.NotEqual(activePage.TotalCount, settledPage.TotalCount);
    }

    [Fact]
    public async Task Overdue_view_returns_only_finished_trips_with_a_real_positive_balance()
    {
        var yesterday = DateTime.UtcNow.Date.AddDays(-1);
        var tomorrow = DateTime.UtcNow.Date.AddDays(1);

        var options = NewDbOptions();
        await using (var ctx = new AppDbContext(options))
        {
            // Venta firme (Confirmed) + viaje ya terminado + debe -> DEBE aparecer en "Con deuda vencida".
            ctx.Reservas.Add(new Reserva
            {
                NumeroReserva = "F-OVERDUE-OK", Name = "Vencida de verdad", Status = EstadoReserva.Confirmed,
                EndDate = yesterday, Balance = 100m,
            });
            // Venta firme + viaje ya terminado, pero SIN deuda (Balance 0) -> NO debe aparecer.
            ctx.Reservas.Add(new Reserva
            {
                NumeroReserva = "F-OVERDUE-NODEBT", Name = "Terminada sin deuda", Status = EstadoReserva.Confirmed,
                EndDate = yesterday, Balance = 0m,
            });
            // Venta firme + debe, pero el viaje TODAVIA no arranco -> NO debe aparecer (no esta vencida).
            ctx.Reservas.Add(new Reserva
            {
                NumeroReserva = "F-OVERDUE-FUTURE", Name = "Confirmada futura con deuda", Status = EstadoReserva.Confirmed,
                EndDate = tomorrow, Balance = 100m,
            });
            // En viaje (Traveling) con deuda y fecha vencida -> NO debe aparecer: en prepago puro una
            // reserva jamas entra a Traveling debiendo (ADR-036), no es venta firme cobrable.
            ctx.Reservas.Add(new Reserva
            {
                NumeroReserva = "F-OVERDUE-TRAVELING", Name = "En viaje con deuda", Status = EstadoReserva.Traveling,
                EndDate = yesterday, Balance = 100m,
            });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = new AppDbContext(options);
        var service = BuildService(readCtx);

        var overduePage = await service.GetReservasAsync(new ReservaListQuery { View = "overdue" }, CancellationToken.None);
        var activePage = await service.GetReservasAsync(new ReservaListQuery { View = "active" }, CancellationToken.None);

        Assert.Equal(1, overduePage.TotalCount);
        Assert.Equal("F-OVERDUE-OK", overduePage.Items.Single().NumeroReserva);

        // Mismo criterio que el test de "settled": antes "overdue" tambien era un alias fantasma de
        // "active". Aca "active" trae las 4 reservas vivas (3 Confirmed + 1 Traveling), "overdue" trae
        // SOLO la vencida de verdad (1) -> tienen que diferir.
        Assert.NotEqual(activePage.TotalCount, overduePage.TotalCount);
    }
}
